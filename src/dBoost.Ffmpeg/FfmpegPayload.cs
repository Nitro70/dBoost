namespace DBoost.Ffmpeg;

/// <summary>The ffmpeg.exe bundled into this build, or null when it was built without one.</summary>
public static class FfmpegPayload
{
    public static Stream? Open() => typeof(FfmpegPayload).Assembly.GetManifestResourceStream("ffmpeg.exe");

    /// <summary>FFmpeg's licence text (LGPL), written next to the unpacked ffmpeg.exe.</summary>
    public static Stream? OpenLicense() => typeof(FfmpegPayload).Assembly.GetManifestResourceStream("ffmpeg-LICENSE.txt");
}
