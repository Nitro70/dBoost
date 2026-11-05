using System.Globalization;

namespace DBoost.Core;

/// <summary>Decibel math, parsing and display.</summary>
public static class Decibels
{
    /// <summary>
    /// The engine clamps gains to this size. There is no limit in the UI, but past roughly 600 dB a
    /// 32-bit float signal is already pure silence (or a full-scale square wave), and not much further
    /// the multiplication overflows to infinity, so bigger numbers cannot change the result.
    /// </summary>
    public const double EngineLimit = 600;

    public static double Clamp(double db) => double.IsNaN(db) ? 0 : Math.Clamp(db, -EngineLimit, EngineLimit);

    /// <summary>Amplitude multiplier for a gain: +6 dB is about x2.</summary>
    public static double ToAmplitude(double db) => Math.Pow(10, Clamp(db) / 20);

    public static double FromAmplitude(double amplitude) =>
        amplitude <= 0 ? double.NegativeInfinity : 20 * Math.Log10(amplitude);

    /// <summary>How much louder it sounds: +10 dB is heard as roughly twice as loud.</summary>
    public static double LoudnessFactor(double db) => Math.Pow(2, Clamp(db) / 10);

    /// <summary>"+6", "-3.5", "0".</summary>
    public static string FormatNumber(double db, int maxDecimals = 1)
    {
        if (double.IsNaN(db)) return "0";
        if (double.IsNegativeInfinity(db)) return "-inf";
        if (double.IsPositiveInfinity(db)) return "+inf";
        double rounded = Math.Round(db, maxDecimals, MidpointRounding.AwayFromZero);
        if (rounded == 0) return "0";
        string format = maxDecimals <= 0 ? "0" : "0." + new string('#', maxDecimals);
        string text = Math.Abs(rounded).ToString(format, CultureInfo.InvariantCulture);
        return (rounded > 0 ? "+" : "-") + text;
    }

    /// <summary>"+6 dB", "-3.5 dB", "0 dB".</summary>
    public static string Format(double db, int maxDecimals = 1) => FormatNumber(db, maxDecimals) + " dB";

    /// <summary>
    /// Reads what a person typed: "6", "+6", "-6", "6dB", "- 6 db", "6,5", and the typographic minus
    /// signs Word and phones like to insert. Rejects anything that is not a finite number.
    /// </summary>
    public static bool TryParse(string? text, out double db)
    {
        db = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string s = text.Trim()
            .Replace('\u2212', '-')  // minus sign
            .Replace('\u2013', '-')  // en dash
            .Replace('\u2014', '-')  // em dash
            .Replace('\uFE63', '-')  // small hyphen-minus
            .Replace('\uFF0D', '-'); // fullwidth hyphen-minus

        if (s.EndsWith("db", StringComparison.OrdinalIgnoreCase)) s = s[..^2];
        s = s.Replace(" ", "");
        if (s.Length == 0) return false;
        if (s.Contains(',') && !s.Contains('.')) s = s.Replace(',', '.');

        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) return false;
        if (!double.IsFinite(value)) return false;
        db = value;
        return true;
    }

    /// <summary>Plain-words hint shown under the gain box, e.g. "About 1.5x as loud".</summary>
    public static string Describe(double db)
    {
        if (Math.Abs(db) < 0.05) return "No change";
        double factor = LoudnessFactor(db);
        if (db > 0)
        {
            if (factor >= 1000) return "Extremely loud (expect heavy distortion)";
            return $"Sounds about {FormatFactor(factor)}x as loud";
        }
        if (factor <= 0.001) return "Practically silent";
        return $"Sounds about {FormatFactor(factor)}x as loud (quieter)";
    }

    private static string FormatFactor(double f) =>
        f >= 10 ? Math.Round(f).ToString("0", CultureInfo.InvariantCulture)
        : f >= 0.1 ? f.ToString("0.#", CultureInfo.InvariantCulture)
        : f.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Invariant, compact number for ffmpeg arguments.</summary>
    internal static string Num(double v)
    {
        if (v == 0) return "0";
        double a = Math.Abs(v);
        if (a >= 1e-4 && a < 1e9) return v.ToString("0.######", CultureInfo.InvariantCulture);
        return v.ToString("0.######E+0", CultureInfo.InvariantCulture);
    }
}
