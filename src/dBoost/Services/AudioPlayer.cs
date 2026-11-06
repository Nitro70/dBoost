using DBoost.Core;
using NAudio.Wave;

namespace DBoost.App.Services;

/// <summary>
/// Plays one audio track of any file: ffmpeg decodes it to PCM and Windows plays it (no picture, even for
/// videos). Pause stops pulling audio; seeking starts a fresh decode at the new time.
/// Create and use it on the UI thread: playback-ended notifications come back there.
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private WaveOutEvent? output;
    private PcmReader? reader;
    private double startOffset;
    private double heldPosition;
    private bool stoppingOnPurpose;

    /// <summary>Raised when the audio reached its end by itself, with ffmpeg's error text if it failed early.</summary>
    public event Action<string?>? Ended;

    /// <summary>0 to 1. Only the tests change it (to play silently).</summary>
    public float Volume { get; set; } = 1f;

    public string? Path { get; private set; }
    public int Track { get; private set; }

    public bool IsPlaying => output?.PlaybackState == PlaybackState.Playing;
    public bool IsPaused => output?.PlaybackState == PlaybackState.Paused;
    public bool IsActive => output is not null;

    /// <summary>Where playback is, in seconds of the file.</summary>
    public double Position
    {
        get
        {
            var o = output;
            if (o is null) return heldPosition;
            try { return startOffset + o.GetPosition() / (double)PcmReader.BytesPerSecond; }
            catch (Exception) { return startOffset; }
        }
    }

    /// <summary>Starts playing <paramref name="path"/> from <paramref name="fromSeconds"/>.</summary>
    public void Play(BoostEngine engine, string path, int track, double fromSeconds)
    {
        StopInternal();
        Path = path;
        Track = track;
        startOffset = Math.Max(0, fromSeconds);
        heldPosition = startOffset;

        reader = engine.OpenPcm(path, track, startOffset);
        output = new WaveOutEvent { DesiredLatency = 250, NumberOfBuffers = 3 };
        output.Init(new PipeProvider(reader));
        if (Volume < 1f) output.Volume = Math.Clamp(Volume, 0f, 1f);
        var thisOutput = output;
        output.PlaybackStopped += (_, _) => OnStopped(thisOutput);
        output.Play();
    }

    public void Pause()
    {
        if (output?.PlaybackState == PlaybackState.Playing) output.Pause();
    }

    public void Resume()
    {
        if (output?.PlaybackState == PlaybackState.Paused) output.Play();
    }

    /// <summary>Stops and forgets the file; <see cref="Position"/> keeps reporting <paramref name="holdAt"/>.</summary>
    public void Stop(double holdAt = 0)
    {
        StopInternal();
        heldPosition = holdAt;
        Path = null;
    }

    private void OnStopped(WaveOutEvent which)
    {
        if (which != output) return; // an older session we already replaced
        if (stoppingOnPurpose) return;
        string? errors = reader?.Errors;
        double reached = Position;
        StopInternal();
        heldPosition = reached;
        Ended?.Invoke(string.IsNullOrWhiteSpace(errors) ? null : errors);
    }

    private void StopInternal()
    {
        stoppingOnPurpose = true;
        try
        {
            // Stop ffmpeg first so a read waiting on it returns, then the sound output.
            reader?.Dispose();
            reader = null;
            if (output is not null)
            {
                try { output.Stop(); } catch (Exception) { }
                output.Dispose();
                output = null;
            }
        }
        finally
        {
            stoppingOnPurpose = false;
        }
    }

    public void Dispose() => StopInternal();

    /// <summary>Hands ffmpeg's PCM to Windows, filling every buffer completely so there are no gaps.</summary>
    private sealed class PipeProvider(PcmReader reader) : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = new(PcmReader.SampleRate, 16, PcmReader.Channels);

        public int Read(byte[] buffer, int offset, int count) => reader.ReadFull(buffer, offset, count);
    }
}
