using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace DBoost.Core;

/// <summary>
/// One audio track decoded by ffmpeg into raw 48 kHz stereo 16-bit PCM, starting at a chosen time, for
/// listening. Works for every format ffmpeg reads (video files give just their sound). Disposing stops ffmpeg.
/// </summary>
public sealed class PcmReader : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int BytesPerSecond = SampleRate * Channels * 2;

    private readonly Process process;
    private readonly Stream stdout;
    private readonly StringBuilder errors = new();
    private int disposed;

    private PcmReader(Process p)
    {
        process = p;
        stdout = p.StandardOutput.BaseStream;
        _ = DrainErrorsAsync(p.StandardError);
    }

    /// <summary>What ffmpeg complained about, if anything (for "could not play" messages).</summary>
    public string Errors { get { lock (errors) return errors.ToString().Trim(); } }

    internal static PcmReader Start(string ffmpegPath, string path, int trackOrdinal, double startSeconds)
    {
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        string[] args =
        [
            "-hide_banner", "-nostdin", "-loglevel", "error",
            "-ss", Decibels.Num(Math.Max(0, startSeconds)),
            "-i", path, "-map", $"0:a:{trackOrdinal}", "-vn", "-sn", "-dn",
            "-ac", Channels.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-ar", SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-c:a", "pcm_s16le", "-f", "s16le", "-",
        ];
        foreach (string a in args) psi.ArgumentList.Add(a);

        var p = new Process { StartInfo = psi };
        try { p.Start(); }
        catch (Win32Exception ex) { p.Dispose(); throw new InvalidOperationException($"Could not start ffmpeg ({ex.Message}).", ex); }
        FfmpegRunner.Adopt(p); // dies with dBoost; normal priority so playback keeps up during games
        return new PcmReader(p);
    }

    /// <summary>
    /// Fills the whole request unless the audio ended (a short read would be heard as a gap).
    /// Returns 0 at the end, after <see cref="Dispose"/>, or if ffmpeg failed.
    /// </summary>
    public int ReadFull(byte[] buffer, int offset, int count)
    {
        int total = 0;
        try
        {
            while (total < count && Volatile.Read(ref disposed) == 0)
            {
                int n = stdout.Read(buffer, offset + total, count - total);
                if (n <= 0) break;
                total += n;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        return total - total % 4; // whole stereo frames only
    }

    private async Task DrainErrorsAsync(StreamReader r)
    {
        try
        {
            var buf = new char[1024];
            int n;
            while ((n = await r.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
                lock (errors) if (errors.Length < 8192) errors.Append(buf, 0, n);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (Win32Exception) { }
        process.Dispose();
    }
}
