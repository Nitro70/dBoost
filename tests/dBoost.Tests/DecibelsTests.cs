using DBoost.Core;

namespace DBoost.Tests;

public class DecibelsTests
{
    [Theory]
    [InlineData("6", 6)]
    [InlineData("+6", 6)]
    [InlineData("-6", -6)]
    [InlineData(" - 6 ", -6)]
    [InlineData("6dB", 6)]
    [InlineData("-12.5 db", -12.5)]
    [InlineData("3,5", 3.5)]
    [InlineData("\u22129", -9)]     // typographic minus sign
    [InlineData("\u20134", -4)]     // dash pasted from a document
    [InlineData("250", 250)]        // no limit
    [InlineData("-1000", -1000)]
    public void Parses_what_people_type(string text, double expected)
    {
        Assert.True(Decibels.TryParse(text, out double db));
        Assert.Equal(expected, db, 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("dB")]
    public void Rejects_non_numbers(string text) => Assert.False(Decibels.TryParse(text, out _));

    [Theory]
    [InlineData(6, "+6 dB")]
    [InlineData(-3.5, "-3.5 dB")]
    [InlineData(0, "0 dB")]
    [InlineData(0.04, "0 dB")]
    [InlineData(12.25, "+12.3 dB")]
    [InlineData(-0.06, "-0.1 dB")]
    public void Formats_with_sign(double db, string expected) => Assert.Equal(expected, Decibels.Format(db));

    [Fact]
    public void Six_dB_doubles_the_amplitude() => Assert.Equal(1.9953, Decibels.ToAmplitude(6), 3);

    [Fact]
    public void Ten_dB_sounds_twice_as_loud() => Assert.Equal(2, Decibels.LoudnessFactor(10), 6);

    [Fact]
    public void Engine_clamps_absurd_values_instead_of_overflowing()
    {
        Assert.True(double.IsFinite(Decibels.ToAmplitude(1e9)));
        Assert.True(Decibels.ToAmplitude(-1e9) > 0);
        Assert.Equal(0, Decibels.Clamp(double.NaN));
    }

    [Fact]
    public void Ffmpeg_numbers_are_invariant_and_parseable()
    {
        Assert.Equal("1.995262", Decibels.Num(Decibels.ToAmplitude(6)));
        Assert.Equal("0", Decibels.Num(0));
        Assert.Contains("E", Decibels.Num(1e30));
        Assert.DoesNotContain(",", Decibels.Num(1234567.5));
    }

    [Theory]
    [InlineData(0, "No change")]
    [InlineData(10, "Sounds about 2x as loud")]
    [InlineData(-10, "Sounds about 0.5x as loud (quieter)")]
    public void Describes_loudness_in_plain_words(double db, string expected) => Assert.Equal(expected, Decibels.Describe(db));
}
