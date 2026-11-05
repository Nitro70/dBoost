using DBoost.App.Services;

namespace DBoost.App;

/// <summary>
/// Entry point. Three ways in: <c>--cli</c> boosts files from a command line without a window,
/// <c>--screenshot</c> renders the window to a PNG without showing it (for docs and tests), and
/// everything else is the normal app, which hands its files to an already running dBoost if there is one.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("--cli", StringComparison.OrdinalIgnoreCase))
            return Cli.Run(args[1..]);

        if (args.Length > 0 && args[0].Equals("--screenshot", StringComparison.OrdinalIgnoreCase))
            return RunApp(screenshot: true, args[1..], null);

        var files = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        var instance = SingleInstance.TryBecomePrimary();
        // Explorer starts one dBoost per selected file. Every copy after the first just hands its file
        // to the first one and exits, before any of WPF is loaded, so a big selection stays cheap.
        if (instance is null && SingleInstance.SendToPrimary(files)) return 0;

        // No answer from another copy (still starting, or hung): open a window of our own.
        int code = RunApp(screenshot: false, files, instance);
        instance?.Dispose();
        return code;
    }

    // Separate method so WPF is only loaded when a window is really needed.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int RunApp(bool screenshot, string[] args, SingleInstance? instance)
    {
        var app = new App { Mode = screenshot ? App.LaunchMode.Screenshot : App.LaunchMode.Normal, LaunchArgs = args, Instance = instance };
        app.InitializeComponent();
        return app.Run();
    }
}
