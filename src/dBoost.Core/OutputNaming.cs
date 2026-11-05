namespace DBoost.Core;

/// <summary>Names results "song (+6 dB).mp3" next to the original (or in a chosen folder), never overwriting anything.</summary>
public static class OutputNaming
{
    public static string Label(BoostSettings s, FileEdits e)
    {
        if (s.AutoLevel == AutoLevel.Loudness) return $"{Decibels.FormatNumber(s.AutoLevelTarget)} LUFS";
        if (s.AutoLevel == AutoLevel.Peak) return "max volume";
        double gain = e.EffectiveGain(s);
        if (Math.Abs(gain) >= 0.05) return Decibels.Format(gain);
        return "edited";
    }

    /// <summary>A path that does not exist yet: "name (label).ext", then "name (label) (2).ext" and so on.</summary>
    public static string UniquePath(string inputPath, string? outputFolder, string extension, string label, Func<string, bool>? isTaken = null)
    {
        string folder = string.IsNullOrWhiteSpace(outputFolder)
            ? Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? "."
            : outputFolder;
        string stem = Path.GetFileNameWithoutExtension(inputPath);
        string baseName = $"{stem} ({label})";

        string candidate = Path.Combine(folder, baseName + extension);
        for (int n = 2; File.Exists(candidate) || Directory.Exists(candidate) || SameFile(candidate, inputPath) || isTaken?.Invoke(candidate) == true; n++)
            candidate = Path.Combine(folder, $"{baseName} ({n}){extension}");
        return candidate;
    }

    /// <summary>Where ffmpeg writes while it works; renamed to the real name only once it succeeded.</summary>
    public static string PartialPath(string finalPath) =>
        Path.Combine(Path.GetDirectoryName(finalPath) ?? ".",
            Path.GetFileNameWithoutExtension(finalPath) + ".dboost-partial" + Path.GetExtension(finalPath));

    private static bool SameFile(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
