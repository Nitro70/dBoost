using DBoost.Core;

namespace DBoost.Tests;

/// <summary>Real ffmpeg 9 output, captured from generated test files.</summary>
public class MediaProbeTests
{
    private const string Mp3WithCover = """
        [in#0 @ 000001cc1ebad080] Format mp3 detected only with low score of 1, misdetection possible!
        Input #0, mp3, from 'cover.mp3':
          Metadata:
            title           : Test Song
            encoder         : Lavf63.1.102
          Duration: 00:03:21.45, start: 0.025056, bitrate: 320 kb/s
          Stream #0:0: Audio: mp3 (mp3float), 44100 Hz, stereo, fltp, 320 kb/s, start 0.025057
          Stream #0:1: Video: mjpeg (Baseline), yuvj420p(pc, bt470bg/unknown/unknown), 64x64 [SAR 1:1 DAR 1:1], 90k tbr, 90k tbn, start 0.025056 (attached pic)
            Metadata:
              comment         : Other
        At least one output file must be specified
        """;

    private const string Flac24 = """
        Input #0, flac, from 'hi.flac':
          Metadata:
            encoder         : Lavf63.1.102
          Duration: 00:00:02.00, start: 0.000000, bitrate: 977 kb/s
          Stream #0:0: Audio: flac, 96000 Hz, stereo, s32 (24 bit)
        At least one output file must be specified
        """;

    private const string MultiTrackMkv = """
        Input #0, matroska,webm, from 'multi.mkv':
          Metadata:
            ENCODER         : Lavf63.1.102
          Duration: 00:00:02.02, start: 0.000000, bitrate: 369 kb/s
          Stream #0:0: Video: mpeg4 (Simple Profile), yuv420p(tv, progressive), 160x120 [SAR 1:1 DAR 4:3], 25 fps, 25 tbr, 1k tbn
            Metadata:
              ENCODER         : Lavc63.1.102 mpeg4
              DURATION        : 00:00:02.000000000
          Stream #0:1: Audio: aac (LC), 44100 Hz, mono, fltp (default)
            Metadata:
              title           : Game
              ENCODER         : Lavc63.1.102 aac
              DURATION        : 00:00:02.023000000
          Stream #0:2(eng): Audio: aac (LC), 44100 Hz, 5.1(side), fltp
            Metadata:
              title           : Mic
              ENCODER         : Lavc63.1.102 aac
              DURATION        : 00:00:02.023000000
          Stream #0:3(eng): Subtitle: subrip
        At least one output file must be specified
        """;

    private const string Mp4 = """
        Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'clip.mp4':
          Metadata:
            major_brand     : isom
            minor_version   : 512
            compatible_brands: isomiso2mp41
            encoder         : Lavf63.1.102
          Duration: 00:00:02.00, start: 0.000000, bitrate: 361 kb/s
          Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p(tv, bt709, progressive), 1920x1080 [SAR 1:1 DAR 16:9], 221 kb/s, 25 fps, 25 tbr, 12800 tbn (default)
            Metadata:
              handler_name    : VideoHandler
              encoder         : Lavc63.1.102 mpeg4
          Stream #0:1[0x2](und): Audio: aac (LC) (mp4a / 0x6134706D), 48000 Hz, stereo, fltp, 127 kb/s (default)
            Metadata:
              handler_name    : SoundHandler
          Stream #0:2[0x3](und): Data: none (tmcd / 0x64636D74)
        At least one output file must be specified
        """;

    [Fact]
    public void Mp3_with_album_art_is_audio_not_video()
    {
        var info = MediaProbe.Parse(Mp3WithCover, "cover.mp3");
        Assert.False(info.IsVideo);
        Assert.True(info.HasCoverArt);
        Assert.Equal(0, info.CoverArtVideoOrdinal);
        Assert.Equal(TimeSpan.Parse("00:03:21.45"), info.Duration);
        var a = Assert.Single(info.AudioStreams);
        Assert.Equal("mp3", a.Codec);
        Assert.Equal(44100, a.SampleRate);
        Assert.Equal(2, a.Channels);
        Assert.Equal(320, a.BitrateKbps);
        Assert.Equal("fltp", a.SampleFormat);
    }

    [Fact]
    public void Flac_24_bit_keeps_its_bit_depth()
    {
        var a = Assert.Single(MediaProbe.Parse(Flac24, "hi.flac").AudioStreams);
        Assert.Equal("flac", a.Codec);
        Assert.Equal(96000, a.SampleRate);
        Assert.Equal("s32", a.SampleFormat);
        Assert.Equal(24, a.BitsPerSample);
        Assert.Null(a.BitrateKbps);
    }

    [Fact]
    public void Multi_track_video_lists_each_track_with_its_name()
    {
        var info = MediaProbe.Parse(MultiTrackMkv, "multi.mkv");
        Assert.True(info.IsVideo);
        Assert.Equal("matroska,webm", info.ContainerFormat);
        Assert.Equal(2, info.AudioStreams.Count);
        Assert.Equal(1, info.SubtitleStreamCount);

        var game = info.AudioStreams[0];
        Assert.Equal(1, game.StreamIndex);
        Assert.Equal(0, game.Ordinal);
        Assert.Equal("Game", game.Title);
        Assert.Equal(1, game.Channels);
        Assert.True(game.IsDefault);

        var mic = info.AudioStreams[1];
        Assert.Equal(1, mic.Ordinal);
        Assert.Equal("Mic", mic.Title);
        Assert.Equal("eng", mic.Language);
        Assert.Equal(6, mic.Channels);
        Assert.False(mic.IsDefault);
    }

    [Fact]
    public void Mp4_ignores_generic_handler_names_and_counts_data_streams()
    {
        var info = MediaProbe.Parse(Mp4, "clip.mp4");
        Assert.Equal(["h264"], info.VideoCodecs);
        var a = Assert.Single(info.AudioStreams);
        Assert.Null(a.Title);
        Assert.Equal(48000, a.SampleRate);
        Assert.Equal(127, a.BitrateKbps);
        Assert.Equal(1, info.OtherStreamCount);
    }

    [Fact]
    public void Unreadable_file_gives_a_plain_message()
    {
        const string err = """
            [in#0 @ 0000] Error opening input: Invalid data found when processing input
            Error opening input file notes.txt.
            Error opening input files: Invalid data found when processing input
            """;
        var ex = Assert.Throws<MediaProbeException>(() => MediaProbe.Parse(err, "notes.txt"));
        Assert.Equal("This is not an audio or video file dBoost can read.", ex.Message);
    }

    [Theory]
    [InlineData("mono", 1)]
    [InlineData("stereo", 2)]
    [InlineData("5.1(side)", 6)]
    [InlineData("7.1", 8)]
    [InlineData("3 channels", 3)]
    [InlineData("something new", 2)]
    public void Channel_counts(string layout, int expected) => Assert.Equal(expected, MediaProbe.ChannelCount(layout));
}
