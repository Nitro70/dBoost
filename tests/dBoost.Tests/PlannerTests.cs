using DBoost.Core;

namespace DBoost.Tests;

public class PlannerTests
{
    private static readonly EncoderPlanner Planner = new(new HashSet<string>
    {
        "aac", "libmp3lame", "libopus", "libvorbis", "flac", "alac", "wavpack", "tta", "ac3", "eac3", "dca", "mp2",
        "wmav2", "pcm_s16le", "pcm_s24le", "pcm_s32le", "pcm_f32le", "pcm_s16be", "libopencore_amrnb",
    });

    private static AudioStreamInfo Track(string codec, int ordinal = 0, int channels = 2, string fmt = "fltp", int bits = 32, int? kbps = 128) =>
        new(ordinal + 1, ordinal, codec, 48000, channels, channels == 1 ? "mono" : "stereo", fmt, bits, kbps, null, null, ordinal == 0);

    private static MediaInfo Media(string path, IReadOnlyList<AudioStreamInfo> audio, bool video = false, int? cover = null, int subs = 0) =>
        new(path, "x", TimeSpan.FromSeconds(10), 1000, audio, video ? ["h264"] : [], cover, subs, 0);

    private static readonly Dictionary<int, double> NoGains = new();

    [Fact]
    public void Simple_mode_mp3_keeps_mp3_cover_and_bitrate()
    {
        var info = Media(@"C:\m\song.mp3", [Track("mp3", kbps: 320)], cover: 0);
        var plan = JobPlanner.Plan(info, BoostSettings.Simple(6), FileEdits.None, Planner, NoGains);

        Assert.Equal(PlanKind.KeepContainer, plan.Kind);
        Assert.Equal(".mp3", plan.Extension);
        var t = Assert.Single(plan.Tracks);
        Assert.Equal("libmp3lame", t.Encoder.Encoder);
        Assert.Equal(320, t.Encoder.BitrateKbps);
        Assert.Contains("volume=6dB", t.Filter);
        Assert.Contains("alimiter=", t.Filter);
        Assert.Equal(["-map", "0", "-c", "copy"], plan.MappingVariants[0]);

        var args = plan.BuildArgs(@"C:\m\song.mp3", @"C:\m\out.mp3", 0);
        Assert.Equal("-i", args[args.IndexOf(@"C:\m\song.mp3") - 1]);
        Assert.True(args.IndexOf("-c:a:0") > args.IndexOf("copy"), "the per-track codec must come after -c copy so it wins");
    }

    [Fact]
    public void Low_bitrate_sources_are_not_made_worse()
    {
        var t = Planner.SameCodec(Track("aac", kbps: 97), null)!;
        Assert.Equal(128, t.BitrateKbps);
        var t2 = Planner.SameCodec(Track("aac", kbps: 200), null)!;
        Assert.Equal(224, t2.BitrateKbps);
    }

    [Fact]
    public void Flac_24_bit_stays_24_bit()
    {
        var info = Media(@"C:\m\hi.flac", [Track("flac", fmt: "s32", bits: 24, kbps: null)]);
        var t = Assert.Single(JobPlanner.Plan(info, BoostSettings.Simple(3), FileEdits.None, Planner, NoGains).Tracks);
        Assert.Equal("flac", t.Encoder.Encoder);
        Assert.Null(t.Encoder.BitrateKbps);
        Assert.EndsWith("aformat=sample_fmts=s32|s32p", t.Filter);
        Assert.Contains(("bits_per_raw_sample", "24"), t.Encoder.ExtraArgs);
    }

    [Fact]
    public void Video_copies_picture_and_only_reencodes_selected_tracks()
    {
        var info = Media(@"C:\v\rec.mkv", [Track("aac", 0), Track("opus", 1)], video: true, subs: 1);
        var edits = new FileEdits { BoostedTracks = new HashSet<int> { 1 } };
        var plan = JobPlanner.Plan(info, BoostSettings.Simple(10), edits, Planner, NoGains);

        Assert.Equal(PlanKind.KeepContainer, plan.Kind);
        var t = Assert.Single(plan.Tracks);
        Assert.Equal(1, t.OutputIndex);
        Assert.Equal("libopus", t.Encoder.Encoder);
        var args = plan.BuildArgs("in.mkv", "out.mkv", 0);
        Assert.Contains("-c:a:1", args);
        Assert.DoesNotContain("-c:a:0", args);
        Assert.Contains("-max_muxing_queue_size", args);
    }

    [Fact]
    public void Extracting_audio_from_video_keeps_one_track_and_says_so()
    {
        var info = Media(@"C:\v\rec.mp4", [Track("aac", 0), Track("aac", 1)], video: true);
        var s = BoostSettings.Simple(6) with { Format = OutputFormat.Mp3 };
        var plan = JobPlanner.Plan(info, s, FileEdits.None, Planner, NoGains);

        Assert.Equal(PlanKind.AudioOnly, plan.Kind);
        Assert.Equal(".mp3", plan.Extension);
        Assert.Equal("libmp3lame", Assert.Single(plan.Tracks).Encoder.Encoder);
        Assert.Equal(["-map", "0:a:0", "-vn"], plan.MappingVariants[0]);
        Assert.Contains(plan.Notes, n => n.Contains("one track"));
    }

    [Fact]
    public void Unwritable_lossless_audio_becomes_flac()
    {
        var info = Media(@"C:\m\album.ape", [Track("ape", fmt: "s16p", bits: 16, kbps: 900)]);
        var plan = JobPlanner.Plan(info, BoostSettings.Simple(6), FileEdits.None, Planner, NoGains);
        Assert.Equal(PlanKind.AudioOnly, plan.Kind);
        Assert.Equal(".flac", plan.Extension);
        Assert.Equal("flac", plan.Tracks[0].Encoder.Encoder);
        Assert.EndsWith("aformat=sample_fmts=s16|s16p", plan.Tracks[0].Filter);
    }

    [Fact]
    public void Unwritable_lossy_codec_in_writable_container_becomes_mp3()
    {
        // wmapro has a decoder but no encoder.
        var info = Media(@"C:\m\track.wma", [Track("wmapro", kbps: 192)]);
        var plan = JobPlanner.Plan(info, BoostSettings.Simple(6), FileEdits.None, Planner, NoGains);
        Assert.Equal(".mp3", plan.Extension);
        Assert.Contains(plan.Notes, n => n.Contains("wmapro"));
    }

    [Fact]
    public void Video_to_mp4_turns_unsupported_audio_into_aac_and_drops_subtitles()
    {
        var info = Media(@"C:\v\movie.mkv", [Track("vorbis", 0), Track("aac", 1)], video: true, subs: 2);
        var s = BoostSettings.Simple(4) with { Format = OutputFormat.Mp4 };
        var edits = new FileEdits { BoostedTracks = new HashSet<int> { 1 } };
        var plan = JobPlanner.Plan(info, s, edits, Planner, NoGains);

        Assert.Equal(PlanKind.ChangeContainer, plan.Kind);
        Assert.Equal(".mp4", plan.Extension);
        // vorbis is not boosted but cannot be copied into MP4, so it is re-encoded with no gain.
        var vorbis = plan.Tracks.Single(t => t.Track.Ordinal == 0);
        Assert.Equal("aac", vorbis.Encoder.Encoder);
        Assert.DoesNotContain("volume=", vorbis.Filter);
        var aac = plan.Tracks.Single(t => t.Track.Ordinal == 1);
        Assert.Contains("volume=4dB", aac.Filter);
        Assert.Contains(plan.Notes, n => n.Contains("Subtitles"));
    }

    [Fact]
    public void Video_format_on_audio_file_is_ignored_with_a_note()
    {
        var info = Media(@"C:\m\a.wav", [Track("pcm_s16le", fmt: "s16", bits: 16, kbps: 1411)]);
        var plan = JobPlanner.Plan(info, BoostSettings.Simple(2) with { Format = OutputFormat.Mkv }, FileEdits.None, Planner, NoGains);
        Assert.Equal(".wav", plan.Extension);
        Assert.Equal("pcm_s16le", plan.Tracks[0].Encoder.Encoder);
        Assert.Contains(plan.Notes, n => n.Contains("is for videos"));
    }

    [Fact]
    public void Nothing_selected_is_an_error_people_can_read()
    {
        var info = Media(@"C:\v\a.mp4", [Track("aac")], video: true);
        var ex = Assert.Throws<BoostException>(() =>
            JobPlanner.Plan(info, BoostSettings.Simple(6), new FileEdits { BoostedTracks = new HashSet<int>() }, Planner, NoGains));
        Assert.Contains("No audio track", ex.Message);
    }

    [Fact]
    public void Resolved_auto_level_gain_wins_over_the_dial()
    {
        var info = Media(@"C:\m\a.mp3", [Track("mp3")]);
        var plan = JobPlanner.Plan(info, BoostSettings.Simple(6), FileEdits.None, Planner, new Dictionary<int, double> { [0] = -2.5 });
        Assert.Contains("volume=-2.5dB", plan.Tracks[0].Filter);
    }

    [Fact]
    public void Filter_chain_order_and_contents()
    {
        var s = new BoostSettings
        {
            GainDb = 3, PreventClipping = true, CeilingDb = -2, LeftDb = -3, RightDb = 2, BassDb = 4, TrebleDb = -1,
            FadeInSeconds = 1.5, FadeOutSeconds = 2,
        };
        var e = new FileEdits { Segments = [new GainSegment(1, 3, 6), new GainSegment(2, 2.001, 50)] };
        string chain = FilterBuilder.BuildChain(s, e, Track("aac"), 10, 3, "aformat=sample_fmts=s16");

        string[] filters = SplitFilters(chain);
        Assert.StartsWith("aeval=", filters[0]);
        Assert.StartsWith("bass=g=4", filters[1]);
        Assert.StartsWith("treble=g=-1", filters[2]);
        Assert.Equal("volume=3dB", filters[3]);
        Assert.StartsWith("alimiter=limit=0.794328", filters[4]);
        Assert.Equal("aformat=sample_fmts=s16", filters[5]);

        // the too-short part is skipped, the real one ramps, left/right and both fades are present
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(filters[0], @"clip\(\(t-1\)/0\.02"));
        Assert.DoesNotContain("t-2)", filters[0]);
        Assert.Contains("if(eq(ch,0),0.707946,if(eq(ch,1),1.258925,1))", filters[0]);
        Assert.Contains("clip(t/1.5,0,1)", filters[0]);
        Assert.Contains("clip((10-t)/2,0,1)", filters[0]);
    }

    [Fact]
    public void Pre_level_chain_leaves_out_fades_and_gain()
    {
        var s = new BoostSettings { FadeInSeconds = 2, BassDb = 3 };
        string? pre = FilterBuilder.BuildPreLevelChain(s, FileEdits.None, Track("aac"), 10);
        Assert.Equal("bass=g=3", pre);
        Assert.Null(FilterBuilder.BuildPreLevelChain(new BoostSettings(), FileEdits.None, Track("aac"), 10));
    }

    [Fact]
    public void Mono_ignores_left_right()
    {
        var s = new BoostSettings { LeftDb = 6, PreventClipping = false };
        Assert.Equal("volume=1dB", FilterBuilder.BuildChain(s, FileEdits.None, Track("mp3", channels: 1), 10, 1));
    }

    [Fact]
    public void Preview_offsets_time_so_parts_line_up()
    {
        var e = new FileEdits { Segments = [new GainSegment(30, 40, 6)] };
        string chain = FilterBuilder.BuildChain(new BoostSettings { PreventClipping = false }, e, Track("aac"), 100, 0, timeOffset: 25);
        Assert.Contains("((t+25)-30)", chain);
    }

    [Fact]
    public void Output_names_never_overwrite()
    {
        string dir = Directory.CreateTempSubdirectory("dboost-names").FullName;
        try
        {
            string input = Path.Combine(dir, "song.mp3");
            File.WriteAllText(input, "");
            string first = OutputNaming.UniquePath(input, null, ".mp3", "+6 dB");
            Assert.Equal(Path.Combine(dir, "song (+6 dB).mp3"), first);
            File.WriteAllText(first, "");
            Assert.Equal(Path.Combine(dir, "song (+6 dB) (2).mp3"), OutputNaming.UniquePath(input, null, ".mp3", "+6 dB"));
            Assert.Equal(Path.Combine(dir, "song (+6 dB).dboost-partial.mp3"), OutputNaming.PartialPath(first));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(6, AutoLevel.Off, "+6 dB")]
    [InlineData(-3, AutoLevel.Off, "-3 dB")]
    [InlineData(0, AutoLevel.Off, "edited")]
    [InlineData(6, AutoLevel.Loudness, "-14 LUFS")]
    [InlineData(6, AutoLevel.Peak, "max volume")]
    public void Labels(double gain, AutoLevel level, string expected) =>
        Assert.Equal(expected, OutputNaming.Label(new BoostSettings { GainDb = gain, AutoLevel = level, AutoLevelTarget = -14 }, FileEdits.None));

    [Fact]
    public void Encoder_list_parsing()
    {
        const string text = """
            Encoders:
             V..... = Video
             A..... = Audio
             ------
             V....D a64multi             Multicolor charset for Commodore 64 (codec a64_multi)
             A....D aac                  AAC (Advanced Audio Coding)
             A....D libmp3lame           libmp3lame MP3 (MPEG audio layer 3) (codec mp3)
            """;
        var set = FfmpegRunner.ParseEncoders(text);
        Assert.Equal(new HashSet<string> { "a64multi", "aac", "libmp3lame" }, set);
    }

    /// <summary>Splits on commas outside quotes, like ffmpeg's filter graph parser.</summary>
    private static string[] SplitFilters(string chain)
    {
        var list = new List<string>();
        bool quoted = false; int start = 0;
        for (int i = 0; i < chain.Length; i++)
        {
            if (chain[i] == '\'') quoted = !quoted;
            else if (chain[i] == ',' && !quoted) { list.Add(chain[start..i]); start = i + 1; }
        }
        list.Add(chain[start..]);
        return list.ToArray();
    }
}
