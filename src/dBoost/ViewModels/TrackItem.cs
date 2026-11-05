using DBoost.Core;

namespace DBoost.App.ViewModels;

/// <summary>One audio track of a file, with a "boost this one" tick (advanced mode).</summary>
public sealed class TrackItem(AudioStreamInfo info) : ObservableObject
{
    private bool isBoosted = true;

    public AudioStreamInfo Info { get; } = info;

    public string Label => Info.Describe();

    /// <summary>"Track 2 (Mic)", for the waveform's track picker.</summary>
    public string ShortLabel
    {
        get
        {
            string? name = Info.Title ?? (string.IsNullOrEmpty(Info.Language) || Info.Language == "und" ? null : Info.Language);
            return name is null ? $"Track {Info.Ordinal + 1}" : $"Track {Info.Ordinal + 1} ({name})";
        }
    }

    public bool IsBoosted
    {
        get => isBoosted;
        set => Set(ref isBoosted, value);
    }
}
