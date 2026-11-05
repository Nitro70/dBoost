using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DBoost.App.Services;
using DBoost.App.ViewModels;

namespace DBoost.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel vm;
    private readonly AppSettings settings;
    private Point dragFrom;

    /// <summary>False for the offscreen screenshot mode, which must never touch the user's settings.</summary>
    public bool PersistSettings { get; init; } = true;
    private FileItem? dragItem;

    public MainWindow(MainViewModel vm, AppSettings settings)
    {
        this.vm = vm;
        this.settings = settings;
        InitializeComponent();
        DataContext = vm;
        RestorePlacement();

        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        // The overlay covers everything while a drag is over the window, so leaving it means leaving the window.
        DragLeave += (_, e) => { if (e.OriginalSource == DropSurface) DropOverlay.Visibility = Visibility.Collapsed; };
        Drop += OnDrop;
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += (_, _) => SaveState();
        vm.BatchFinished += () => { if (!IsActive) Native.FlashUntilFocused(this); };

        FileList.PreviewMouseLeftButtonDown += FileList_PreviewMouseLeftButtonDown;
        FileList.PreviewMouseMove += FileList_PreviewMouseMove;
        FileList.KeyDown += FileList_KeyDown;
    }

    // ------------------------------------------------------------ drop files in

    private static bool HasFiles(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (HasFiles(e) && !vm.IsBusy && dragItem is null) DropOverlay.Visibility = Visibility.Visible;
        OnDragOver(sender, e);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasFiles(e) && !vm.IsBusy && dragItem is null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (vm.IsBusy || dragItem is not null) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) vm.AddPaths(paths);
        e.Handled = true;
    }

    // ------------------------------------------------------------ drag finished files out (into Explorer, chat apps...)

    private void FileList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        dragFrom = e.GetPosition(this);
        dragItem = (e.OriginalSource as FrameworkElement)?.DataContext as FileItem;
        if (dragItem is { IsDone: false }) dragItem = null;
    }

    private void FileList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (dragItem is null || e.LeftButton != MouseButtonState.Pressed) { dragItem = null; return; }
        var d = e.GetPosition(this) - dragFrom;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = dragItem;
        if (item.OutputPath is { } output && File.Exists(output))
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { output });
            try { DragDrop.DoDragDrop(FileList, data, DragDropEffects.Copy); }
            finally { dragItem = null; }
        }
        dragItem = null;
    }

    // ------------------------------------------------------------ keys

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.FocusedElement is not TextBox)
        {
            if (Clipboard.ContainsFileDropList() && !vm.IsBusy)
            {
                var list = Clipboard.GetFileDropList();
                vm.AddPaths(list.Cast<string>().ToArray());
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Space && vm.Advanced
                 && Keyboard.FocusedElement is not (TextBox or System.Windows.Controls.Primitives.ButtonBase or ComboBox or Slider))
        {
            // Space toggles the preview, like a media player.
            if (vm.IsPreviewPlaying || vm.IsPreviewRendering) vm.StopPreview();
            else if (vm.PreviewCommand.CanExecute(null)) vm.PreviewCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void FileList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            vm.RemoveSelectedCommand.Execute(FileList.SelectedItems);
            e.Handled = true;
        }
    }

    private void TrackPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (vm.SelectedFile is { } f) vm.OnWaveformTrackChanged(f);
    }

    // ------------------------------------------------------------ menu

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        MoreMenu.PlacementTarget = MenuButton;
        MoreMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        MoreMenu.IsOpen = true;
    }

    private void MoreMenu_Opened(object sender, RoutedEventArgs e)
    {
        ContextMenuToggle.IsChecked = SafeCheck(ShellIntegration.IsContextMenuInstalled);
        SendToToggle.IsChecked = SafeCheck(() => ShellIntegration.IsSendToInstalled());
    }

    private static bool SafeCheck(Func<bool> f)
    {
        try { return f(); } catch (Exception) { return false; }
    }

    private void ContextMenuToggle_Click(object sender, RoutedEventArgs e)
    {
        if (Environment.ProcessPath is not { } exe) return;
        try
        {
            if (ContextMenuToggle.IsChecked) ShellIntegration.InstallContextMenu(exe);
            else ShellIntegration.RemoveContextMenu();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not change the Explorer menu: {ex.Message}", "dBoost", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SendToToggle_Click(object sender, RoutedEventArgs e)
    {
        if (Environment.ProcessPath is not { } exe) return;
        try
        {
            if (SendToToggle.IsChecked) ShellIntegration.InstallSendTo(exe);
            else ShellIntegration.RemoveSendTo();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not change the Send to menu: {ex.Message}", "dBoost", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        string version = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        int plus = version.IndexOf('+');
        if (plus > 0) version = version[..plus];
        string ffmpeg = EngineHost.FfmpegPath ?? "not started yet";
        MessageBox.Show(this,
            $"dBoost {version}\n\nMake audio and video louder or quieter, one file or hundreds at once. "
            + "Your original files are never changed: dBoost always saves a new copy.\n\n"
            + $"Audio is processed with FFmpeg (LGPL), which is bundled unmodified.\nffmpeg in use: {ffmpeg}\n\n"
            + "MIT licensed. Copyright (c) 2026 Nitro70.",
            "About dBoost", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ------------------------------------------------------------ window placement

    private void RestorePlacement()
    {
        if (settings.WindowWidth >= MinWidth && settings.WindowHeight >= MinHeight)
        {
            Width = settings.WindowWidth;
            Height = settings.WindowHeight;
        }
        if (double.IsFinite(settings.WindowLeft) && double.IsFinite(settings.WindowTop)
            && settings.WindowLeft >= SystemParameters.VirtualScreenLeft - 50
            && settings.WindowTop >= SystemParameters.VirtualScreenTop - 50
            && settings.WindowLeft < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100
            && settings.WindowTop < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = settings.WindowLeft;
            Top = settings.WindowTop;
        }
        if (settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void SaveState()
    {
        if (!PersistSettings) { vm.Shutdown(); return; }
        vm.SaveInto(settings);
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        settings.WindowWidth = bounds.Width;
        settings.WindowHeight = bounds.Height;
        settings.WindowLeft = bounds.Left;
        settings.WindowTop = bounds.Top;
        settings.WindowMaximized = WindowState == WindowState.Maximized;
        SettingsStore.Save(settings);
        vm.Shutdown();
    }
}
