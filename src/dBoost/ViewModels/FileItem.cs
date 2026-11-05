using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using DBoost.Core;

namespace DBoost.App.ViewModels;

public enum FileState { Reading, Ready, Waiting, Working, Done, Skipped, Failed }

/// <summary>One file in the list.</summary>
public sealed class FileItem : ObservableObject
{
    private ImageSource? thumbnail;
    private MediaInfo? info;
    private FileState state = FileState.Reading;
    private double progress;
    private bool isIndeterminate;
    private string? message;
    private string? outputPath;
    private bool useOwnGain;
    private double ownGainDb = 6;
    private TrackItem? waveformTrack;
    private Waveform? waveform;
    private bool waveformLoading;
    private string? waveformError;
    private string? analysisText;
    private double? headroomDb;
    private double cursorSeconds;
    private bool canRetry;

    public FileItem(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        Folder = System.IO.Path.GetDirectoryName(path) ?? "";
        IsVideo = MediaExtensions.IsVideoExtension(path);
        Segments.CollectionChanged += (_, _) => Raise(nameof(HasSegments));
    }

    public string Path { get; }
    public string Name { get; }
    public string Folder { get; }

    public ImageSource? Thumbnail { get => thumbnail; set => Set(ref thumbnail, value); }

    public bool IsVideo { get; private set; }

    public MediaInfo? Info
    {
        get => info;
        set
        {
            if (!Set(ref info, value) || value is null) return;
            IsVideo = value.IsVideo;
            Raise(nameof(IsVideo));
            Raise(nameof(Details));
            Tracks.Clear();
            foreach (var a in value.AudioStreams)
            {
                var t = new TrackItem(a);
                t.PropertyChanged += (_, _) => Raise(nameof(TracksSummary));
                Tracks.Add(t);
            }
            WaveformTrack = Tracks.FirstOrDefault();
            Raise(nameof(HasMultipleTracks));
            Raise(nameof(TracksSummary));
        }
    }

    /// <summary>"MP4 video · 2:31 · 2 audio tracks".</summary>
    public string Details
    {
        get
        {
            string ext = System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();
            if (info is null) return $"{ext} {(IsVideo ? "video" : "audio")}";
            var parts = new List<string> { $"{ext} {(info.IsVideo ? "video" : "audio")}" };
            if (info.Duration is { } d) parts.Add(FormatDuration(d));
            if (info.AudioStreams.Count > 1) parts.Add($"{info.AudioStreams.Count} audio tracks");
            else if (info.AudioStreams.Count == 1) parts.Add(info.AudioStreams[0].ChannelLayout);
            return string.Join(" · ", parts);
        }
    }

    public FileState State
    {
        get => state;
        set
        {
            if (!Set(ref state, value)) return;
            Raise(nameof(StatusText));
            Raise(nameof(ShowProgress));
            Raise(nameof(IsDone));
            Raise(nameof(IsFailed));
            Raise(nameof(IsUsable));
            Raise(nameof(WaveformMessage));
        }
    }

    public double Progress { get => progress; set => Set(ref progress, value); }
    public bool IsIndeterminate { get => isIndeterminate; set => Set(ref isIndeterminate, value); }

    /// <summary>Error text, or notes about what happened (shown under the name).</summary>
    public string? Message { get => message; set { if (Set(ref message, value)) Raise(nameof(HasMessage)); } }
    public bool HasMessage => !string.IsNullOrEmpty(message);

    public string? OutputPath
    {
        get => outputPath;
        set { if (Set(ref outputPath, value)) Raise(nameof(OutputName)); }
    }

    public string? OutputName => outputPath is null ? null : System.IO.Path.GetFileName(outputPath);

    /// <summary>A failure while boosting (not while reading), which pressing Boost again can retry.</summary>
    public bool CanRetry { get => canRetry; set { if (Set(ref canRetry, value)) Raise(nameof(IsUsable)); } }

    public bool ShowProgress => state is FileState.Waiting or FileState.Working;
    public bool IsDone => state == FileState.Done;
    public bool IsFailed => state == FileState.Failed;
    public bool IsUsable => info is not null && state != FileState.Reading && !(state == FileState.Failed && !canRetry);

    public string StatusText => state switch
    {
        FileState.Reading => "Reading...",
        FileState.Ready => "",
        FileState.Waiting => "Waiting",
        FileState.Working => "Working",
        FileState.Done => "Saved",
        FileState.Skipped => "Nothing to change",
        FileState.Failed => "Failed",
        _ => "",
    };

    // ------------------------------------------------------------- advanced, per file

    public bool UseOwnGain { get => useOwnGain; set => Set(ref useOwnGain, value); }
    public double OwnGainDb { get => ownGainDb; set => Set(ref ownGainDb, value); }

    public ObservableCollection<SegmentItem> Segments { get; } = [];
    public bool HasSegments => Segments.Count > 0;

    public ObservableCollection<TrackItem> Tracks { get; } = [];
    public bool HasMultipleTracks => Tracks.Count > 1;

    public string TracksSummary
    {
        get
        {
            int on = Tracks.Count(t => t.IsBoosted);
            return on == Tracks.Count ? "All tracks" : $"{on} of {Tracks.Count} tracks";
        }
    }

    /// <summary>Which track the waveform and preview use.</summary>
    public TrackItem? WaveformTrack
    {
        get => waveformTrack;
        set
        {
            if (!Set(ref waveformTrack, value)) return;
            Waveform = null;
            WaveformError = null;
            AnalysisText = null;
            HeadroomDb = null;
        }
    }

    public Waveform? Waveform { get => waveform; set => Set(ref waveform, value); }
    public bool WaveformLoading { get => waveformLoading; set { if (Set(ref waveformLoading, value)) Raise(nameof(WaveformMessage)); } }
    public string? WaveformError { get => waveformError; set { if (Set(ref waveformError, value)) Raise(nameof(WaveformMessage)); } }

    /// <summary>What the waveform area says while there is no waveform to draw.</summary>
    public string? WaveformMessage =>
        waveformError ?? (waveformLoading ? "Reading the waveform..." : state == FileState.Failed && info is null ? "This file could not be read." : null);

    public string? AnalysisText { get => analysisText; set => Set(ref analysisText, value); }

    /// <summary>From the last analysis: how far the selected track can go up before clipping.</summary>
    public double? HeadroomDb { get => headroomDb; set { if (Set(ref headroomDb, value)) Raise(nameof(CanUseHeadroom)); } }
    public bool CanUseHeadroom => headroomDb is > 0.05;

    /// <summary>Where a preview starts (set by clicking the waveform).</summary>
    public double CursorSeconds { get => cursorSeconds; set => Set(ref cursorSeconds, Math.Max(0, value)); }

    public Dictionary<string, LoudnessAnalysis> AnalysisCache { get; } = [];

    /// <summary>The per-file choices, for advanced mode.</summary>
    public FileEdits ToEdits() => new()
    {
        GainDb = useOwnGain ? ownGainDb : null,
        Segments = Segments.Select(s => s.ToSegment()).Where(s => s.IsValid).ToList(),
        BoostedTracks = Tracks.All(t => t.IsBoosted) ? null : Tracks.Where(t => t.IsBoosted).Select(t => t.Info.Ordinal).ToHashSet(),
    };

    public static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}" : $"{d.Minutes}:{d.Seconds:00}";
}
