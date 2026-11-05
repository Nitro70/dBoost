using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DBoost.Core;

/// <summary>Peak and loudness of one track, as measured by ffmpeg.</summary>
public sealed record LoudnessAnalysis(double PeakDb, double MeanDb, double? IntegratedLufs)
{
    /// <summary>How far it can be boosted before the loudest peak hits full scale.</summary>
    public double HeadroomDb => double.IsFinite(PeakDb) ? -PeakDb : 0;

    public bool IsSilent => !double.IsFinite(PeakDb) || PeakDb < -90;
}

public sealed record BoostResult(string? OutputPath, bool Skipped, IReadOnlyList<string> Notes);

/// <summary>Peaks and RMS per slice of time, for drawing.</summary>
public sealed record Waveform(double DurationSeconds, float[] Peaks, float[] Rms)
{
    public int Buckets => Peaks.Length;
}

/// <summary>The whole engine: probe, analyse, boost, waveform and preview, all through ffmpeg.</summary>
public sealed partial class BoostEngine
{
    private readonly FfmpegRunner runner;

    public EncoderPlanner Planner { get; }
    public string FfmpegPath => runner.FfmpegPath;

    private BoostEngine(FfmpegRunner runner, EncoderPlanner planner)
    {
        this.runner = runner;
        Planner = planner;
    }

    public static async Task<BoostEngine> CreateAsync(string ffmpegPath, CancellationToken ct = default)
    {
        var runner = new FfmpegRunner(ffmpegPath);
        var encoders = await runner.ListEncodersAsync(ct).ConfigureAwait(false);
        if (encoders.Count == 0)
            throw new InvalidOperationException("This ffmpeg did not list any encoders.");
        return new BoostEngine(runner, new EncoderPlanner(encoders));
    }

    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct = default)
    {
        var result = await runner.RunAsync(["-hide_banner", "-nostdin", "-i", path], ct).ConfigureAwait(false);
        return MediaProbe.Parse(result.StdErr, path);
    }

    // ---------------------------------------------------------------- analysis

    [GeneratedRegex(@"max_volume: (?<v>-?inf|-?[\d.]+) dB", RegexOptions.CultureInvariant)]
    private static partial Regex MaxVolume();

    [GeneratedRegex(@"mean_volume: (?<v>-?inf|-?[\d.]+) dB", RegexOptions.CultureInvariant)]
    private static partial Regex MeanVolume();

    [GeneratedRegex(@"^\s*I:\s+(?<v>-?inf|nan|-?[\d.]+) LUFS", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex Integrated();

    /// <summary>Measures one track after parts, left/right and tone are applied (the sound auto level will work on).</summary>
    public async Task<LoudnessAnalysis> AnalyzeAsync(MediaInfo info, AudioStreamInfo track, BoostSettings s, FileEdits e,
        Action<double>? progress = null, CancellationToken ct = default)
    {
        string pre = FilterBuilder.BuildPreLevelChain(s, e, track, info.DurationSeconds) is { } p ? p + "," : "";
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "info", "-progress", "pipe:1", "-nostats",
            "-i", info.Path, "-map", $"0:a:{track.Ordinal}",
            "-filter:a", pre + "volumedetect,ebur128=framelog=verbose",
            "-f", "null", "-",
        };
        var result = await runner.RunAsync(args, ct, Progress(info, progress)).ConfigureAwait(false);
        if (!result.Succeeded) throw new BoostException(FriendlyError(result, info.Extension));
        return ParseAnalysis(result.StdErr);
    }

    internal static LoudnessAnalysis ParseAnalysis(string stderr)
    {
        double peak = LastNumber(MaxVolume(), stderr) ?? double.NegativeInfinity;
        double mean = LastNumber(MeanVolume(), stderr) ?? double.NegativeInfinity;
        double? lufs = LastNumber(Integrated(), stderr);
        if (lufs is not null && (!double.IsFinite(lufs.Value) || lufs <= -69.9)) lufs = null;
        return new LoudnessAnalysis(peak, mean, lufs);
    }

    private static double? LastNumber(Regex rx, string text)
    {
        var matches = rx.Matches(text);
        if (matches.Count == 0) return null;
        string v = matches[^1].Groups["v"].Value;
        if (v.EndsWith("inf", StringComparison.Ordinal)) return v.StartsWith('-') ? double.NegativeInfinity : double.PositiveInfinity;
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : null;
    }

    /// <summary>The gain auto level picks for a measured track.</summary>
    public static (double GainDb, string? Note) AutoLevelGain(BoostSettings s, LoudnessAnalysis a)
    {
        if (a.IsSilent) return (0, "A track is silent, so it was left as it was.");
        return s.AutoLevel switch
        {
            AutoLevel.Peak => (s.AutoLevelTarget - a.PeakDb, null),
            AutoLevel.Loudness when a.IntegratedLufs is double i => (s.AutoLevelTarget - i, null),
            AutoLevel.Loudness => (0, "A track is too quiet to measure its loudness, so it was left as it was."),
            _ => (0, null),
        };
    }

    // ---------------------------------------------------------------- boosting

    /// <summary>Writes the boosted copy. Progress goes from 0 to 1, or NaN when the length is unknown.</summary>
    public async Task<BoostResult> ProcessAsync(MediaInfo info, BoostSettings s, FileEdits e,
        Action<double>? progress = null, CancellationToken ct = default)
    {
        var notes = new List<string>();
        var gains = new Dictionary<int, double>();
        var boosted = info.AudioStreams.Where(a => e.BoostedTracks is null || e.BoostedTracks.Contains(a.Ordinal)).ToList();

        double analysisShare = s.AutoLevel == AutoLevel.Off ? 0 : 0.4;
        if (s.AutoLevel != AutoLevel.Off)
        {
            for (int i = 0; i < boosted.Count; i++)
            {
                int index = i;
                var analysis = await AnalyzeAsync(info, boosted[i], s, e,
                    f => progress?.Invoke(double.IsNaN(f) ? f : analysisShare * (index + f) / boosted.Count), ct).ConfigureAwait(false);
                var (gain, note) = AutoLevelGain(s, analysis);
                gains[boosted[i].Ordinal] = gain;
                if (note is not null && !notes.Contains(note)) notes.Add(note);
            }
        }

        if (s.Format == OutputFormat.SameAsSource && s.AutoLevel == AutoLevel.Off
            && boosted.All(a => FilterBuilder.IsIdentity(s, e, e.EffectiveGain(s))))
        {
            return new BoostResult(null, true, ["0 dB and no other changes, so there was nothing to do."]);
        }

        var plan = JobPlanner.Plan(info, s, e, Planner, gains);
        notes.AddRange(plan.Notes);

        string? folder = s.OutputFolder;
        if (!string.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);
        string label = OutputNaming.Label(s, e);
        string final = Reserve(info.Path, folder, plan.Extension, label);
        string reserved = final;
        string partial = OutputNaming.PartialPath(final);

        var onProgress = Progress(info, progress is null ? null : f => progress(double.IsNaN(f) ? f : analysisShare + (1 - analysisShare) * f));
        FfmpegResult? firstFailure = null;
        try
        {
            for (int v = 0; v < plan.MappingVariants.Count; v++)
            {
                var result = await runner.RunAsync(plan.BuildArgs(info.Path, partial, v), ct, onProgress).ConfigureAwait(false);
                if (result.Succeeded && File.Exists(partial))
                {
                    final = MoveIntoPlace(partial, final, info.Path, folder, plan.Extension, label);
                    if (v > 0) notes.Add(v == plan.MappingVariants.Count - 1
                        ? "Some extra streams (subtitles or attachments) could not be kept."
                        : "Some extra data streams could not be kept.");
                    progress?.Invoke(1);
                    return new BoostResult(final, false, notes);
                }
                firstFailure ??= result;
                TryDelete(partial);
            }
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
        finally
        {
            lock (ReservedNames) ReservedNames.Remove(reserved);
        }

        throw new BoostException(FriendlyError(firstFailure!, plan.Extension));
    }

    /// <summary>Output names being written right now, so two files called the same thing that are
    /// processed at the same time into one folder never pick the same name.</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase);

    private static string Reserve(string input, string? folder, string ext, string label)
    {
        lock (ReservedNames)
        {
            string path = OutputNaming.UniquePath(input, folder, ext, label, ReservedNames.Contains);
            ReservedNames.Add(path);
            return path;
        }
    }

    private static string MoveIntoPlace(string partial, string final, string input, string? folder, string ext, string label)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(partial, final, overwrite: false);
                return final;
            }
            catch (IOException) when (attempt < 5 && File.Exists(final))
            {
                final = OutputNaming.UniquePath(input, folder, ext, label); // something appeared there meanwhile
            }
        }
    }

    internal static string FriendlyError(FfmpegResult result, string ext)
    {
        string all = result.StdErr;
        if (all.Contains("No space left", StringComparison.OrdinalIgnoreCase))
            return "The disk is full.";
        if (all.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            return "Windows would not let dBoost save there. Try another output folder.";
        if (all.Contains("not currently supported in container", StringComparison.OrdinalIgnoreCase)
            || all.Contains("Could not find tag for codec", StringComparison.OrdinalIgnoreCase))
            return $"This video's picture format can't go into {ext.TrimStart('.').ToUpperInvariant()}. Choose \"Same as original\" or MKV.";
        return result.ErrorSummary();
    }

    // ---------------------------------------------------------------- waveform

    /// <summary>Decodes one track at a low sample rate and reduces it to <paramref name="buckets"/> peak/RMS pairs.</summary>
    public async Task<Waveform> ReadWaveformAsync(MediaInfo info, AudioStreamInfo track, int buckets, CancellationToken ct = default)
    {
        double? duration = info.DurationSeconds;
        int rate = duration is > 0 ? (int)Math.Clamp(4_000_000 / duration.Value, 100, 8000) : 2000;
        int channels = track.Channels >= 2 ? 2 : 1;
        int block = Math.Max(1, rate / 200); // 5 ms blocks, reduced to buckets at the end

        var blockPeaks = new List<float>(capacity: 1 << 16);
        var blockSq = new List<double>(capacity: 1 << 16);
        long frames = 0;

        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "error", "-i", info.Path, "-map", $"0:a:{track.Ordinal}",
            "-ac", channels.ToString(CultureInfo.InvariantCulture), "-ar", rate.ToString(CultureInfo.InvariantCulture),
            "-c:a", "pcm_f32le", "-f", "f32le", "-",
        };

        var result = await runner.RunAsync(args, ct, stdoutReader: async (stdout, token) =>
        {
            var buf = new byte[64 * 1024];
            int carry = 0;
            int frameBytes = 4 * channels;
            float peak = 0; double sq = 0; int inBlock = 0;
            int n;
            while ((n = await stdout.ReadAsync(buf.AsMemory(carry, buf.Length - carry), token).ConfigureAwait(false)) > 0)
            {
                int avail = carry + n;
                int whole = avail - avail % frameBytes;
                for (int off = 0; off < whole; off += frameBytes)
                {
                    float m = 0;
                    for (int c = 0; c < channels; c++)
                    {
                        float x = Math.Abs(BinaryPrimitives.ReadSingleLittleEndian(buf.AsSpan(off + 4 * c, 4)));
                        if (float.IsFinite(x) && x > m) m = x;
                    }
                    if (m > peak) peak = m;
                    sq += (double)m * m;
                    if (++inBlock == block)
                    {
                        blockPeaks.Add(peak); blockSq.Add(sq / block);
                        peak = 0; sq = 0; inBlock = 0;
                    }
                    frames++;
                }
                carry = avail - whole;
                if (carry > 0) Buffer.BlockCopy(buf, whole, buf, 0, carry);
            }
            if (inBlock > 0) { blockPeaks.Add(peak); blockSq.Add(sq / inBlock); }
        }).ConfigureAwait(false);

        if (!result.Succeeded && blockPeaks.Count == 0)
            throw new BoostException(result.ErrorSummary());

        buckets = Math.Max(1, Math.Min(buckets, Math.Max(1, blockPeaks.Count)));
        var peaks = new float[buckets];
        var rms = new float[buckets];
        for (int b = 0; b < buckets; b++)
        {
            int from = (int)((long)b * blockPeaks.Count / buckets);
            int to = (int)((long)(b + 1) * blockPeaks.Count / buckets);
            float p = 0; double s = 0; int count = 0;
            for (int i = from; i < Math.Max(to, from + 1) && i < blockPeaks.Count; i++)
            {
                if (blockPeaks[i] > p) p = blockPeaks[i];
                s += blockSq[i]; count++;
            }
            peaks[b] = p;
            rms[b] = count == 0 ? 0 : (float)Math.Sqrt(s / count);
        }
        double seconds = frames > 0 ? (double)frames / rate : duration ?? 0;
        return new Waveform(seconds, peaks, rms);
    }

    // ---------------------------------------------------------------- preview

    /// <summary>
    /// Renders a short WAV of one track, with every setting applied (<paramref name="processed"/>) or untouched
    /// for comparison. <paramref name="gainDb"/> is the resolved overall gain (auto level already measured).
    /// </summary>
    public async Task RenderPreviewAsync(MediaInfo info, AudioStreamInfo track, BoostSettings s, FileEdits e, double gainDb,
        double startSeconds, double lengthSeconds, bool processed, string outWav, CancellationToken ct = default)
    {
        startSeconds = Math.Max(0, startSeconds);
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-y", "-loglevel", "error",
            "-ss", Decibels.Num(startSeconds), "-t", Decibels.Num(Math.Max(0.5, lengthSeconds)),
            "-i", info.Path, "-map", $"0:a:{track.Ordinal}",
        };
        if (processed)
            args.AddRange(["-filter:a", FilterBuilder.BuildChain(s, e, track, info.DurationSeconds, gainDb, timeOffset: startSeconds)]);
        args.AddRange(["-ac", "2", "-c:a", "pcm_s16le", "-f", "wav", outWav]);

        var result = await runner.RunAsync(args, ct).ConfigureAwait(false);
        if (!result.Succeeded) throw new BoostException(result.ErrorSummary());
    }

    private static Action<TimeSpan>? Progress(MediaInfo info, Action<double>? progress)
    {
        if (progress is null) return null;
        double? total = info.DurationSeconds;
        if (total is not > 0) return _ => progress(double.NaN);
        return t => progress(Math.Clamp(t.TotalSeconds / total.Value, 0, 1));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
