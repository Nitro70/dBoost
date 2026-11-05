using System.IO;
using System.Windows;
using System.Windows.Threading;
using DBoost.App.Services;
using DBoost.App.ViewModels;

namespace DBoost.App;

public partial class App : Application
{
    public enum LaunchMode { Normal, Screenshot }

    public LaunchMode Mode { get; init; }
    public string[] LaunchArgs { get; init; } = [];
    public SingleInstance? Instance { get; init; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        if (Mode == LaunchMode.Screenshot)
        {
            _ = ScreenshotMode.RunAsync(this, LaunchArgs);
            return;
        }

        var settings = SettingsStore.Load();
        var vm = new MainViewModel(settings);
        Resources["Vm"] = vm; // item templates and menus bind to it
        var window = new MainWindow(vm, settings);
        MainWindow = window;
        window.Show();

        vm.AddPaths(LaunchArgs);
        if (Instance is not null)
        {
            Instance.FilesReceived += files => Dispatcher.BeginInvoke(() =>
            {
                vm.AddPaths(files);
                Native.BringToFront(window);
            });
        }

        _ = vm.WarmUpAsync();

        if (Environment.ProcessPath is { } exe)
        {
            try { ShellIntegration.RepairIfMoved(exe); } catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(EngineHost.DataFolder);
            File.AppendAllText(Path.Combine(EngineHost.DataFolder, "error.log"), $"{DateTime.Now:u}\n{e.Exception}\n\n");
        }
        catch (IOException) { }

        if (Mode == LaunchMode.Screenshot)
        {
            Console.Error.WriteLine(e.Exception);
            e.Handled = true;
            Shutdown(1);
            return;
        }

        MessageBox.Show(MainWindow, $"Something went wrong:\n\n{e.Exception.Message}\n\nThe details were saved to {Path.Combine(EngineHost.DataFolder, "error.log")}.",
            "dBoost", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
