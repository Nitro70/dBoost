using System.Globalization;
using System.Text.RegularExpressions;

namespace DBoost.Core;

public sealed class MediaProbeException(string message) : Exception(message);

/// <summary>
/// Reads the stream summary ffmpeg prints for <c>ffmpeg -i file</c>. This avoids shipping ffprobe
/// (another 130 MB) for the handful of facts the app needs.
/// </summary>
public static partial class MediaProbe
{
    [GeneratedRegex(@"^Input #0, (?<fmt>.+?), from '", RegexOptions.CultureInvariant)]
    private static partial Regex InputLine();

    [GeneratedRegex(@"^\s*Duration: (?<d>N/A|\d+:\d{2}:\d{2}(?:\.\d+)?)(?:.*?bitrate: (?<br>\d+) kb/s)?", RegexOptions.CultureInvariant)]
    private static partial Regex DurationLine();

    [GeneratedRegex(@"^(?<indent>\s*)Stream #0:(?<idx>\d+)(?:\[[^\]]*\])?(?:\((?<lang>[^)]*)\))?(?:\[[^\]]*\])?: (?<type>[A-Za-z]+): (?<rest>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex StreamLine();

    [GeneratedRegex(@"^(?<indent>\s+)(?<key>[A-Za-z0-9_\-]+)\s*: ?(?<val>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex MetadataLine();

    [GeneratedRegex(@"^(?<n>\d+) channels$", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelsCount();

    [GeneratedRegex(@"^(?<fmt>[a-z0-9]+)(?:\s*\((?<bits>\d+) bit\))?", RegexOptions.CultureInvariant)]
    private static partial Regex SampleFormatPart();

    [GeneratedRegex(@"\((default|forced|dub|original|comment|lyrics|karaoke|hearing impaired|visual impaired|clean effects|attached pic|timed thumbnails|non-diegetic|captions|descriptions|metadata|dependent|still image|multilayer)\)", RegexOptions.CultureInvariant)]
    private static partial Regex Disposition();

    private static readonly Dictionary<string, int> LayoutChannels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mono"] = 1, ["stereo"] = 2, ["downmix"] = 2, ["binaural"] = 2, ["2.1"] = 3, ["3.0"] = 3,
        ["3.1"] = 4, ["4.0"] = 4, ["quad"] = 4, ["4.1"] = 5, ["5.0"] = 5, ["5.1"] = 6, ["6.0"] = 6,
        ["hexagonal"] = 6, ["6.1"] = 7, ["7.0"] = 7, ["7.1"] = 8, ["octagonal"] = 8, ["cube"] = 8,
        ["5.1.2"] = 8, ["5.1.4"] = 10, ["7.1.2"] = 10, ["7.1.4"] = 12, ["7.2.3"] = 12, ["9.1.4"] = 14,
        ["hexadecagonal"] = 16, ["22.2"] = 24,
    };

    /// <summary>Parses the text ffmpeg writes to stderr. Throws <see cref="MediaProbeException"/> when it found no input.</summary>
    public static MediaInfo Parse(string stderr, string path)
    {
        string container = "";
        TimeSpan? duration = null;
        int? totalBitrate = null;
        var audio = new List<AudioStreamInfo>();
        var video = new List<string>();
        int? coverOrdinal = null;
        int videoOrdinal = 0, subtitles = 0, other = 0;
        bool sawInput = false;

        // Metadata lines belong to the last stream when they are indented deeper than its "Stream #" line.
        int currentStreamIndent = -1;
        int currentAudio = -1;

        foreach (string raw in stderr.Split('\n'))
        {
            string line = raw.TrimEnd('\r');

            var input = InputLine().Match(line);
            if (input.Success)
            {
                if (sawInput) break; // only the first input matters
                sawInput = true;
                container = input.Groups["fmt"].Value;
                continue;
            }
            if (!sawInput) continue;

            var dur = DurationLine().Match(line);
            if (dur.Success)
            {
                currentStreamIndent = -1;
                currentAudio = -1;
                string d = dur.Groups["d"].Value;
                if (d != "N/A" && TimeSpan.TryParse(d, CultureInfo.InvariantCulture, out var ts) && ts > TimeSpan.Zero)
                    duration = ts;
                if (dur.Groups["br"].Success && int.TryParse(dur.Groups["br"].Value, out int br))
                    totalBitrate = br;
                continue;
            }

            var stream = StreamLine().Match(line);
            if (stream.Success)
            {
                currentStreamIndent = stream.Groups["indent"].Length;
                currentAudio = -1;
                int index = int.Parse(stream.Groups["idx"].Value, CultureInfo.InvariantCulture);
                string type = stream.Groups["type"].Value;
                string rest = stream.Groups["rest"].Value;
                string? lang = stream.Groups["lang"].Success ? stream.Groups["lang"].Value : null;

                switch (type)
                {
                    case "Audio":
                        audio.Add(ParseAudio(index, audio.Count, lang, rest));
                        currentAudio = audio.Count - 1;
                        break;
                    case "Video":
                        if (rest.Contains("(attached pic)", StringComparison.Ordinal))
                            coverOrdinal ??= videoOrdinal;
                        else
                            video.Add(FirstToken(rest));
                        videoOrdinal++;
                        break;
                    case "Subtitle":
                        subtitles++;
                        break;
                    default:
                        other++;
                        break;
                }
                continue;
            }

            if (currentAudio >= 0)
            {
                var meta = MetadataLine().Match(line);
                if (meta.Success && meta.Groups["indent"].Length > currentStreamIndent + 2)
                {
                    string key = meta.Groups["key"].Value;
                    string val = meta.Groups["val"].Value.Trim();
                    var a = audio[currentAudio];
                    if (key.Equals("title", StringComparison.OrdinalIgnoreCase) && val.Length > 0)
                        audio[currentAudio] = a with { Title = val };
                    else if (key.Equals("handler_name", StringComparison.OrdinalIgnoreCase) && a.Title is null && IsMeaningfulHandler(val))
                        audio[currentAudio] = a with { Title = val };
                }
                else if (!line.StartsWith(new string(' ', currentStreamIndent + 1), StringComparison.Ordinal))
                {
                    currentAudio = -1;
                }
            }
        }

        if (!sawInput)
            throw new MediaProbeException(DescribeFailure(stderr, path));

        return new MediaInfo(path, container, duration, totalBitrate, audio, video, coverOrdinal, subtitles, other);
    }

    private static bool IsMeaningfulHandler(string v) =>
        v.Length > 0
        && !v.Contains("Handler", StringComparison.OrdinalIgnoreCase)
        && !v.StartsWith("Core Media", StringComparison.OrdinalIgnoreCase)
        && !v.Equals("ISO Media file produced by Google Inc.", StringComparison.OrdinalIgnoreCase);

    private static AudioStreamInfo ParseAudio(int index, int ordinal, string? lang, string rest)
    {
        bool isDefault = rest.Contains("(default)", StringComparison.Ordinal);
        string cleaned = Disposition().Replace(rest, "").Trim();
        var parts = SplitTopLevel(cleaned);

        string codec = parts.Count > 0 ? FirstToken(parts[0]) : "unknown";
        int rate = 0, channels = 2, bits = 0;
        string layout = "stereo", fmt = "";
        int? bitrate = null;

        int hzAt = parts.FindIndex(p => p.EndsWith(" Hz", StringComparison.Ordinal));
        if (hzAt >= 0)
        {
            int.TryParse(parts[hzAt].AsSpan(0, parts[hzAt].Length - 3), NumberStyles.Integer, CultureInfo.InvariantCulture, out rate);
            if (hzAt + 1 < parts.Count)
            {
                layout = parts[hzAt + 1].Trim();
                channels = ChannelCount(layout);
            }
            if (hzAt + 2 < parts.Count)
            {
                var m = SampleFormatPart().Match(parts[hzAt + 2].Trim());
                if (m.Success)
                {
                    fmt = m.Groups["fmt"].Value;
                    bits = m.Groups["bits"].Success
                        ? int.Parse(m.Groups["bits"].Value, CultureInfo.InvariantCulture)
                        : BitsOf(fmt);
                }
            }
        }

        foreach (string p in parts)
        {
            if (p.EndsWith(" kb/s", StringComparison.Ordinal)
                && int.TryParse(p.AsSpan(0, p.Length - 5), NumberStyles.Integer, CultureInfo.InvariantCulture, out int br)
                && br > 0)
            {
                bitrate = br;
            }
        }

        return new AudioStreamInfo(index, ordinal, codec, rate, channels, layout, fmt, bits, bitrate,
            string.IsNullOrWhiteSpace(lang) ? null : lang, null, isDefault);
    }

    internal static int ChannelCount(string layout)
    {
        string l = layout.Trim();
        var m = ChannelsCount().Match(l);
        if (m.Success) return int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
        int paren = l.IndexOf('(');
        if (paren > 0) l = l[..paren];
        return LayoutChannels.TryGetValue(l, out int n) ? n : 2;
    }

    private static int BitsOf(string fmt) => fmt switch
    {
        "u8" or "u8p" => 8,
        "s16" or "s16p" => 16,
        "s32" or "s32p" or "flt" or "fltp" => 32,
        "s64" or "s64p" or "dbl" or "dblp" => 64,
        _ => 0,
    };

    private static string FirstToken(string s)
    {
        int end = 0;
        while (end < s.Length && (char.IsLetterOrDigit(s[end]) || s[end] == '_')) end++;
        return end == 0 ? s.Trim() : s[..end];
    }

    /// <summary>Splits on ", " but not inside brackets: "yuv420p(tv, bt709)" stays one part.</summary>
    internal static List<string> SplitTopLevel(string s)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth = Math.Max(0, depth - 1);
            else if (c == ',' && depth == 0)
            {
                parts.Add(s[start..i].Trim());
                start = i + 1;
            }
        }
        parts.Add(s[start..].Trim());
        parts.RemoveAll(p => p.Length == 0);
        return parts;
    }

    private static string DescribeFailure(string stderr, string path)
    {
        string fileName = System.IO.Path.GetFileName(path);
        var lines = stderr.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("At least one output file", StringComparison.Ordinal))
            .ToList();
        string last = lines.LastOrDefault() ?? "";

        if (last.Contains("No such file", StringComparison.OrdinalIgnoreCase)) return "The file is missing or was moved.";
        if (last.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)) return "Windows would not let dBoost read this file.";
        if (last.Contains("Invalid data", StringComparison.OrdinalIgnoreCase)) return "This is not an audio or video file dBoost can read.";

        // Drop the "C:\path\file.ext: " prefix ffmpeg puts before its message.
        int colon = last.IndexOf(fileName + ":", StringComparison.OrdinalIgnoreCase);
        if (colon >= 0) last = last[(colon + fileName.Length + 1)..].Trim();
        return last.Length > 0 ? last : "ffmpeg could not open this file.";
    }
}
