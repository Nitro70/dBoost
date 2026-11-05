namespace DBoost.Core;

public sealed class BoostException(string message) : Exception(message);

public enum PlanKind
{
    /// <summary>Same container, video and extra streams copied, boosted tracks re-encoded.</summary>
    KeepContainer,
    /// <summary>A plain audio file with one track (MP3, FLAC, ...), possibly pulled out of a video.</summary>
    AudioOnly,
    /// <summary>Video copied into a different container (MP4 or MKV).</summary>
    ChangeContainer,
}

/// <summary>One audio track that gets written by an encoder (boosted, or re-encoded because the new container needs it).</summary>
public sealed record TrackPlan(AudioStreamInfo Track, int OutputIndex, EncoderChoice Encoder, string Filter);

/// <summary>Everything about how one file is written, except the input and output paths.</summary>
public sealed record JobPlan(
    PlanKind Kind,
    string Extension,
    IReadOnlyList<TrackPlan> Tracks,
    IReadOnlyList<IReadOnlyList<string>> MappingVariants,
    IReadOnlyList<string> OutputArgs,
    IReadOnlyList<string> Notes)
{
    /// <summary>Full ffmpeg argument list for one mapping variant.</summary>
    public List<string> BuildArgs(string inputPath, string outputPath, int variant)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-progress", "pipe:1", "-nostats",
            "-i", inputPath,
        };
        args.AddRange(MappingVariants[variant]);
        args.AddRange(OutputArgs);
        args.Add(outputPath);
        return args;
    }
}

/// <summary>Decides containers, encoders, filters and stream mapping for one file. Pure: no ffmpeg runs here.</summary>
public static class JobPlanner
{
    private static readonly HashSet<string> CoverArtContainers = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".m4a", ".flac" };

    /// <param name="trackGains">The overall gain for each boosted track ordinal (already resolved for auto level).</param>
    public static JobPlan Plan(MediaInfo info, BoostSettings s, FileEdits e, EncoderPlanner planner, IReadOnlyDictionary<int, double> trackGains)
    {
        if (info.AudioStreams.Count == 0)
            throw new BoostException("This file has no sound to boost.");

        var boosted = info.AudioStreams
            .Where(a => e.BoostedTracks is null || e.BoostedTracks.Contains(a.Ordinal))
            .ToList();
        if (boosted.Count == 0)
            throw new BoostException("No audio track is selected for this file.");

        var notes = new List<string>();
        double? duration = info.DurationSeconds;
        string sourceExt = info.Extension;
        int? kbps = s.BitrateKbps;

        double GainOf(AudioStreamInfo a) => trackGains.TryGetValue(a.Ordinal, out double g) ? g : e.EffectiveGain(s);
        string ChainFor(AudioStreamInfo a, EncoderChoice enc, bool processed) => processed
            ? FilterBuilder.BuildChain(s, e, a, duration, GainOf(a), enc.FormatFilter)
            : enc.FormatFilter ?? "anull";

        // 1. Pick the kind of output.
        PlanKind kind;
        string ext;
        EncoderChoice? forcedAudioEncoder = null;

        if (s.Format.IsAudioOnly())
        {
            kind = PlanKind.AudioOnly;
            ext = s.Format.Extension();
        }
        else if (s.Format.IsVideoContainer() && info.IsVideo && !string.Equals(s.Format.Extension(), sourceExt, StringComparison.OrdinalIgnoreCase))
        {
            kind = PlanKind.ChangeContainer;
            ext = s.Format.Extension();
        }
        else
        {
            if (s.Format.IsVideoContainer() && !info.IsVideo)
                notes.Add($"{s.Format.Extension().TrimStart('.').ToUpperInvariant()} is for videos, so this audio file stayed {sourceExt.TrimStart('.').ToUpperInvariant()}.");
            kind = PlanKind.KeepContainer;
            ext = sourceExt;

            if (!MediaExtensions.Writable.Contains(sourceExt))
            {
                if (info.IsVideo)
                {
                    kind = PlanKind.ChangeContainer;
                    ext = ".mkv";
                    notes.Add($"{Upper(sourceExt)} files can't be written, so this one was saved as MKV.");
                }
                else
                {
                    var (choice, fallbackExt) = planner.AudioFallback(boosted[0], kbps);
                    kind = PlanKind.AudioOnly;
                    ext = fallbackExt;
                    forcedAudioEncoder = choice;
                    notes.Add($"{Upper(sourceExt)} files can't be written, so this one was saved as {Upper(fallbackExt)}.");
                }
            }
            else if (!info.IsVideo)
            {
                // An audio file keeps its own codec when this ffmpeg can write it; otherwise it changes format.
                var stuck = boosted.FirstOrDefault(a => planner.SameCodec(a, kbps) is null);
                if (stuck is not null)
                {
                    var (choice, fallbackExt) = planner.AudioFallback(stuck, kbps);
                    kind = PlanKind.AudioOnly;
                    ext = fallbackExt;
                    forcedAudioEncoder = choice;
                    notes.Add($"{stuck.Codec} audio can't be written, so this file was saved as {Upper(fallbackExt)}.");
                }
            }
        }

        // 2. Tracks, encoders and filters.
        var tracks = new List<TrackPlan>();
        var mapping = new List<IReadOnlyList<string>>();
        var outArgs = new List<string>();

        switch (kind)
        {
            case PlanKind.AudioOnly:
            {
                var track = boosted[0];
                if (boosted.Count > 1 || (e.BoostedTracks is null && info.AudioStreams.Count > 1))
                    notes.Add($"Audio files hold one track, so only {track.Describe()} was kept.");
                var enc = forcedAudioEncoder ?? planner.ForFormat(s.Format, track, kbps);
                tracks.Add(new TrackPlan(track, 0, enc, ChainFor(track, enc, processed: true)));

                bool cover = info.CoverArtVideoOrdinal is not null && !info.IsVideo && CoverArtContainers.Contains(ext);
                if (cover)
                    mapping.Add(["-map", $"0:a:{track.Ordinal}", "-map", $"0:v:{info.CoverArtVideoOrdinal}", "-c:v", "copy", "-disposition:v:0", "attached_pic"]);
                mapping.Add(["-map", $"0:a:{track.Ordinal}", "-vn"]);
                break;
            }

            case PlanKind.KeepContainer:
            {
                foreach (var a in info.AudioStreams)
                {
                    if (!boosted.Contains(a)) continue; // copied as-is by -c copy
                    var enc = info.IsVideo
                        ? planner.ForVideoContainer(ext, a, kbps)
                        : planner.SameCodec(a, kbps)!;
                    tracks.Add(new TrackPlan(a, a.Ordinal, enc, ChainFor(a, enc, processed: true)));
                }
                mapping.Add(["-map", "0", "-c", "copy"]);
                mapping.Add(["-map", "0", "-map", "-0:d", "-map", "-0:t", "-c", "copy"]);
                mapping.Add(["-map", "0:v?", "-map", "0:a", "-c", "copy"]);
                break;
            }

            case PlanKind.ChangeContainer:
            {
                foreach (var a in info.AudioStreams)
                {
                    bool isBoosted = boosted.Contains(a);
                    if (!isBoosted && EncoderPlanner.ContainerAccepts(ext, a.Codec)) continue; // copied
                    var enc = planner.ForVideoContainer(ext, a, kbps);
                    tracks.Add(new TrackPlan(a, a.Ordinal, enc, ChainFor(a, enc, processed: isBoosted)));
                }
                if (info.SubtitleStreamCount > 0)
                    notes.Add("Subtitles were left out when changing the video format.");
                mapping.Add(["-map", "0:v", "-map", "0:a", "-c", "copy"]);
                mapping.Add(["-map", "0:V", "-map", "0:a", "-c", "copy"]);
                break;
            }
        }

        foreach (var t in tracks)
        {
            string i = t.OutputIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
            outArgs.AddRange([$"-c:a:{i}", t.Encoder.Encoder]);
            if (t.Encoder.BitrateKbps is int br) outArgs.AddRange([$"-b:a:{i}", $"{br}k"]);
            foreach (var (opt, val) in t.Encoder.ExtraArgs) outArgs.AddRange([$"-{opt}:a:{i}", val]);
            outArgs.AddRange([$"-filter:a:{i}", t.Filter]);
        }
        outArgs.AddRange(["-strict", "-2"]);
        if (kind != PlanKind.AudioOnly) outArgs.AddRange(["-max_muxing_queue_size", "4096"]);

        return new JobPlan(kind, ext, tracks, mapping, outArgs, notes);
    }

    private static string Upper(string ext) => ext.TrimStart('.').ToUpperInvariant();
}
