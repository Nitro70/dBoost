namespace DBoost.Core;

public enum AutoLevel
{
    /// <summary>Use the fixed dB change.</summary>
    Off,
    /// <summary>Boost each file so its loudest peak lands on a target (e.g. -1 dBFS).</summary>
    Peak,
    /// <summary>Make each file equally loud (integrated loudness in LUFS, e.g. -14 like streaming services).</summary>
    Loudness,
}

public enum OutputFormat
{
    SameAsSource,
    Mp3,
    M4a,
    Wav,
    Flac,
    Ogg,
    Opus,
    Mp4,
    Mkv,
}

public static class OutputFormats
{
    public static bool IsAudioOnly(this OutputFormat f) =>
        f is OutputFormat.Mp3 or OutputFormat.M4a or OutputFormat.Wav or OutputFormat.Flac or OutputFormat.Ogg or OutputFormat.Opus;

    public static bool IsVideoContainer(this OutputFormat f) => f is OutputFormat.Mp4 or OutputFormat.Mkv;

    public static string Extension(this OutputFormat f) => f switch
    {
        OutputFormat.Mp3 => ".mp3",
        OutputFormat.M4a => ".m4a",
        OutputFormat.Wav => ".wav",
        OutputFormat.Flac => ".flac",
        OutputFormat.Ogg => ".ogg",
        OutputFormat.Opus => ".opus",
        OutputFormat.Mp4 => ".mp4",
        OutputFormat.Mkv => ".mkv",
        _ => "",
    };
}

/// <summary>
/// Settings that apply to every file in the batch. Simple mode uses <see cref="Simple"/>: only the
/// dB change, with clipping protection on.
/// </summary>
public sealed record BoostSettings
{
    public double GainDb { get; init; } = 6;

    /// <summary>Soft limiter after the gain so peaks do not crunch.</summary>
    public bool PreventClipping { get; init; } = true;

    /// <summary>Highest level the limiter lets through, in dBFS (-24 to 0).</summary>
    public double CeilingDb { get; init; } = -1;

    public AutoLevel AutoLevel { get; init; } = AutoLevel.Off;

    /// <summary>LUFS for <see cref="AutoLevel.Loudness"/>, dBFS for <see cref="AutoLevel.Peak"/>.</summary>
    public double AutoLevelTarget { get; init; } = -14;

    public double LeftDb { get; init; }
    public double RightDb { get; init; }
    public double BassDb { get; init; }
    public double TrebleDb { get; init; }
    public double FadeInSeconds { get; init; }
    public double FadeOutSeconds { get; init; }

    public OutputFormat Format { get; init; } = OutputFormat.SameAsSource;

    /// <summary>Bitrate for lossy encoders; null keeps about what the original had.</summary>
    public int? BitrateKbps { get; init; }

    /// <summary>Where results go; null puts each one next to its original.</summary>
    public string? OutputFolder { get; init; }

    public static BoostSettings Simple(double gainDb, string? outputFolder = null) =>
        new() { GainDb = gainDb, PreventClipping = true, OutputFolder = outputFolder };

    public double ClampedCeilingDb => Math.Clamp(CeilingDb, -24, 0);
}

/// <summary>A stretch of time that gets its own extra dB change.</summary>
public sealed record GainSegment(double StartSeconds, double EndSeconds, double GainDb)
{
    public double Length => EndSeconds - StartSeconds;

    public bool IsValid => double.IsFinite(StartSeconds) && double.IsFinite(EndSeconds) && EndSeconds - StartSeconds >= 0.01;
}

/// <summary>Per-file choices (advanced mode only).</summary>
public sealed record FileEdits
{
    public static readonly FileEdits None = new();

    /// <summary>Overrides <see cref="BoostSettings.GainDb"/> for this file.</summary>
    public double? GainDb { get; init; }

    public IReadOnlyList<GainSegment> Segments { get; init; } = [];

    /// <summary>Which audio tracks (by ordinal) get processed; the rest are copied untouched. Null means all.</summary>
    public IReadOnlySet<int>? BoostedTracks { get; init; }

    public double EffectiveGain(BoostSettings s) => GainDb ?? s.GainDb;
}
