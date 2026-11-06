using DBoost.App.Services;
using DBoost.App.ViewModels;
using DBoost.Core;

namespace DBoost.Tests;

/// <summary>The listen feature: decoding for playback, and the player itself at volume 0 (no sound comes out).</summary>
public class PlayerTests(MediaFixture fx) : IClassFixture<MediaFixture>
{
    [System.Runtime.InteropServices.DllImport("winmm.dll")]
    private static extern int waveOutGetNumDevs();

    private static long ReadAll(PcmReader r)
    {
        var buf = new byte[65536];
        long total = 0;
        int n;
        while ((n = r.ReadFull(buf, 0, buf.Length)) > 0) total += n;
        return total;
    }

    [Fact]
    public void Decodes_the_whole_track_as_48k_stereo()
    {
        using var r = fx.Engine.OpenPcm(fx.Wav, 0, 0);
        double seconds = ReadAll(r) / (double)PcmReader.BytesPerSecond;
        Assert.Equal(3, seconds, 0.05);
    }

    [Fact]
    public void Starts_at_the_requested_time()
    {
        using var r = fx.Engine.OpenPcm(fx.Wav, 0, 2.0);
        double seconds = ReadAll(r) / (double)PcmReader.BytesPerSecond;
        Assert.Equal(1, seconds, 0.05);
    }

    [Fact]
    public void Plays_only_the_sound_of_a_video_and_any_track()
    {
        using (var v = fx.Engine.OpenPcm(fx.Mp4, 0, 0))
            Assert.Equal(3, ReadAll(v) / (double)PcmReader.BytesPerSecond, 0.1);
        using (var second = fx.Engine.OpenPcm(fx.MultiMkv, 1, 1))
            Assert.Equal(2, ReadAll(second) / (double)PcmReader.BytesPerSecond, 0.1);
    }

    [Fact]
    public void A_missing_file_just_ends_and_says_why()
    {
        using var r = fx.Engine.OpenPcm(fx.P("not-there.mp3"), 0, 0);
        Assert.Equal(0, ReadAll(r));
        Thread.Sleep(200);
        Assert.Contains("No such file", r.Errors, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Disposing_stops_decoding_straight_away()
    {
        var r = fx.Engine.OpenPcm(fx.Long, 0, 0);
        var buf = new byte[4096];
        Assert.Equal(buf.Length, r.ReadFull(buf, 0, buf.Length));
        r.Dispose();
        Assert.Equal(0, r.ReadFull(buf, 0, buf.Length));
    }

    [Fact]
    public void Silent_playback_tracks_position_pauses_and_ends()
    {
        if (waveOutGetNumDevs() == 0) return; // no sound device on this machine
        var ended = new ManualResetEventSlim();
        using var player = new AudioPlayer { Volume = 0f };
        player.Ended += _ => ended.Set();

        player.Play(fx.Engine, fx.Wav, 0, 0);
        Thread.Sleep(700);
        Assert.True(player.IsPlaying);
        double p1 = player.Position;
        Assert.InRange(p1, 0.2, 1.2);

        player.Pause();
        Assert.True(player.IsPaused);
        double held = player.Position;
        Thread.Sleep(400);
        Assert.Equal(held, player.Position, 0.05);

        player.Resume();
        player.Play(fx.Engine, fx.Wav, 0, 2.5); // a seek is a fresh start at the new time
        Assert.True(player.Position >= 2.5);
        Assert.True(ended.Wait(TimeSpan.FromSeconds(5)), "playback should end by itself");
        Assert.False(player.IsActive);
    }

    [Fact]
    public void Player_bar_says_which_file_it_will_play()
    {
        var vm = new MainViewModel(new AppSettings());
        var info = new MediaInfo(@"C:\m\song.mp3", "mp3", TimeSpan.FromSeconds(200), 320,
            [new AudioStreamInfo(0, 0, "mp3", 44100, 2, "stereo", "fltp", 32, 320, null, null, true)], [], null, 0, 0);
        var item = new FileItem(@"C:\m\song.mp3") { Info = info, State = FileState.Ready };
        vm.Files.Add(item);
        vm.SelectedFile = item;

        Assert.True(vm.CanListen);
        Assert.False(vm.HasNewFile);
        Assert.Equal("song.mp3", vm.ListenTitle);
        Assert.Equal("Original (not boosted yet)", vm.ListenSourceLabel);
        Assert.Equal(200, vm.ListenDuration);

        string output = fx.Wav; // stands in for a boosted copy that exists on disk
        item.OutputPath = output;
        item.OutputInfo = info with { Duration = TimeSpan.FromSeconds(3) };
        item.State = FileState.Done;
        Assert.True(vm.HasNewFile);
        Assert.Equal("New file", vm.ListenSourceLabel);
        Assert.Equal(Path.GetFileName(output), vm.ListenTitle);
        Assert.Equal(3, vm.ListenDuration);

        vm.ListenToOriginal = true;
        Assert.Equal("Original", vm.ListenSourceLabel);
        Assert.Equal(200, vm.ListenDuration);
    }
}
