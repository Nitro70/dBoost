using System.Diagnostics;
using System.IO;
using System.Windows.Threading;

namespace DBoost.App.Services;

/// <summary>Plays a rendered preview WAV with the Windows sound API and reports where playback is.</summary>
public sealed class PreviewPlayer
{
    private readonly DispatcherTimer timer;
    private readonly Stopwatch clock = new();
    private double startSeconds, lengthSeconds;
    private string? currentFile;

    public static string TempFolder { get; } = Path.Combine(Path.GetTempPath(), "dBoost-preview");

    /// <summary>Position in the source file, in seconds; null when stopped.</summary>
    public event Action<double?>? PositionChanged;

    public bool IsPlaying => clock.IsRunning;

    public PreviewPlayer()
    {
        timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        timer.Tick += (_, _) =>
        {
            double t = clock.Elapsed.TotalSeconds;
            if (t >= lengthSeconds) Stop();
            else PositionChanged?.Invoke(startSeconds + t);
        };
    }

    public static string NewTempWav()
    {
        Directory.CreateDirectory(TempFolder);
        return Path.Combine(TempFolder, $"preview-{Guid.NewGuid():N}.wav");
    }

    public void Play(string wav, double fromSeconds, double seconds)
    {
        Stop();
        if (!Native.PlayWav(wav)) return;
        currentFile = wav;
        startSeconds = fromSeconds;
        lengthSeconds = seconds;
        clock.Restart();
        timer.Start();
        PositionChanged?.Invoke(fromSeconds);
    }

    public void Stop()
    {
        Native.StopSound();
        bool was = clock.IsRunning;
        clock.Reset();
        timer.Stop();
        if (currentFile is not null) TryDelete(currentFile);
        currentFile = null;
        if (was) PositionChanged?.Invoke(null);
    }

    public static void CleanTempFolder()
    {
        try
        {
            if (Directory.Exists(TempFolder))
                foreach (string f in Directory.EnumerateFiles(TempFolder, "preview-*.wav")) TryDelete(f);
        }
        catch (IOException) { }
    }

    private static void TryDelete(string f)
    {
        try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
