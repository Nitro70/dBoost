using System.Diagnostics;
using DBoost.Core;

namespace DBoost.Tests;

/// <summary>Generates small test files with ffmpeg once, shared by the engine tests.</summary>
public sealed class MediaFixture : IAsyncLifetime
{
    public string Dir { get; } = Directory.CreateTempSubdirectory("dboost-tests-").FullName;
    public BoostEngine Engine { get; private set; } = null!;
    public string Ffmpeg { get; } = FindFfmpeg();

    /// <summary>A 440 Hz tone peaking at exactly -20 dBFS on both channels.</summary>
    public const double SourcePeakDb = -20;
    private const string Tone = "aevalsrc='0.1*sin(2*PI*440*t)|0.1*sin(2*PI*440*t)':s=44100:d=3";

    public string Wav => P("tone.wav");
    public string Mp3Cover => P("cover.mp3");
    public string Flac => P("tone.flac");
    public string Mp4 => P("clip.mp4");
    public string MultiMkv => P("multi.mkv");
    public string Unicode => P("tëst ♪ 日本.wav");
    public string Long => P("long.wav");

    public string P(string name) => Path.Combine(Dir, name);

    public string NewDir(string prefix) => Directory.CreateDirectory(P($"{prefix}-{Guid.NewGuid():N}")).FullName;

    public async Task InitializeAsync()
    {
        Engine = await BoostEngine.CreateAsync(Ffmpeg);
        Run("-f", "lavfi", "-i", Tone, "-c:a", "pcm_s16le", Wav);
        Run("-f", "lavfi", "-i", "color=c=red:s=64x64", "-frames:v", "1", P("cover.jpg"));
        Run("-i", Wav, "-i", P("cover.jpg"), "-map", "0", "-map", "1", "-c:a", "libmp3lame", "-b:a", "192k",
            "-c:v", "copy", "-disposition:v", "attached_pic", "-metadata", "title=Test Song", Mp3Cover);
        Run("-i", Wav, "-c:a", "flac", Flac);
        Run("-f", "lavfi", "-i", "testsrc=s=160x120:d=3:r=25", "-f", "lavfi", "-i", Tone,
            "-map", "0", "-map", "1", "-c:v", "mpeg4", "-c:a", "aac", "-b:a", "160k", "-shortest", Mp4);
        Run("-f", "lavfi", "-i", "testsrc=s=160x120:d=3:r=25", "-f", "lavfi", "-i", Tone, "-f", "lavfi", "-i", Tone,
            "-map", "0", "-map", "1", "-map", "2", "-c:v", "mpeg4", "-c:a", "aac", "-b:a", "160k",
            "-metadata:s:a:0", "title=Game", "-metadata:s:a:1", "title=Mic", "-shortest", MultiMkv);
        File.Copy(Wav, Unicode);
        Run("-f", "lavfi", "-i", "aevalsrc='0.1*sin(2*PI*220*t)':s=8000:d=900", "-c:a", "pcm_s16le", Long);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(Dir, true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private void Run(params string[] args)
    {
        var psi = new ProcessStartInfo(Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string a in new[] { "-hide_banner", "-nostdin", "-y", "-loglevel", "error" }.Concat(args)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"ffmpeg {string.Join(' ', args)} failed: {err}");
    }

    /// <summary>DBOOST_FFMPEG, then the repo's third_party copy, then PATH.</summary>
    private static string FindFfmpeg()
    {
        if (FfmpegLocator.FromEnvironment() is { } env) return env;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string p = Path.Combine(d.FullName, "third_party", "ffmpeg", "ffmpeg.exe");
            if (File.Exists(p)) return p;
        }
        return FfmpegLocator.OnPath() ?? throw new InvalidOperationException("No ffmpeg found. Run tools/get-ffmpeg.ps1.");
    }
}

public class EngineTests(MediaFixture fx) : IClassFixture<MediaFixture>
{
    private BoostEngine E => fx.Engine;

    private async Task<(MediaInfo Info, BoostResult Result)> Boost(string input, BoostSettings s, FileEdits? e = null, string? outDir = null)
    {
        var info = await E.ProbeAsync(input);
        outDir ??= fx.NewDir("out");
        var result = await E.ProcessAsync(info, s with { OutputFolder = outDir }, e ?? FileEdits.None);
        return (info, result);
    }

    private async Task<LoudnessAnalysis> Measure(string path, int track = 0)
    {
        var info = await E.ProbeAsync(path);
        return await E.AnalyzeAsync(info, info.AudioStreams[track], new BoostSettings(), FileEdits.None);
    }

    [Fact]
    public async Task Source_files_are_what_the_tests_assume()
    {
        var a = await Measure(fx.Wav);
        Assert.Equal(MediaFixture.SourcePeakDb, a.PeakDb, 0.2);
    }

    [Fact]
    public async Task Mp3_gets_6_dB_louder_and_keeps_album_art_and_tags()
    {
        var (_, r) = await Boost(fx.Mp3Cover, BoostSettings.Simple(6));
        Assert.NotNull(r.OutputPath);
        Assert.EndsWith("cover (+6 dB).mp3", r.OutputPath);

        var outInfo = await E.ProbeAsync(r.OutputPath!);
        Assert.Equal("mp3", outInfo.AudioStreams[0].Codec);
        Assert.True(outInfo.HasCoverArt);
        Assert.Equal(MediaFixture.SourcePeakDb + 6, (await Measure(r.OutputPath!)).PeakDb, 0.7);
    }

    [Fact]
    public async Task Wav_can_be_made_quieter()
    {
        var (_, r) = await Boost(fx.Wav, BoostSettings.Simple(-6));
        Assert.EndsWith("tone (-6 dB).wav", r.OutputPath);
        Assert.Equal("pcm_s16le", (await E.ProbeAsync(r.OutputPath!)).AudioStreams[0].Codec);
        Assert.Equal(MediaFixture.SourcePeakDb - 6, (await Measure(r.OutputPath!)).PeakDb, 0.2);
    }

    [Fact]
    public async Task Huge_boost_with_limiter_stays_under_the_ceiling()
    {
        var (_, r) = await Boost(fx.Wav, BoostSettings.Simple(40));
        double peak = (await Measure(r.OutputPath!)).PeakDb;
        Assert.InRange(peak, -2.5, -0.8);
    }

    [Fact]
    public async Task Huge_boost_without_limiter_hits_full_scale()
    {
        var (_, r) = await Boost(fx.Wav, BoostSettings.Simple(40) with { PreventClipping = false });
        Assert.Equal(0, (await Measure(r.OutputPath!)).PeakDb, 0.1);
    }

    [Fact]
    public async Task Unlimited_dB_values_do_not_break_anything()
    {
        var (_, loud) = await Boost(fx.Wav, BoostSettings.Simple(5000));
        Assert.True(File.Exists(loud.OutputPath));
        var (_, quiet) = await Boost(fx.Wav, BoostSettings.Simple(-5000));
        Assert.True((await Measure(quiet.OutputPath!)).IsSilent);
    }

    [Fact]
    public async Task Flac_stays_flac()
    {
        var (_, r) = await Boost(fx.Flac, BoostSettings.Simple(3));
        var a = (await E.ProbeAsync(r.OutputPath!)).AudioStreams[0];
        Assert.Equal("flac", a.Codec);
        Assert.Equal(16, a.BitsPerSample);
        Assert.Equal(MediaFixture.SourcePeakDb + 3, (await Measure(r.OutputPath!)).PeakDb, 0.2);
    }

    [Fact]
    public async Task Video_picture_is_copied_bit_for_bit()
    {
        var (_, r) = await Boost(fx.Mp4, BoostSettings.Simple(6));
        Assert.EndsWith(".mp4", r.OutputPath);
        Assert.Equal(VideoHash(fx.Mp4), VideoHash(r.OutputPath!));
        Assert.Equal(MediaFixture.SourcePeakDb + 6, (await Measure(r.OutputPath!)).PeakDb, 0.7);
    }

    [Fact]
    public async Task Only_the_selected_track_of_a_multi_track_video_changes()
    {
        var edits = new FileEdits { BoostedTracks = new HashSet<int> { 1 } };
        var (_, r) = await Boost(fx.MultiMkv, BoostSettings.Simple(6), edits);
        var outInfo = await E.ProbeAsync(r.OutputPath!);
        Assert.Equal(["Game", "Mic"], outInfo.AudioStreams.Select(a => a.Title));
        Assert.Equal(MediaFixture.SourcePeakDb, (await Measure(r.OutputPath!, 0)).PeakDb, 0.7);
        Assert.Equal(MediaFixture.SourcePeakDb + 6, (await Measure(r.OutputPath!, 1)).PeakDb, 0.7);
        Assert.Equal(VideoHash(fx.MultiMkv), VideoHash(r.OutputPath!));
    }

    [Fact]
    public async Task A_part_of_the_file_can_be_boosted_on_its_own()
    {
        var s = BoostSettings.Simple(0) with { PreventClipping = false };
        var e = new FileEdits { Segments = [new GainSegment(1, 2, 12)] };
        var (_, r) = await Boost(fx.Wav, s, e);
        Assert.EndsWith("tone (edited).wav", r.OutputPath);

        var info = await E.ProbeAsync(r.OutputPath!);
        var wave = await E.ReadWaveformAsync(info, info.AudioStreams[0], 30);
        double before = wave.Peaks[5], inside = wave.Peaks[15], after = wave.Peaks[25];
        Assert.Equal(0.1, before, 0.01);
        Assert.Equal(0.1, after, 0.01);
        Assert.Equal(0.1 * Decibels.ToAmplitude(12), inside, 0.02);
    }

    [Fact]
    public async Task Loudness_auto_level_hits_the_target()
    {
        var s = new BoostSettings { AutoLevel = AutoLevel.Loudness, AutoLevelTarget = -16, PreventClipping = true };
        var (_, r) = await Boost(fx.Wav, s);
        Assert.EndsWith("tone (-16 LUFS).wav", r.OutputPath);
        Assert.Equal(-16, (await Measure(r.OutputPath!)).IntegratedLufs!.Value, 0.6);
    }

    [Fact]
    public async Task Peak_auto_level_boosts_to_just_below_clipping()
    {
        var s = new BoostSettings { AutoLevel = AutoLevel.Peak, AutoLevelTarget = -1, PreventClipping = false };
        var (_, r) = await Boost(fx.Wav, s);
        Assert.Equal(-1, (await Measure(r.OutputPath!)).PeakDb, 0.2);
    }

    [Fact]
    public async Task Left_and_right_can_be_set_separately()
    {
        var s = BoostSettings.Simple(0) with { LeftDb = -6, RightDb = 6, PreventClipping = false };
        var (_, r) = await Boost(fx.Wav, s);
        var info = await E.ProbeAsync(r.OutputPath!);
        // Measure each channel by muting the other one.
        var left = await E.AnalyzeAsync(info, info.AudioStreams[0], new BoostSettings { RightDb = -600 }, FileEdits.None);
        var right = await E.AnalyzeAsync(info, info.AudioStreams[0], new BoostSettings { LeftDb = -600 }, FileEdits.None);
        Assert.Equal(MediaFixture.SourcePeakDb - 6, left.PeakDb, 0.2);
        Assert.Equal(MediaFixture.SourcePeakDb + 6, right.PeakDb, 0.2);
    }

    [Theory]
    [InlineData(OutputFormat.Mp3, ".mp3", "mp3")]
    [InlineData(OutputFormat.M4a, ".m4a", "aac")]
    [InlineData(OutputFormat.Flac, ".flac", "flac")]
    [InlineData(OutputFormat.Ogg, ".ogg", "vorbis")]
    [InlineData(OutputFormat.Opus, ".opus", "opus")]
    [InlineData(OutputFormat.Wav, ".wav", "pcm_s16le")]
    public async Task Converts_to_other_audio_formats(OutputFormat format, string ext, string codec)
    {
        var (_, r) = await Boost(fx.Wav, BoostSettings.Simple(3) with { Format = format });
        Assert.EndsWith(ext, r.OutputPath);
        Assert.Equal(codec, (await E.ProbeAsync(r.OutputPath!)).AudioStreams[0].Codec);
    }

    [Fact]
    public async Task Pulls_boosted_audio_out_of_a_video()
    {
        var (_, r) = await Boost(fx.Mp4, BoostSettings.Simple(6) with { Format = OutputFormat.M4a });
        var info = await E.ProbeAsync(r.OutputPath!);
        Assert.False(info.IsVideo);
        Assert.Equal(MediaFixture.SourcePeakDb + 6, (await Measure(r.OutputPath!)).PeakDb, 0.7);
    }

    [Fact]
    public async Task Video_can_move_to_mkv()
    {
        var (_, r) = await Boost(fx.Mp4, BoostSettings.Simple(2) with { Format = OutputFormat.Mkv });
        Assert.EndsWith(".mkv", r.OutputPath);
        Assert.Equal(VideoHash(fx.Mp4), VideoHash(r.OutputPath!));
    }

    [Fact]
    public async Task Zero_dB_with_nothing_else_is_skipped()
    {
        var (_, r) = await Boost(fx.Wav, BoostSettings.Simple(0));
        Assert.True(r.Skipped);
        Assert.Null(r.OutputPath);
    }

    [Fact]
    public async Task Unicode_file_names_work()
    {
        var (_, r) = await Boost(fx.Unicode, BoostSettings.Simple(6));
        Assert.EndsWith("tëst ♪ 日本 (+6 dB).wav", r.OutputPath);
        Assert.True(File.Exists(r.OutputPath));
    }

    [Fact]
    public async Task Fades_are_applied()
    {
        var s = BoostSettings.Simple(0) with { FadeInSeconds = 1, FadeOutSeconds = 1, PreventClipping = false };
        var (_, r) = await Boost(fx.Wav, s);
        var info = await E.ProbeAsync(r.OutputPath!);
        var wave = await E.ReadWaveformAsync(info, info.AudioStreams[0], 30);
        Assert.True(wave.Peaks[0] < 0.02, $"start should be near silent, was {wave.Peaks[0]}");
        Assert.Equal(0.1, wave.Peaks[15], 0.01);
        Assert.True(wave.Peaks[29] < 0.02, $"end should be near silent, was {wave.Peaks[29]}");
    }

    [Fact]
    public async Task Preview_renders_a_short_wav_from_the_chosen_spot()
    {
        var info = await E.ProbeAsync(fx.Wav);
        string outWav = fx.P($"preview-{Guid.NewGuid():N}.wav");
        await E.RenderPreviewAsync(info, info.AudioStreams[0], BoostSettings.Simple(6), FileEdits.None, 6, 1, 1.5, true, outWav);
        var p = await E.ProbeAsync(outWav);
        Assert.InRange(p.DurationSeconds!.Value, 1.3, 1.7);
        Assert.Equal(MediaFixture.SourcePeakDb + 6, (await Measure(outWav)).PeakDb, 0.3);
    }

    [Fact]
    public async Task Waveform_has_the_requested_resolution()
    {
        var info = await E.ProbeAsync(fx.Mp4);
        var wave = await E.ReadWaveformAsync(info, info.AudioStreams[0], 200);
        Assert.Equal(200, wave.Buckets);
        Assert.Equal(3, wave.DurationSeconds, 0.2);
        Assert.Equal(0.1, wave.Peaks[100], 0.02);
    }

    [Fact]
    public async Task Cancelling_leaves_no_half_written_file()
    {
        string outDir = fx.NewDir("cancel");
        var info = await E.ProbeAsync(fx.Long);
        var e = new FileEdits { Segments = Enumerable.Range(0, 40).Select(i => new GainSegment(i * 20, i * 20 + 10, 3)).ToList() };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try
        {
            await E.ProcessAsync(info, BoostSettings.Simple(6) with { OutputFolder = outDir }, e, null, cts.Token);
        }
        catch (OperationCanceledException) { }
        Assert.Empty(Directory.GetFiles(outDir, "*.dboost-partial.*"));
    }

    [Fact]
    public async Task Probe_explains_a_file_that_is_not_media()
    {
        string txt = fx.P("notes.txt");
        await File.WriteAllTextAsync(txt, "hello, not audio");
        var ex = await Assert.ThrowsAsync<MediaProbeException>(() => E.ProbeAsync(txt));
        Assert.Contains("not an audio or video file", ex.Message);
    }

    private string VideoHash(string path)
    {
        var psi = new ProcessStartInfo(fx.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-i", path, "-map", "0:v:0", "-c", "copy", "-f", "md5", "-" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string md5 = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return md5;
    }
}
