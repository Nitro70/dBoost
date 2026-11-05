using System.Text;

namespace DBoost.Core;

/// <summary>
/// Builds the ffmpeg audio filter chain for one track:
/// <c>aeval (parts, left/right, fades) , bass , treble , volume , alimiter , aformat</c>.
/// </summary>
public static class FilterBuilder
{
    /// <summary>Ramp at each end of a part, so a boosted stretch fades in and out instead of clicking.</summary>
    public const double SegmentRampSeconds = 0.02;

    /// <summary>
    /// Everything that changes the sound before the overall level is set: parts, left/right and tone.
    /// Loudness measurement for auto level runs on this, so the level is set for what will be heard.
    /// Returns null when there is nothing to do.
    /// </summary>
    public static string? BuildPreLevelChain(BoostSettings s, FileEdits e, AudioStreamInfo track, double? durationSeconds, double timeOffset = 0) =>
        Join(BuildEval(s, e, track, durationSeconds, timeOffset, includeFades: false), BuildTone(s));

    /// <summary>The full chain for one track, with the overall gain already decided.</summary>
    public static string BuildChain(BoostSettings s, FileEdits e, AudioStreamInfo track, double? durationSeconds,
        double gainDb, string? formatFilter = null, double timeOffset = 0)
    {
        var parts = new List<string?>
        {
            BuildEval(s, e, track, durationSeconds, timeOffset, includeFades: true),
            BuildTone(s),
            Math.Abs(gainDb) < 1e-9 ? null : $"volume={Decibels.Num(Decibels.Clamp(gainDb))}dB",
            s.PreventClipping ? BuildLimiter(s) : null,
            formatFilter,
        };
        return Join(parts.ToArray()) ?? "anull";
    }

    /// <summary>True when the chain would leave the sound exactly as it is.</summary>
    public static bool IsIdentity(BoostSettings s, FileEdits e, double gainDb) =>
        Math.Abs(gainDb) < 1e-9
        && s.AutoLevel == AutoLevel.Off
        && !e.Segments.Any(seg => seg.IsValid && Math.Abs(seg.GainDb) > 1e-9)
        && Math.Abs(s.LeftDb) < 1e-9 && Math.Abs(s.RightDb) < 1e-9
        && Math.Abs(s.BassDb) < 1e-9 && Math.Abs(s.TrebleDb) < 1e-9
        && s.FadeInSeconds <= 0 && s.FadeOutSeconds <= 0;

    internal static string BuildLimiter(BoostSettings s)
    {
        double limit = Math.Clamp(Decibels.ToAmplitude(s.ClampedCeilingDb), 0.0625, 1);
        // level=0: do not auto-gain the output; latency=1: remove the look-ahead delay so video stays in sync.
        return $"alimiter=limit={Decibels.Num(limit)}:attack=5:release=50:level=0:latency=1";
    }

    private static string? BuildTone(BoostSettings s)
    {
        var parts = new List<string?>();
        if (Math.Abs(s.BassDb) > 1e-9) parts.Add($"bass=g={Decibels.Num(Math.Clamp(s.BassDb, -40, 40))}");
        if (Math.Abs(s.TrebleDb) > 1e-9) parts.Add($"treble=g={Decibels.Num(Math.Clamp(s.TrebleDb, -40, 40))}");
        return Join(parts.ToArray());
    }

    /// <summary>
    /// One per-sample expression for every time-varying gain: <c>val(ch) * parts * left/right * fades</c>.
    /// Parts multiply, so overlapping parts add their dB. ffmpeg's expression parser needs plain numbers,
    /// so gains go in as amplitude factors.
    /// </summary>
    internal static string? BuildEval(BoostSettings s, FileEdits e, AudioStreamInfo track, double? durationSeconds, double timeOffset, bool includeFades)
    {
        string T = timeOffset == 0 ? "t" : $"(t+{Decibels.Num(timeOffset)})";
        var factors = new List<string>();

        foreach (var seg in e.Segments)
        {
            if (!seg.IsValid || Math.Abs(seg.GainDb) < 1e-9) continue;
            double g = Decibels.ToAmplitude(seg.GainDb);
            double ramp = Math.Min(SegmentRampSeconds, seg.Length / 2);
            string st = Decibels.Num(seg.StartSeconds), en = Decibels.Num(seg.EndSeconds), r = Decibels.Num(ramp);
            factors.Add($"(1+{Decibels.Num(g - 1)}*clip(({T}-{st})/{r},0,1)*clip(({en}-{T})/{r},0,1))");
        }

        if (track.Channels >= 2 && (Math.Abs(s.LeftDb) > 1e-9 || Math.Abs(s.RightDb) > 1e-9))
        {
            string gl = Decibels.Num(Decibels.ToAmplitude(s.LeftDb));
            string gr = Decibels.Num(Decibels.ToAmplitude(s.RightDb));
            factors.Add($"if(eq(ch,0),{gl},if(eq(ch,1),{gr},1))");
        }

        if (includeFades)
        {
            if (s.FadeInSeconds > 0)
                factors.Add($"clip({T}/{Decibels.Num(s.FadeInSeconds)},0,1)");
            if (s.FadeOutSeconds > 0 && durationSeconds is > 0)
                factors.Add($"clip(({Decibels.Num(durationSeconds.Value)}-{T})/{Decibels.Num(s.FadeOutSeconds)},0,1)");
        }

        if (factors.Count == 0) return null;
        // Single quotes keep the commas inside the expression from splitting the filter chain.
        return $"aeval=exprs='val(ch)*{string.Join("*", factors)}':channel_layout=same";
    }

    private static string? Join(params string?[] parts)
    {
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            if (string.IsNullOrEmpty(p)) continue;
            if (sb.Length > 0) sb.Append(',');
            sb.Append(p);
        }
        return sb.Length == 0 ? null : sb.ToString();
    }
}
