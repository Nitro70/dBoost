using System.Security.Cryptography;

namespace DBoost.Core;

/// <summary>
/// Finds the ffmpeg to use. Order: <c>DBOOST_FFMPEG</c>, an ffmpeg.exe next to dBoost.exe (lets anyone swap in
/// their own build), the copy bundled inside dBoost.exe, then ffmpeg on PATH.
/// </summary>
public static class FfmpegLocator
{
    public static string? FromEnvironment()
    {
        string? p = Environment.GetEnvironmentVariable("DBOOST_FFMPEG");
        return !string.IsNullOrWhiteSpace(p) && File.Exists(p) ? Path.GetFullPath(p) : null;
    }

    public static string? NextTo(string? directory)
    {
        if (string.IsNullOrEmpty(directory)) return null;
        string p = Path.Combine(directory, "ffmpeg.exe");
        return File.Exists(p) ? p : null;
    }

    public static string? OnPath()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string p = Path.Combine(dir.Trim('"'), "ffmpeg.exe");
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>
    /// Unpacks the bundled ffmpeg into <c>baseDir\ffmpeg\&lt;hash&gt;\ffmpeg.exe</c> the first time, checks its
    /// SHA-256, and marks it complete. Later starts only check that the marker exists.
    /// </summary>
    public static string Provision(string sha256Hex, Func<Stream?> openPayload, string baseDir, Func<Stream?>? openLicense = null)
    {
        string hash = sha256Hex.Trim().ToLowerInvariant();
        string root = Path.Combine(baseDir, "ffmpeg");
        string dir = Path.Combine(root, hash[..16]);
        string exe = Path.Combine(dir, "ffmpeg.exe");
        string marker = Path.Combine(dir, ".complete");
        if (File.Exists(marker) && File.Exists(exe)) return exe;

        using var mutex = new Mutex(false, @"Local\dBoost-ffmpeg-unpack");
        bool owned = false;
        try
        {
            try { owned = mutex.WaitOne(TimeSpan.FromMinutes(2)); }
            catch (AbandonedMutexException) { owned = true; }

            if (File.Exists(marker) && File.Exists(exe)) return exe; // another instance just did it

            Directory.CreateDirectory(dir);
            string temp = exe + ".unpacking";
            using (var payload = openPayload() ?? throw new InvalidOperationException("The bundled ffmpeg is missing from this build."))
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[1 << 20];
                int n;
                while ((n = payload.Read(buffer, 0, buffer.Length)) > 0)
                {
                    sha.AppendData(buffer, 0, n);
                    file.Write(buffer, 0, n);
                }
                file.Flush(flushToDisk: true);
                string got = Convert.ToHexStringLower(sha.GetHashAndReset());
                if (got != hash)
                {
                    file.Close();
                    File.Delete(temp);
                    throw new InvalidOperationException("The bundled ffmpeg did not unpack correctly (checksum mismatch).");
                }
            }
            File.Move(temp, exe, overwrite: true);
            using (var license = openLicense?.Invoke())
            {
                if (license is not null)
                {
                    using var lf = File.Create(Path.Combine(dir, "LICENSE.txt"));
                    license.CopyTo(lf);
                }
            }
            File.WriteAllText(marker, hash);
            CleanOldVersions(root, dir);
            return exe;
        }
        finally
        {
            if (owned) mutex.ReleaseMutex();
        }
    }

    private static void CleanOldVersions(string root, string keep)
    {
        try
        {
            foreach (string d in Directory.EnumerateDirectories(root))
            {
                if (string.Equals(d, keep, StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(d, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
    }
}
