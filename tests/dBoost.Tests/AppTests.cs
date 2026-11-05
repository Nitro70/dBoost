using DBoost.App.Services;
using DBoost.App.ViewModels;
using DBoost.Core;

namespace DBoost.Tests;

public class AppTests
{
    [Fact]
    public async Task A_second_launch_hands_its_files_to_the_first()
    {
        string id = "dBoost-test-" + Guid.NewGuid().ToString("N");
        using var primary = SingleInstance.TryBecomePrimary(id);
        Assert.NotNull(primary);
        Assert.Null(SingleInstance.TryBecomePrimary(id));

        var received = new TaskCompletionSource<string[]>();
        primary!.FilesReceived += f => received.TrySetResult(f);
        Assert.True(SingleInstance.SendToPrimary(["song.mp3", @"C:\Videos\clip (1).mp4"], id));

        var files = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([Path.GetFullPath("song.mp3"), @"C:\Videos\clip (1).mp4"], files);
    }

    [Fact]
    public void Sending_with_nobody_listening_reports_failure_quickly_enough()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(SingleInstance.SendToPrimary(["x.mp3"], "dBoost-test-nobody-" + Guid.NewGuid().ToString("N")));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8));
    }

    [Fact]
    public void Simple_mode_ignores_every_advanced_setting()
    {
        var saved = new AppSettings
        {
            GainDb = 9, Advanced = false, PreventClipping = false, AutoLevel = AutoLevel.Loudness, BassDb = 5,
            LeftDb = 3, FadeInSeconds = 2, Format = OutputFormat.Mp3, BitrateKbps = 320,
        };
        var vm = new MainViewModel(saved);
        Assert.Equal(BoostSettings.Simple(9), vm.BuildSettings());

        vm.Advanced = true;
        var adv = vm.BuildSettings();
        Assert.Equal(AutoLevel.Loudness, adv.AutoLevel);
        Assert.False(adv.PreventClipping);
        Assert.Equal(5, adv.BassDb);
        Assert.Equal(OutputFormat.Mp3, adv.Format);
        Assert.Equal(320, adv.BitrateKbps);
    }

    [Fact]
    public void The_dial_has_no_limit_and_the_slider_grows_to_fit()
    {
        var vm = new MainViewModel(new AppSettings());
        vm.GainDb = 250;
        Assert.Equal(250, vm.GainDb);
        Assert.True(vm.SliderMax >= 250);
        vm.GainDb = -1234.5;
        Assert.Equal(-1234.5, vm.GainDb);
        Assert.True(vm.SliderMin <= -1234.5);
        vm.GainDb = double.NaN;
        Assert.Equal(-1234.5, vm.GainDb);
    }

    [Fact]
    public void Button_text_follows_the_direction()
    {
        var vm = new MainViewModel(new AppSettings { GainDb = 6 });
        Assert.Equal("Boost", vm.StartButtonText);
        vm.GainDb = -3;
        Assert.Equal("Lower", vm.StartButtonText);
    }

    [Theory]
    [InlineData("75", 75)]
    [InlineData("75.5", 75.5)]
    [InlineData("1:15", 75)]
    [InlineData("1:15,5", 75.5)]
    [InlineData("1:02:03", 3723)]
    public void Part_times_can_be_typed_several_ways(string text, double seconds)
    {
        Assert.True(SegmentItem.TryParseTime(text, out double t));
        Assert.Equal(seconds, t, 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("1:2:3:4")]
    public void Bad_part_times_are_refused(string text) => Assert.False(SegmentItem.TryParseTime(text, out _));

    [Fact]
    public void Part_edits_keep_start_before_end()
    {
        var seg = new SegmentItem(10, 20, 6);
        seg.StartText = "25"; // after the end: refused
        Assert.Equal(10, seg.Start);
        seg.EndText = "0:30";
        Assert.Equal(30, seg.End);
        Assert.Equal("0:10.0", seg.StartText);
        Assert.Equal("0:20.0", seg.LengthText);
    }

    [Fact]
    public void Per_file_choices_become_edits()
    {
        var info = new MediaInfo(@"C:\v.mkv", "matroska", TimeSpan.FromSeconds(60), null,
            [
                new AudioStreamInfo(1, 0, "aac", 48000, 2, "stereo", "fltp", 32, 128, null, "Game", true),
                new AudioStreamInfo(2, 1, "aac", 48000, 1, "mono", "fltp", 32, 64, null, "Mic", false),
            ], ["h264"], null, 0, 0);
        var item = new FileItem(@"C:\v.mkv") { Info = info };

        var none = item.ToEdits();
        Assert.Null(none.GainDb);
        Assert.Null(none.BoostedTracks);
        Assert.Empty(none.Segments);

        item.Tracks[0].IsBoosted = false;
        item.UseOwnGain = true;
        item.OwnGainDb = -4;
        item.Segments.Add(new SegmentItem(5, 9, 12));
        item.Segments.Add(new SegmentItem(3, 3, 12)); // zero length: dropped
        var edits = item.ToEdits();
        Assert.Equal(-4, edits.GainDb);
        Assert.Equal(new HashSet<int> { 1 }, edits.BoostedTracks);
        Assert.Equal([new GainSegment(5, 9, 12)], edits.Segments);
        Assert.Equal("1 of 2 tracks", item.TracksSummary);
    }

    [Fact]
    public void Explorer_menu_command_quotes_the_paths()
    {
        Assert.Equal("\"C:\\Program Files\\dBoost\\dBoost.exe\" \"%1\"", ShellIntegration.CommandFor(@"C:\Program Files\dBoost\dBoost.exe"));
        Assert.Contains(".mp3", ShellIntegration.Extensions);
        Assert.Contains(".mkv", ShellIntegration.Extensions);
    }

    [Fact]
    public void Send_to_shortcut_can_be_made_and_removed()
    {
        string dir = Directory.CreateTempSubdirectory("dboost-sendto-").FullName;
        try
        {
            string fakeExe = Path.Combine(dir, "dBoost.exe");
            File.WriteAllText(fakeExe, "");
            Assert.False(ShellIntegration.IsSendToInstalled(dir));
            ShellIntegration.InstallSendTo(fakeExe, dir);
            Assert.True(ShellIntegration.IsSendToInstalled(dir));
            ShellIntegration.RemoveSendTo(dir);
            Assert.False(ShellIntegration.IsSendToInstalled(dir));
        }
        finally { Directory.Delete(dir, true); }
    }
}
