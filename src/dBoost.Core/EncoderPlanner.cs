namespace DBoost.Core;

/// <summary>How one boosted track is written.</summary>
/// <param name="Encoder">ffmpeg encoder name.</param>
/// <param name="BitrateKbps">For lossy encoders; null for lossless and PCM.</param>
/// <param name="FormatFilter">An <c>aformat</c> filter that keeps the original bit depth, if needed.</param>
/// <param name="ExtraArgs">Encoder options, written with a <c>:a:N</c> stream specifier by the caller.</param>
public sealed record EncoderChoice(string Encoder, int? BitrateKbps, string? FormatFilter, IReadOnlyList<(string Option, string Value)> ExtraArgs)
{
    public bool IsLossless => BitrateKbps is null;
}

/// <summary>Picks the encoder for a boosted track: the same codec it had, or a sensible stand-in.</summary>
public sealed class EncoderPlanner(IReadOnlySet<string> availableEncoders)
{
    private static readonly HashSet<string> LosslessCodecs = new(StringComparer.Ordinal)
    {
        "flac", "alac", "wavpack", "tta", "ape", "wmalossless", "tak", "shorten", "mlp", "truehd", "als",
        "dsd_lsbf", "dsd_msbf", "dsd_lsbf_planar", "dsd_msbf_planar", "mp4als",
    };

    /// <param name="HighKbps">Used when the source is lossless, so nothing is lost that does not have to be.</param>
    private sealed record Lossy(string Encoder, int DefaultKbps, int MaxKbps, int HighKbps);

    private static readonly Dictionary<string, Lossy> LossyByCodec = new(StringComparer.Ordinal)
    {
        ["mp3"] = new("libmp3lame", 192, 320, 320),
        ["aac"] = new("aac", 192, 512, 256),
        ["opus"] = new("libopus", 128, 510, 192),
        ["vorbis"] = new("libvorbis", 192, 500, 256),
        ["ac3"] = new("ac3", 448, 640, 640),
        ["eac3"] = new("eac3", 640, 6144, 1024),
        ["dts"] = new("dca", 1509, 3840, 1509),
        ["mp2"] = new("mp2", 256, 384, 384),
        ["wmav1"] = new("wmav2", 192, 320, 320),
        ["wmav2"] = new("wmav2", 192, 320, 320),
    };

    private static readonly Dictionary<string, string> LosslessByCodec = new(StringComparer.Ordinal)
    {
        ["flac"] = "flac", ["alac"] = "alac", ["wavpack"] = "wavpack", ["tta"] = "tta",
    };

    public bool Has(string encoder) => availableEncoders.Contains(encoder);

    public static bool IsLosslessCodec(string codec) => LosslessCodecs.Contains(codec) || codec.StartsWith("pcm_", StringComparison.Ordinal);

    /// <summary>The same codec the track already had, or null when this ffmpeg cannot write it.</summary>
    public EncoderChoice? SameCodec(AudioStreamInfo track, int? userKbps)
    {
        string codec = track.Codec;
        if (codec.StartsWith("pcm_", StringComparison.Ordinal))
            return Has(codec) ? new EncoderChoice(codec, null, null, []) : null;

        if (LosslessByCodec.TryGetValue(codec, out var lossless))
            return Has(lossless) ? Lossless(lossless, track) : null;

        if (LossyByCodec.TryGetValue(codec, out var lossy) && Has(lossy.Encoder))
            return LossyChoice(lossy, track, userKbps);

        if (codec == "amr_nb" && Has("libopencore_amrnb"))
            return new EncoderChoice("libopencore_amrnb", null, "aresample=8000,aformat=channel_layouts=mono", []);

        return null;
    }

    /// <summary>The encoder for an explicit output format.</summary>
    public EncoderChoice ForFormat(OutputFormat format, AudioStreamInfo track, int? userKbps) => format switch
    {
        OutputFormat.Mp3 => LossyChoice(LossyByCodec["mp3"], track, userKbps, fromLossless: IsLosslessCodec(track.Codec)),
        OutputFormat.M4a => LossyChoice(LossyByCodec["aac"], track, userKbps, fromLossless: IsLosslessCodec(track.Codec)),
        OutputFormat.Ogg => LossyChoice(LossyByCodec["vorbis"], track, userKbps, fromLossless: IsLosslessCodec(track.Codec)),
        OutputFormat.Opus => LossyChoice(LossyByCodec["opus"], track, userKbps, fromLossless: IsLosslessCodec(track.Codec)),
        OutputFormat.Flac => Lossless("flac", track),
        OutputFormat.Wav => Pcm(track),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Not an audio format."),
    };

    /// <summary>
    /// Audio for a video being kept in (or moved to) <paramref name="containerExt"/>: the original
    /// codec when it can be written and the container takes it, otherwise the container's usual codec.
    /// </summary>
    public EncoderChoice ForVideoContainer(string containerExt, AudioStreamInfo track, int? userKbps, bool allowSameCodec = true)
    {
        if (allowSameCodec && ContainerAccepts(containerExt, track.Codec))
        {
            var same = SameCodec(track, userKbps);
            if (same is not null) return same;
        }
        string fallback = containerExt switch
        {
            ".webm" => "opus",
            ".avi" => "mp3",
            ".wmv" or ".asf" => "wmav2",
            ".mpg" or ".mpeg" or ".vob" => "ac3",
            ".ogv" => "vorbis",
            _ => "aac",
        };
        return LossyChoice(LossyByCodec[fallback], track, userKbps, fromLossless: IsLosslessCodec(track.Codec));
    }

    /// <summary>
    /// For an audio file whose own codec or container cannot be written (APE, WMA Lossless, ...):
    /// FLAC for lossless sources, MP3 for lossy ones.
    /// </summary>
    public (EncoderChoice Choice, string Extension) AudioFallback(AudioStreamInfo track, int? userKbps) =>
        IsLosslessCodec(track.Codec)
            ? (Lossless("flac", track), ".flac")
            : (LossyChoice(LossyByCodec["mp3"], track, userKbps ?? 256), ".mp3");

    /// <summary>Whether a container can hold a codec (only the cases that matter here).</summary>
    public static bool ContainerAccepts(string ext, string codec) => ext switch
    {
        ".mp4" or ".m4v" or ".mov" or ".3gp" or ".3g2" or ".f4v" => codec is "aac" or "mp3" or "ac3" or "eac3" or "alac" or "flac" or "opus" || (ext == ".mov" && codec.StartsWith("pcm_", StringComparison.Ordinal)),
        ".webm" => codec is "opus" or "vorbis",
        ".ogv" => codec is "vorbis" or "opus" or "flac",
        ".flv" => codec is "aac" or "mp3",
        ".wmv" or ".asf" => codec is "wmav1" or "wmav2" or "mp3",
        _ => true, // mkv, avi, ts and friends take nearly anything
    };

    private EncoderChoice LossyChoice(Lossy l, AudioStreamInfo track, int? userKbps, bool fromLossless = false)
    {
        int kbps;
        if (userKbps is > 0) kbps = userKbps.Value;
        else if (fromLossless) kbps = l.HighKbps;
        else if (track.BitrateKbps is > 0)
        {
            // Round up to a multiple of 32 so a re-encode never ends up thinner than the original.
            kbps = (int)Math.Ceiling(track.BitrateKbps.Value / 32.0) * 32;
            kbps = Math.Max(kbps, 128);
        }
        else kbps = l.DefaultKbps;

        kbps = Math.Clamp(kbps, 32, l.MaxKbps);
        var extra = new List<(string, string)>();
        if (l.Encoder == "dca") extra.Add(("strict", "-2"));
        return new EncoderChoice(l.Encoder, kbps, null, extra);
    }

    private static EncoderChoice Lossless(string encoder, AudioStreamInfo track)
    {
        bool deep = track.BitsPerSample > 16 && IsLosslessCodec(track.Codec);
        string formats = deep ? "s32|s32p" : "s16|s16p";
        var extra = new List<(string, string)>();
        if (deep && track.BitsPerSample == 24 && encoder is "flac" or "alac")
            extra.Add(("bits_per_raw_sample", "24"));
        return new EncoderChoice(encoder, null, $"aformat=sample_fmts={formats}", extra);
    }

    private EncoderChoice Pcm(AudioStreamInfo track)
    {
        string codec;
        if (track.Codec is "pcm_f32le" or "pcm_f32be" or "pcm_f64le") codec = "pcm_f32le";
        else if (IsLosslessCodec(track.Codec) && track.BitsPerSample >= 24 && !track.IsFloat) codec = "pcm_s24le";
        else codec = "pcm_s16le";
        if (track.Codec.StartsWith("pcm_", StringComparison.Ordinal) && track.BitsPerSample == 32 && !track.IsFloat) codec = "pcm_s32le";
        return new EncoderChoice(codec, null, null, []);
    }
}
