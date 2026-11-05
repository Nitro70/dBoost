using System.IO;
using System.Text.Json;
using DBoost.Core;

namespace DBoost.App.Services;

/// <summary>What dBoost remembers between runs, in %APPDATA%\dBoost\settings.json.</summary>
public sealed class AppSettings
{
    public double GainDb { get; set; } = 6;
    public bool Advanced { get; set; }

    public bool PreventClipping { get; set; } = true;
    public double CeilingDb { get; set; } = -1;
    public AutoLevel AutoLevel { get; set; } = AutoLevel.Off;
    public double LoudnessTarget { get; set; } = -14;
    public double PeakTarget { get; set; } = -1;
    public double LeftDb { get; set; }
    public double RightDb { get; set; }
    public double BassDb { get; set; }
    public double TrebleDb { get; set; }
    public double FadeInSeconds { get; set; }
    public double FadeOutSeconds { get; set; }
    public OutputFormat Format { get; set; } = OutputFormat.SameAsSource;
    public int BitrateKbps { get; set; }

    public bool UseOutputFolder { get; set; }
    public string? OutputFolder { get; set; }

    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public bool WindowMaximized { get; set; }
}

public static class SettingsStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "dBoost", "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new AppSettings();
    }

    public static void Save(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(s, Json));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
