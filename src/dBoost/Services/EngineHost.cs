using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using DBoost.Core;

namespace DBoost.App.Services;

/// <summary>Gets ffmpeg ready (unpacking the bundled copy the first time) and creates the engine once.</summary>
public static class EngineHost
{
    private static readonly Lazy<Task<BoostEngine>> Engine = new(() => Task.Run(CreateAsync));

    public static Task<BoostEngine> GetAsync() => Engine.Value;

    /// <summary>%LOCALAPPDATA%\dBoost</summary>
    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dBoost");

    public static string? BundledFfmpegHash =>
        typeof(EngineHost).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "FfmpegSha256")?.Value is { Length: 64 } h ? h : null;

    /// <summary>Which ffmpeg is in use, for the About box.</summary>
    public static string? FfmpegPath { get; private set; }

    private static async Task<BoostEngine> CreateAsync()
    {
        string path = Locate();
        FfmpegPath = path;
        return await BoostEngine.CreateAsync(path).ConfigureAwait(false);
    }

    private static string Locate()
    {
        if (FfmpegLocator.FromEnvironment() is { } env) return env;
        if (FfmpegLocator.NextTo(AppContext.BaseDirectory) is { } beside) return beside;
        if (BundledFfmpegHash is { } hash) return ProvisionBundled(hash);
        return FfmpegLocator.OnPath()
            ?? throw new InvalidOperationException("ffmpeg was not found. This build has no ffmpeg inside; put ffmpeg.exe next to dBoost.exe.");
    }

    // Kept out of line so the payload assembly is only loaded (and decompressed) when it is really needed.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string ProvisionBundled(string hash) =>
        FfmpegLocator.Provision(hash, OpenPayload, DataFolder, OpenLicense);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Stream? OpenPayload() => DBoost.Ffmpeg.FfmpegPayload.Open();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Stream? OpenLicense() => DBoost.Ffmpeg.FfmpegPayload.OpenLicense();
}
