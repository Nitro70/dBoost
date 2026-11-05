namespace DBoost.Core;

/// <summary>One audio track inside a file.</summary>
/// <param name="StreamIndex">ffmpeg's absolute stream index (0:N).</param>
/// <param name="Ordinal">Position among the audio streams only (0:a:N), which is how tracks are addressed.</param>
public sealed record AudioStreamInfo(
    int StreamIndex,
    int Ordinal,
    string Codec,
    int SampleRate,
    int Channels,
    string ChannelLayout,
    string SampleFormat,
    int BitsPerSample,
    int? BitrateKbps,
    string? Language,
    string? Title,
    bool IsDefault)
{
    public bool IsFloat => SampleFormat.StartsWith("flt", StringComparison.Ordinal) || SampleFormat.StartsWith("dbl", StringComparison.Ordinal);

    /// <summary>"Track 2 (Mic): aac, stereo, 48 kHz".</summary>
    public string Describe()
    {
        string name = $"Track {Ordinal + 1}";
        string? label = Title ?? (string.IsNullOrEmpty(Language) || Language == "und" ? null : Language);
        if (!string.IsNullOrWhiteSpace(label)) name += $" ({label})";
        string rate = SampleRate > 0 ? $", {SampleRate / 1000.0:0.#} kHz" : "";
        return $"{name}: {Codec}, {ChannelLayout}{rate}";
    }
}

/// <summary>What ffmpeg found inside a file.</summary>
/// <param name="VideoCodecs">Real video streams only; album art is not counted here.</param>
/// <param name="CoverArtVideoOrdinal">The 0:v:N position of the first album-art picture, if any.</param>
public sealed record MediaInfo(
    string Path,
    string ContainerFormat,
    TimeSpan? Duration,
    int? TotalBitrateKbps,
    IReadOnlyList<AudioStreamInfo> AudioStreams,
    IReadOnlyList<string> VideoCodecs,
    int? CoverArtVideoOrdinal,
    int SubtitleStreamCount,
    int OtherStreamCount)
{
    /// <summary>True when there is real video, not just album art.</summary>
    public bool IsVideo => VideoCodecs.Count > 0;

    public bool HasCoverArt => CoverArtVideoOrdinal is not null;

    public double? DurationSeconds => Duration?.TotalSeconds;

    public string Extension => System.IO.Path.GetExtension(Path).ToLowerInvariant();
}
