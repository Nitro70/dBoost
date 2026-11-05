using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DBoost.App.Services;
using DBoost.App.ViewModels;
using DBoost.Core;

namespace DBoost.App;

/// <summary>
/// <c>dBoost.exe --screenshot out.png [options] [files...]</c> builds the real window, fills it, and renders
/// it to a PNG without ever showing it. Used for the README pictures and to check the layout without
/// popping windows up. Binding errors are printed to stderr so broken XAML shows up.
///
/// Options: --theme light|dark, --advanced, --size 1080x740, --gain 8, --select N, --part 1.5-3:+8,
/// --run (actually boost the files into a temp folder first), --scale 1.5.
/// </summary>
public static class ScreenshotMode
{
    private sealed class BindingErrors : TraceListener
    {
        public readonly StringBuilder Text = new();
        public override void Write(string? message) => Text.Append(message);
        public override void WriteLine(string? message) => Text.AppendLine(message);
    }

    public static async Task RunAsync(App app, string[] args)
    {
        int code = 0;
        try
        {
            code = await RenderAsync(app, args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            code = 1;
        }
        app.Shutdown(code);
    }

    private static async Task<int> RenderAsync(App app, string[] args)
    {
        if (args.Length == 0) throw new ArgumentException("--screenshot needs an output .png path");
        string output = Path.GetFullPath(args[0]);
        string? theme = null;
        bool advanced = false, run = false;
        double width = 1080, height = 740, gain = 6, scale = 1.5;
        int select = 0;
        var parts = new List<(double, double, double)>();
        var files = new List<string>();

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--theme": theme = args[++i]; break;
                case "--advanced": advanced = true; break;
                case "--run": run = true; break;
                case "--gain": gain = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--select": select = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--scale": scale = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--size":
                    var wh = args[++i].Split('x');
                    width = double.Parse(wh[0], CultureInfo.InvariantCulture);
                    height = double.Parse(wh[1], CultureInfo.InvariantCulture);
                    break;
                case "--part":
                    // 1.5-3:+8
                    var p = args[++i].Split(':');
                    var range = p[0].Split('-');
                    parts.Add((double.Parse(range[0], CultureInfo.InvariantCulture), double.Parse(range[1], CultureInfo.InvariantCulture),
                        Decibels.TryParse(p[1], out double g) ? g : 6));
                    break;
                default: files.Add(args[i]); break;
            }
        }

        if (theme is "light") app.ThemeMode = ThemeMode.Light;
        else if (theme is "dark") app.ThemeMode = ThemeMode.Dark;

        var errors = new BindingErrors();
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

        // --run saves next to the originals (and deletes the results again afterwards).
        var settings = new AppSettings { Advanced = advanced, GainDb = gain };
        var vm = new MainViewModel(settings);
        app.Resources["Vm"] = vm;
        var window = new MainWindow(vm, settings) { Width = width, Height = height, PersistSettings = false };

        if (files.Count > 0)
        {
            vm.AddPaths(files);
            await WaitFor(() => vm.Files.All(f => f.State != FileState.Reading && f.Thumbnail is not null || f.State == FileState.Failed), 30);
            if (vm.Files.Count > select) vm.SelectedFile = vm.Files[select];
            if (advanced && vm.SelectedFile is { } sel)
            {
                vm.EnsureWaveform(sel);
                await WaitFor(() => sel.Waveform is not null || sel.WaveformError is not null, 30);
                foreach (var (a, b, g) in parts) sel.Segments.Add(new SegmentItem(a, b, g));
                if (parts.Count > 0) sel.CursorSeconds = parts[0].Item1;
            }
            if (run)
            {
                vm.StartCommand.Execute(null);
                await WaitFor(() => vm.IsBusy, 5);
                await WaitFor(() => !vm.IsBusy, 120);
            }
        }

        var root = (FrameworkElement)window.Content;
        var size = new Size(width, height);
        for (int i = 0; i < 3; i++)
        {
            root.Measure(size);
            root.Arrange(new Rect(size));
            root.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }

        var bg = app.TryFindResource("ApplicationBackgroundBrush") as Brush ?? Brushes.White;
        var background = new DrawingVisual();
        using (var dc = background.RenderOpen()) dc.DrawRectangle(bg, null, new Rect(size));
        var bmp = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(background);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using (var fs = File.Create(output)) enc.Save(fs);

        Console.WriteLine($"Saved {output}");
        foreach (var f in vm.Files) Console.WriteLine($"  {f.Name}: {f.State} {f.Message} {f.OutputPath}");
        string err = errors.Text.ToString().Trim();
        if (err.Length > 0)
        {
            Console.Error.WriteLine("Binding problems:");
            Console.Error.WriteLine(err);
        }
        if (run)
        {
            foreach (var f in vm.Files)
                if (f.OutputPath is { } o) try { File.Delete(o); } catch (IOException) { }
        }
        return 0;
    }

    private static async Task WaitFor(Func<bool> condition, double seconds)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.Elapsed.TotalSeconds < seconds)
            await Task.Delay(50);
    }
}
