using System.Globalization;
using System.IO;
using DBoost.App.Services;
using DBoost.Core;

namespace DBoost.App;

/// <summary>
/// <c>dBoost.exe --cli [options] files-or-folders...</c>: boosts files with no window. From cmd.exe use
/// <c>start /wait dBoost.exe --cli ...</c> so the prompt waits for it.
/// </summary>
public static class Cli
{
    private const string Usage = """
        dBoost --cli [options] <files or folders...>

          --gain <dB>         Volume change, any amount. Default +6. Use a minus to make it quieter: --gain -6
          --out <folder>      Save into this folder instead of next to each file
          --format <f>        same (default), mp3, m4a, wav, flac, ogg, opus, mp4, mkv
          --bitrate <kbps>    For lossy formats. Default: about what the original had
          --loudness <LUFS>   Auto level every file to this loudness (e.g. -14), instead of --gain
          --peak <dB>         Auto level every file so its loudest peak is here (e.g. -1), instead of --gain
          --no-limiter        Let peaks clip instead of limiting them
          --help              Show this
        """;

    public static int Run(string[] args)
    {
        if (!Console.IsOutputRedirected) Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
        try
        {
            return RunAsync(args).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"dBoost: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var s = new BoostSettings { GainDb = 6 };
        var inputs = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value.");
            double Number(string what)
            {
                string v = Next();
                return Decibels.TryParse(v, out double d) ? d : throw new ArgumentException($"{what} must be a number, not \"{v}\".");
            }

            switch (a.ToLowerInvariant())
            {
                case "--help" or "-h" or "/?":
                    Console.WriteLine(Usage);
                    return 0;
                case "--gain": s = s with { GainDb = Number("--gain") }; break;
                case "--out": s = s with { OutputFolder = Path.GetFullPath(Next()) }; break;
                case "--format": s = s with { Format = ParseFormat(Next()) }; break;
                case "--bitrate": s = s with { BitrateKbps = (int)Number("--bitrate") }; break;
                case "--loudness": s = s with { AutoLevel = AutoLevel.Loudness, AutoLevelTarget = Number("--loudness") }; break;
                case "--peak": s = s with { AutoLevel = AutoLevel.Peak, AutoLevelTarget = Number("--peak") }; break;
                case "--no-limiter": s = s with { PreventClipping = false }; break;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unknown option {a}. Try --help.");
                    inputs.Add(a);
                    break;
            }
        }

        var files = MediaExtensions.Expand(inputs).ToList();
        if (files.Count == 0)
        {
            Console.Error.WriteLine(inputs.Count == 0 ? Usage : "No audio or video files found.");
            return 2;
        }

        var engine = await EngineHost.GetAsync();
        int failed = 0;
        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            try
            {
                var info = await engine.ProbeAsync(file);
                var result = await engine.ProcessAsync(info, s, FileEdits.None);
                if (result.Skipped) Console.WriteLine($"{name}: nothing to change");
                else Console.WriteLine($"{name} -> {result.OutputPath}");
                foreach (string note in result.Notes) Console.WriteLine($"  note: {note}");
            }
            catch (Exception ex) when (ex is BoostException or MediaProbeException or IOException or UnauthorizedAccessException)
            {
                failed++;
                Console.Error.WriteLine($"{name}: FAILED: {ex.Message}");
            }
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Done: {files.Count - failed} of {files.Count} saved."));
        return failed == 0 ? 0 : 1;
    }

    private static OutputFormat ParseFormat(string f) => f.ToLowerInvariant().TrimStart('.') switch
    {
        "same" => OutputFormat.SameAsSource,
        "mp3" => OutputFormat.Mp3,
        "m4a" or "aac" => OutputFormat.M4a,
        "wav" => OutputFormat.Wav,
        "flac" => OutputFormat.Flac,
        "ogg" => OutputFormat.Ogg,
        "opus" => OutputFormat.Opus,
        "mp4" => OutputFormat.Mp4,
        "mkv" => OutputFormat.Mkv,
        _ => throw new ArgumentException($"Unknown format \"{f}\"."),
    };
}
