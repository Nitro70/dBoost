using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace DBoost.Core;

public sealed record FfmpegResult(int ExitCode, string StdErr)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>The most useful line of ffmpeg's error output, for showing to a person.</summary>
    public string ErrorSummary()
    {
        var lines = StdErr.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        string? pick = lines.LastOrDefault(l =>
            !l.StartsWith("Conversion failed", StringComparison.OrdinalIgnoreCase)
            && !l.StartsWith("[out#", StringComparison.Ordinal)
            && !l.StartsWith("Error opening output files", StringComparison.OrdinalIgnoreCase))
            ?? lines.LastOrDefault();
        if (pick is null) return $"ffmpeg stopped with code {ExitCode}.";
        // "[aist#0:1/aac @ 0000...] message" -> "message"
        int close = pick.IndexOf("] ", StringComparison.Ordinal);
        if (pick.StartsWith('[') && close > 0) pick = pick[(close + 2)..];
        return pick;
    }
}

/// <summary>
/// Runs ffmpeg as a hidden child process at below-normal priority (so games and video keep running
/// smoothly), inside a job object so it dies with dBoost, and reports progress from <c>-progress pipe:1</c>.
/// </summary>
public sealed class FfmpegRunner(string ffmpegPath)
{
    public string FfmpegPath { get; } = ffmpegPath;

    private static readonly Lazy<JobObject?> Job = new(JobObject.TryCreate);

    /// <summary>Puts a process started elsewhere into the kill-on-close job, so it never outlives dBoost.</summary>
    internal static void Adopt(Process p) => Job.Value?.Assign(p);

    /// <summary>Runs ffmpeg. With <paramref name="stdoutReader"/> the caller consumes stdout (binary output);
    /// otherwise stdout is read as <c>-progress</c> lines and <paramref name="onProgress"/> gets the position.</summary>
    public async Task<FfmpegResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct,
        Action<TimeSpan>? onProgress = null, Func<Stream, CancellationToken, Task>? stdoutReader = null)
    {
        var psi = new ProcessStartInfo(FfmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = stdoutReader is null ? Encoding.UTF8 : null,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        ct.ThrowIfCancellationRequested();
        using var p = new Process { StartInfo = psi };
        try
        {
            p.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"Could not start ffmpeg ({ex.Message}).", ex);
        }

        Job.Value?.Assign(p);
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (InvalidOperationException) { } catch (Win32Exception) { }

        using var reg = ct.Register(() =>
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (Win32Exception) { }
        });

        var stderrTask = ReadStdErrAsync(p.StandardError);
        Task stdoutTask = stdoutReader is not null
            ? ReadBinaryAsync(p.StandardOutput.BaseStream, stdoutReader, ct)
            : ReadProgressAsync(p.StandardOutput, onProgress);

        await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        try { await stdoutTask.ConfigureAwait(false); }
        catch (Exception) when (ct.IsCancellationRequested) { }

        ct.ThrowIfCancellationRequested();
        return new FfmpegResult(p.ExitCode, stderr);
    }

    private static async Task ReadBinaryAsync(Stream stdout, Func<Stream, CancellationToken, Task> reader, CancellationToken ct)
    {
        try { await reader(stdout, ct).ConfigureAwait(false); }
        finally
        {
            // Keep draining so ffmpeg never blocks on a full pipe if the reader stopped early.
            try { await stdout.CopyToAsync(Stream.Null, CancellationToken.None).ConfigureAwait(false); } catch (IOException) { }
        }
    }

    private static async Task ReadProgressAsync(StreamReader stdout, Action<TimeSpan>? onProgress)
    {
        string? line;
        while ((line = await stdout.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            if (onProgress is null) continue;
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out long us)
                && us >= 0)
            {
                onProgress(TimeSpan.FromTicks(us * 10));
            }
        }
    }

    /// <summary>Keeps the whole log up to 4 MB, then only the end, which is where errors and summaries are.</summary>
    private static async Task<string> ReadStdErrAsync(StreamReader stderr)
    {
        const int Max = 4 * 1024 * 1024;
        var sb = new StringBuilder();
        var buffer = new char[8192];
        int n;
        while ((n = await stderr.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
        {
            sb.Append(buffer, 0, n);
            if (sb.Length > Max) sb.Remove(0, sb.Length - Max / 2);
        }
        return sb.ToString();
    }

    /// <summary>Encoder names this ffmpeg build can use.</summary>
    public async Task<IReadOnlySet<string>> ListEncodersAsync(CancellationToken ct)
    {
        string text = "";
        await RunAsync(["-hide_banner", "-nostdin", "-encoders"], ct, stdoutReader: async (s, token) =>
        {
            using var r = new StreamReader(s, Encoding.UTF8, leaveOpen: true);
            text = await r.ReadToEndAsync(token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return ParseEncoders(text);
    }

    internal static IReadOnlySet<string> ParseEncoders(string text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        bool table = false;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.TrimStart().StartsWith("------", StringComparison.Ordinal)) { table = true; continue; }
            if (!table) continue;
            var parts = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Length == 6) set.Add(parts[1]);
        }
        return set;
    }
}

/// <summary>A Windows job object that kills every ffmpeg it holds when dBoost exits or crashes.</summary>
internal sealed partial class JobObject
{
    private readonly IntPtr handle;

    private JobObject(IntPtr h) => handle = h;

    public static JobObject? TryCreate()
    {
        if (!OperatingSystem.IsWindows()) return null;
        IntPtr h = CreateJobObjectW(IntPtr.Zero, null);
        if (h == IntPtr.Zero) return null;

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        if (!SetInformationJobObject(h, 9 /* JobObjectExtendedLimitInformation */, ref info, (uint)size))
        {
            CloseHandle(h);
            return null;
        }
        return new JobObject(h); // never closed on purpose: closing it is what kills the children
    }

    public void Assign(Process p)
    {
        try { AssignProcessToJobObject(handle, p.Handle); } catch (InvalidOperationException) { } catch (Win32Exception) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr h);
}
