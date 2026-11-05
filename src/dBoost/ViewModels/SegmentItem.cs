using System.Globalization;
using DBoost.Core;

namespace DBoost.App.ViewModels;

/// <summary>A boosted stretch of one file (advanced mode), drawn on the waveform and listed under it.</summary>
public sealed class SegmentItem : ObservableObject
{
    private double start, end, gainDb;

    public SegmentItem(double start, double end, double gainDb)
    {
        this.start = Math.Min(start, end);
        this.end = Math.Max(start, end);
        this.gainDb = gainDb;
    }

    public double Start
    {
        get => start;
        set { if (Set(ref start, Math.Max(0, value))) { Raise(nameof(StartText)); Raise(nameof(LengthText)); } }
    }

    public double End
    {
        get => end;
        set { if (Set(ref end, Math.Max(0, value))) { Raise(nameof(EndText)); Raise(nameof(LengthText)); } }
    }

    public double GainDb
    {
        get => gainDb;
        set { if (Set(ref gainDb, value)) Raise(nameof(GainText)); }
    }

    public string GainText => Decibels.Format(GainDb);

    public string StartText
    {
        get => FormatTime(Start);
        set { if (TryParseTime(value, out double t) && t < End) Start = t; else Raise(); }
    }

    public string EndText
    {
        get => FormatTime(End);
        set { if (TryParseTime(value, out double t) && t > Start) End = t; else Raise(); }
    }

    public string LengthText => FormatTime(End - Start);

    public GainSegment ToSegment() => new(Start, End, GainDb);

    /// <summary>"1:05.2", "0:09.0", "1:02:03.5".</summary>
    public static string FormatTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(Math.Round(seconds, 1));
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 100}"
            : $"{t.Minutes}:{t.Seconds:00}.{t.Milliseconds / 100}";
    }

    /// <summary>Accepts "75", "75.5", "1:15", "1:15.5", "1:02:03".</summary>
    public static bool TryParseTime(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Replace(',', '.').Split(':');
        if (parts.Length > 3) return false;
        double total = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || v < 0 || !double.IsFinite(v))
                return false;
            total = total * 60 + v;
        }
        seconds = total;
        return true;
    }
}
