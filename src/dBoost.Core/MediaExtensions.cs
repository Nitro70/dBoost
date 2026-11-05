namespace DBoost.Core;

/// <summary>File types dBoost recognises when a folder is dropped, and which ones ffmpeg can write back.</summary>
public static class MediaExtensions
{
    public static readonly IReadOnlySet<string> Audio = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".m4a", ".m4b", ".aac", ".ogg", ".oga", ".opus", ".wma", ".aiff", ".aif",
        ".aifc", ".ape", ".wv", ".ac3", ".eac3", ".dts", ".mka", ".amr", ".au", ".caf", ".mp2", ".mpa",
        ".tta", ".spx", ".weba", ".w64", ".mpc", ".tak", ".dsf", ".dff", ".ra", ".voc", ".shn",
    };

    public static readonly IReadOnlySet<string> Video = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".flv", ".mpg", ".mpeg", ".ts", ".m2ts",
        ".mts", ".3gp", ".3g2", ".ogv", ".vob", ".mxf", ".asf", ".f4v", ".divx", ".rm", ".rmvb", ".dv",
        ".mod", ".tod", ".wtv",
    };

    /// <summary>Extensions ffmpeg picks a muxer for on its own.</summary>
    public static readonly IReadOnlySet<string> Writable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".m4a", ".m4b", ".aac", ".ogg", ".oga", ".opus", ".wma", ".aiff", ".aif",
        ".aifc", ".wv", ".ac3", ".eac3", ".dts", ".mka", ".amr", ".au", ".caf", ".mp2", ".mpa", ".tta",
        ".w64", ".mp4", ".m4v", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".flv", ".mpg", ".mpeg", ".ts",
        ".m2ts", ".mts", ".3gp", ".3g2", ".ogv", ".vob", ".asf", ".f4v",
    };

    public static bool IsMedia(string path)
    {
        string ext = Path.GetExtension(path);
        return Audio.Contains(ext) || Video.Contains(ext);
    }

    public static bool IsVideoExtension(string path) => Video.Contains(Path.GetExtension(path));

    /// <summary>Files from a drop or paste: files as given, folders searched for media (subfolders too).</summary>
    public static IEnumerable<string> Expand(IEnumerable<string> paths)
    {
        foreach (string p in paths)
        {
            if (File.Exists(p))
            {
                yield return Path.GetFullPath(p);
            }
            else if (Directory.Exists(p))
            {
                IEnumerable<string> found;
                try
                {
                    found = Directory.EnumerateFiles(p, "*", new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                    }).Where(IsMedia).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToList();
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                foreach (string f in found) yield return f;
            }
        }
    }
}
