using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Shell;
using DBoost.App.Services;
using DBoost.Core;

namespace DBoost.App.ViewModels;

public sealed record Choice<T>(T Value, string Label);

public sealed class MainViewModel : ObservableObject
{
    private readonly SemaphoreSlim probeGate = new(4);
    private readonly PreviewPlayer player = new();
    private CancellationTokenSource? runCts;
    private CancellationTokenSource? previewCts;

    public MainViewModel(AppSettings s)
    {
        gainDb = double.IsFinite(s.GainDb) ? s.GainDb : 6;
        advanced = s.Advanced;
        preventClipping = s.PreventClipping;
        ceilingDb = s.CeilingDb;
        autoLevel = s.AutoLevel;
        loudnessTarget = s.LoudnessTarget;
        peakTarget = s.PeakTarget;
        leftDb = s.LeftDb;
        rightDb = s.RightDb;
        bassDb = s.BassDb;
        trebleDb = s.TrebleDb;
        fadeIn = s.FadeInSeconds;
        fadeOut = s.FadeOutSeconds;
        format = s.Format;
        bitrate = s.BitrateKbps;
        useOutputFolder = s.UseOutputFolder && Directory.Exists(s.OutputFolder);
        outputFolder = s.OutputFolder;
        UpdateSliderRange();

        Files.CollectionChanged += OnFilesChanged;
        player.PositionChanged += p => Playhead = p;
        audio.Ended += OnListenEnded;
        listenTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        listenTimer.Tick += (_, _) =>
        {
            if (isScrubbing || !audio.IsActive) return;
            listenPosition = audio.Position;
            Raise(nameof(ListenPosition));
        };
        PlayPauseCommand = new RelayCommand(() => _ = PlayPauseAsync(), () => CanListen);

        AddFilesCommand = new RelayCommand(AddFilesWithDialog, () => !IsBusy);
        AddFolderCommand = new RelayCommand(AddFolderWithDialog, () => !IsBusy);
        ClearCommand = new RelayCommand(() => { StopPreview(); Files.Clear(); }, () => !IsBusy && Files.Count > 0);
        ClearFinishedCommand = new RelayCommand(() => { foreach (var f in Files.Where(f => f.IsDone).ToList()) Files.Remove(f); },
            () => !IsBusy && Files.Any(f => f.IsDone));
        RemoveCommand = new RelayCommand(p => { if (p is FileItem f && !IsBusy) { if (f == SelectedFile) StopPreview(); Files.Remove(f); } });
        RemoveSelectedCommand = new RelayCommand(p => RemoveSelected(p as System.Collections.IList), _ => !IsBusy);
        StartCommand = new RelayCommand(() => _ = StartAsync(), () => !IsBusy && FilesToProcess().Any());
        CancelCommand = new RelayCommand(() => runCts?.Cancel(), () => IsBusy);
        SetGainCommand = new RelayCommand(p => { if (Decibels.TryParse(p as string, out double v)) GainDb = v; });
        ChooseFolderCommand = new RelayCommand(ChooseFolder);
        UseOriginalFolderCommand = new RelayCommand(() => UseOutputFolder = false);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);
        ShowInExplorerCommand = new RelayCommand(p => { if (p is FileItem f) ShowInExplorer(f.OutputPath ?? f.Path); });
        OpenResultCommand = new RelayCommand(p => { if (p is FileItem { OutputPath: { } o }) OpenWithDefaultApp(o); });

        AnalyzeCommand = new RelayCommand(() => _ = AnalyzeSelectedAsync(), () => SelectedFile?.Info is not null && !IsAnalyzing);
        UseHeadroomCommand = new RelayCommand(() =>
        {
            if (SelectedFile is { HeadroomDb: double h } f) { f.UseOwnGain = true; f.OwnGainDb = Math.Round(h - 0.1, 1); }
        });
        PreviewCommand = new RelayCommand(() => _ = PreviewAsync(processed: true), () => SelectedFile?.WaveformTrack is not null);
        PreviewOriginalCommand = new RelayCommand(() => _ = PreviewAsync(processed: false), () => SelectedFile?.WaveformTrack is not null);
        StopPreviewCommand = new RelayCommand(StopPreview, () => IsPreviewPlaying || IsPreviewRendering);
        AddSegmentCommand = new RelayCommand(AddSegmentAtCursor, () => SelectedFile?.Waveform is not null);
        RemoveSegmentCommand = new RelayCommand(p =>
        {
            if (p is SegmentItem seg && SelectedFile is { } f) { f.Segments.Remove(seg); if (SelectedSegment == seg) SelectedSegment = null; }
        });
        ClearSegmentsCommand = new RelayCommand(() => { SelectedFile?.Segments.Clear(); SelectedSegment = null; }, () => SelectedFile?.HasSegments == true);
    }

    public ObservableCollection<FileItem> Files { get; } = [];

    // ================================================================ volume dial

    private double gainDb;
    private double sliderMin = -30, sliderMax = 30;

    /// <summary>The main dB change. Any finite number: no upper or lower limit.</summary>
    public double GainDb
    {
        get => gainDb;
        set
        {
            if (!double.IsFinite(value)) return;
            value = Math.Round(value, 2);
            if (gainDb == value) return;
            gainDb = value;
            UpdateSliderRange(); // widen the slider first so it never clamps the value
            Raise();
            Raise(nameof(EffectiveGainDb));
            Raise(nameof(GainDescription));
            Raise(nameof(StartButtonText));
        }
    }

    public double SliderMin { get => sliderMin; private set => Set(ref sliderMin, value); }
    public double SliderMax { get => sliderMax; private set => Set(ref sliderMax, value); }

    private void UpdateSliderRange()
    {
        double reach = Math.Max(30, Math.Ceiling(Math.Abs(gainDb) / 10) * 10);
        SliderMax = reach;
        SliderMin = -reach;
    }

    public string GainDescription => IsAutoLevelOn ? "Auto level picks the change for each file." : Decibels.Describe(gainDb);

    // ================================================================ advanced settings

    private bool advanced;
    public bool Advanced
    {
        get => advanced;
        set
        {
            if (!Set(ref advanced, value)) return;
            Raise(nameof(IsAutoLevelOn));
            Raise(nameof(EffectiveGainDb));
            Raise(nameof(GainDescription));
            Raise(nameof(StartButtonText));
            if (value) EnsureWaveform(SelectedFile);
            else StopPreview();
        }
    }

    private bool preventClipping;
    public bool PreventClipping { get => preventClipping; set => Set(ref preventClipping, value); }

    private double ceilingDb;
    public double CeilingDb { get => ceilingDb; set => Set(ref ceilingDb, Math.Clamp(value, -24, 0)); }

    private AutoLevel autoLevel;
    public AutoLevel AutoLevel
    {
        get => autoLevel;
        set
        {
            if (!Set(ref autoLevel, value)) return;
            Raise(nameof(IsAutoLevelOn));
            Raise(nameof(EffectiveGainDb));
            Raise(nameof(AutoLevelTarget));
            Raise(nameof(AutoLevelUnit));
            Raise(nameof(GainDescription));
            Raise(nameof(StartButtonText));
        }
    }

    public bool IsAutoLevelOn => advanced && autoLevel != AutoLevel.Off;

    private double loudnessTarget, peakTarget;

    /// <summary>LUFS for loudness, dBFS for peak; each mode remembers its own number.</summary>
    public double AutoLevelTarget
    {
        get => autoLevel == AutoLevel.Peak ? peakTarget : loudnessTarget;
        set
        {
            if (autoLevel == AutoLevel.Peak) peakTarget = Math.Clamp(value, -60, 0);
            else loudnessTarget = Math.Clamp(value, -70, 0);
            Raise();
        }
    }

    public string AutoLevelUnit => autoLevel == AutoLevel.Peak ? "dB peak" : "LUFS";

    private double leftDb, rightDb, bassDb, trebleDb, fadeIn, fadeOut;
    public double LeftDb { get => leftDb; set => Set(ref leftDb, value); }
    public double RightDb { get => rightDb; set => Set(ref rightDb, value); }
    public double BassDb { get => bassDb; set => Set(ref bassDb, Math.Clamp(Math.Round(value, 1), -20, 20)); }
    public double TrebleDb { get => trebleDb; set => Set(ref trebleDb, Math.Clamp(Math.Round(value, 1), -20, 20)); }
    public double FadeIn { get => fadeIn; set => Set(ref fadeIn, Math.Clamp(value, 0, 3600)); }
    public double FadeOut { get => fadeOut; set => Set(ref fadeOut, Math.Clamp(value, 0, 3600)); }

    private OutputFormat format;
    public OutputFormat Format { get => format; set { if (Set(ref format, value)) Raise(nameof(FormatHint)); } }

    public string? FormatHint => format switch
    {
        OutputFormat.Mp4 or OutputFormat.Mkv => "Applies to videos. Audio files keep their own format.",
        _ when format.IsAudioOnly() => "Videos become audio-only files.",
        _ => null,
    };

    private int bitrate;
    public int Bitrate { get => bitrate; set => Set(ref bitrate, value); }

    public IReadOnlyList<Choice<AutoLevel>> AutoLevelChoices { get; } =
    [
        new(AutoLevel.Off, "Off (use the dB change above)"),
        new(AutoLevel.Loudness, "Same loudness for every file"),
        new(AutoLevel.Peak, "As loud as possible without clipping"),
    ];

    public IReadOnlyList<Choice<OutputFormat>> FormatChoices { get; } =
    [
        new(OutputFormat.SameAsSource, "Same as original"),
        new(OutputFormat.Mp3, "MP3 audio"),
        new(OutputFormat.M4a, "M4A audio (AAC)"),
        new(OutputFormat.Wav, "WAV audio (uncompressed)"),
        new(OutputFormat.Flac, "FLAC audio (lossless)"),
        new(OutputFormat.Ogg, "OGG audio (Vorbis)"),
        new(OutputFormat.Opus, "Opus audio"),
        new(OutputFormat.Mp4, "MP4 video"),
        new(OutputFormat.Mkv, "MKV video"),
    ];

    public IReadOnlyList<Choice<int>> BitrateChoices { get; } =
    [
        new(0, "Match original"),
        new(96, "96 kbps"),
        new(128, "128 kbps"),
        new(160, "160 kbps"),
        new(192, "192 kbps"),
        new(256, "256 kbps"),
        new(320, "320 kbps"),
    ];

    /// <summary>The settings for a run. Simple mode is only the dB change (with clipping protection).</summary>
    public BoostSettings BuildSettings()
    {
        string? folder = useOutputFolder ? outputFolder : null;
        if (!advanced) return BoostSettings.Simple(gainDb, folder);
        return new BoostSettings
        {
            GainDb = gainDb,
            PreventClipping = preventClipping,
            CeilingDb = ceilingDb,
            AutoLevel = autoLevel,
            AutoLevelTarget = AutoLevelTarget,
            LeftDb = leftDb,
            RightDb = rightDb,
            BassDb = bassDb,
            TrebleDb = trebleDb,
            FadeInSeconds = fadeIn,
            FadeOutSeconds = fadeOut,
            Format = format,
            BitrateKbps = bitrate > 0 ? bitrate : null,
            OutputFolder = folder,
        };
    }

    private FileEdits EditsFor(FileItem f) => advanced ? f.ToEdits() : FileEdits.None;

    // ================================================================ output folder

    private bool useOutputFolder;
    private string? outputFolder;

    public bool UseOutputFolder
    {
        get => useOutputFolder;
        set { if (Set(ref useOutputFolder, value && !string.IsNullOrEmpty(outputFolder))) Raise(nameof(OutputDisplay)); }
    }

    public string? OutputFolder => outputFolder;

    public string OutputDisplay => useOutputFolder && outputFolder is not null ? outputFolder : "Same folder as each file";

    private void ChooseFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Save boosted files to", Multiselect = false };
        if (outputFolder is not null && Directory.Exists(outputFolder)) dlg.InitialDirectory = outputFolder;
        if (dlg.ShowDialog(Application.Current.MainWindow) == true)
        {
            outputFolder = dlg.FolderName;
            useOutputFolder = true;
            Raise(nameof(UseOutputFolder));
            Raise(nameof(OutputFolder));
            Raise(nameof(OutputDisplay));
        }
    }

    private void OpenOutputFolder()
    {
        if (useOutputFolder && outputFolder is not null && Directory.Exists(outputFolder))
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { outputFolder } });
            return;
        }
        var last = Files.LastOrDefault(f => f.OutputPath is not null) ?? Files.FirstOrDefault();
        if (last is not null) ShowInExplorer(last.OutputPath ?? last.Path);
    }

    public static void ShowInExplorer(string path)
    {
        if (File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""));
        else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir))
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { dir } });
    }

    private static void OpenWithDefaultApp(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { ShowInExplorer(path); }
    }

    // ================================================================ adding files

    public void AddPaths(IEnumerable<string> paths)
    {
        var known = new HashSet<string>(Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        FileItem? first = null;
        foreach (string p in MediaExtensions.Expand(paths))
        {
            if (!known.Add(p)) continue;
            var item = new FileItem(p);
            Files.Add(item);
            first ??= item;
            _ = LoadAsync(item);
        }
        if (first is not null && SelectedFile is null) SelectedFile = first;
    }

    private void AddFilesWithDialog()
    {
        string audio = string.Join(";", MediaExtensions.Audio.Select(e => "*" + e));
        string video = string.Join(";", MediaExtensions.Video.Select(e => "*" + e));
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add audio or video files",
            Multiselect = true,
            Filter = $"Audio and video|{audio};{video}|Audio|{audio}|Video|{video}|All files|*.*",
        };
        if (dlg.ShowDialog(Application.Current.MainWindow) == true) AddPaths(dlg.FileNames);
    }

    private void AddFolderWithDialog()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Add every audio and video file in a folder", Multiselect = true };
        if (dlg.ShowDialog(Application.Current.MainWindow) == true) AddPaths(dlg.FolderNames);
    }

    private void RemoveSelected(System.Collections.IList? selected)
    {
        if (selected is null) return;
        foreach (var f in selected.OfType<FileItem>().ToList())
        {
            if (f == SelectedFile) StopPreview();
            Files.Remove(f);
        }
    }

    private async Task LoadAsync(FileItem item)
    {
        _ = LoadThumbnailAsync(item);
        try
        {
            var engine = await EngineHost.GetAsync();
            await probeGate.WaitAsync();
            try
            {
                var info = await engine.ProbeAsync(item.Path);
                item.Info = info;
                if (info.AudioStreams.Count == 0)
                {
                    item.Message = "This file has no sound to boost.";
                    item.State = FileState.Failed;
                }
                else
                {
                    item.State = FileState.Ready;
                    if (item == SelectedFile) EnsureWaveform(item);
                }
            }
            finally { probeGate.Release(); }
        }
        catch (Exception ex)
        {
            item.Message = ex is MediaProbeException or BoostException ? ex.Message : $"Could not read the file: {ex.Message}";
            item.State = FileState.Failed;
        }
        Raise(nameof(StartButtonText));
        CommandManager.InvalidateRequerySuggested();
    }

    private static async Task LoadThumbnailAsync(FileItem item)
    {
        int px = (int)Math.Ceiling(48 * (Application.Current?.MainWindow is { } w ? System.Windows.Media.VisualTreeHelper.GetDpi(w).DpiScaleX : 1.5));
        item.Thumbnail = await ShellThumbnails.GetAsync(item.Path, Math.Max(48, px));
    }

    private void OnFilesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (SelectedFile is not null && !Files.Contains(SelectedFile)) SelectedFile = Files.FirstOrDefault();
        Raise(nameof(HasFiles));
        Raise(nameof(FileCountText));
        Raise(nameof(StartButtonText));
        CommandManager.InvalidateRequerySuggested();
    }

    public bool HasFiles => Files.Count > 0;

    public string FileCountText => Files.Count switch { 0 => "No files yet", 1 => "1 file", int n => $"{n} files" };

    // ================================================================ running

    private bool isBusy;
    public bool IsBusy
    {
        get => isBusy;
        private set { if (Set(ref isBusy, value)) { Raise(nameof(StartButtonText)); CommandManager.InvalidateRequerySuggested(); } }
    }

    private double overallProgress;
    public double OverallProgress { get => overallProgress; private set => Set(ref overallProgress, value); }

    private TaskbarItemProgressState taskbarState = TaskbarItemProgressState.None;
    public TaskbarItemProgressState TaskbarState { get => taskbarState; private set => Set(ref taskbarState, value); }

    private string statusText = "";
    public string StatusText { get => statusText; private set => Set(ref statusText, value); }

    /// <summary>Raised when a batch ends (the window flashes its taskbar button if it is in the background).</summary>
    public event Action? BatchFinished;

    private IEnumerable<FileItem> FilesToProcess()
    {
        var usable = Files.Where(f => f.IsUsable).ToList();
        var fresh = usable.Where(f => f.State != FileState.Done && f.State != FileState.Skipped).ToList();
        return fresh.Count > 0 ? fresh : usable;
    }

    public string StartButtonText
    {
        get
        {
            if (isBusy) return "Working...";
            var todo = FilesToProcess().ToList();
            bool again = todo.Count > 0 && todo.All(f => f.State is FileState.Done or FileState.Skipped);
            string verb = IsAutoLevelOn ? "Level" : gainDb < 0 ? "Lower" : "Boost";
            if (again) return $"{verb} again";
            return todo.Count switch { 0 => verb, 1 => $"{verb} 1 file", int n => $"{verb} {n} files" };
        }
    }

    private async Task StartAsync()
    {
        var todo = FilesToProcess().ToList();
        if (todo.Count == 0) return;
        StopPreview();

        BoostEngine engine;
        try { engine = await EngineHost.GetAsync(); }
        catch (Exception ex)
        {
            StatusText = $"ffmpeg could not start: {ex.Message}";
            return;
        }

        var settings = BuildSettings();
        runCts = new CancellationTokenSource();
        var ct = runCts.Token;
        IsBusy = true;
        Native.KeepAwake(true);
        TaskbarState = TaskbarItemProgressState.Normal;
        OverallProgress = 0;
        StatusText = "";

        foreach (var f in todo)
        {
            f.State = FileState.Waiting;
            f.Progress = 0;
            f.IsIndeterminate = false;
            f.Message = null;
            f.OutputPath = null;
            f.CanRetry = false;
        }

        int parallel = Math.Clamp(Environment.ProcessorCount / 4, 1, 3);
        using var gate = new SemaphoreSlim(parallel);

        void UpdateOverall() => OverallProgress = todo.Average(f => f.State is FileState.Done or FileState.Skipped or FileState.Failed ? 1 : f.Progress);

        var tasks = todo.Select(async f =>
        {
            try { await gate.WaitAsync(ct); }
            catch (OperationCanceledException)
            {
                f.State = FileState.Ready;
                return;
            }
            try
            {
                ct.ThrowIfCancellationRequested();
                f.State = FileState.Working;
                var progress = new Progress<double>(v =>
                {
                    if (double.IsNaN(v)) f.IsIndeterminate = true;
                    else { f.IsIndeterminate = false; f.Progress = v; }
                    UpdateOverall();
                });
                var edits = EditsFor(f);
                var result = await engine.ProcessAsync(f.Info!, settings, edits, ((IProgress<double>)progress).Report, ct);
                if (result.OutputPath is { } written)
                {
                    try { f.OutputInfo = await engine.ProbeAsync(written); }
                    catch (Exception ex) when (ex is MediaProbeException or IOException or InvalidOperationException) { f.OutputInfo = null; }
                }
                f.OutputPath = result.OutputPath;
                f.Message = result.Notes.Count > 0 ? string.Join(" ", result.Notes) : null;
                f.State = result.Skipped ? FileState.Skipped : FileState.Done;
                if (f == selectedFile && !audio.IsPlaying) StopListening();
            }
            catch (OperationCanceledException)
            {
                f.State = FileState.Ready;
                f.Progress = 0;
            }
            catch (Exception ex)
            {
                f.Message = ex is BoostException or MediaProbeException ? ex.Message : $"Something went wrong: {ex.Message}";
                f.CanRetry = true;
                f.State = FileState.Failed;
            }
            finally
            {
                f.IsIndeterminate = false;
                gate.Release();
                UpdateOverall();
            }
        }).ToList();

        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { }

        int done = todo.Count(f => f.State == FileState.Done);
        int failed = todo.Count(f => f.State == FileState.Failed);
        int skipped = todo.Count(f => f.State == FileState.Skipped);
        bool cancelled = ct.IsCancellationRequested;

        var parts = new List<string>();
        if (done > 0) parts.Add(done == 1 ? "1 file saved" : $"{done} files saved");
        if (skipped > 0) parts.Add($"{skipped} unchanged");
        if (failed > 0) parts.Add($"{failed} failed");
        StatusText = (cancelled ? "Stopped. " : "") + (parts.Count > 0 ? string.Join(", ", parts) + "." : "");

        TaskbarState = failed > 0 ? TaskbarItemProgressState.Error : TaskbarItemProgressState.None;
        Native.KeepAwake(false);
        IsBusy = false;
        runCts.Dispose();
        runCts = null;
        BatchFinished?.Invoke();
    }

    // ================================================================ selection and the parts editor

    private FileItem? selectedFile;
    public FileItem? SelectedFile
    {
        get => selectedFile;
        set
        {
            if (selectedFile == value) return;
            StopPreview();
            StopListening();
            if (selectedFile is not null) selectedFile.PropertyChanged -= OnSelectedFileChanged;
            selectedFile = value;
            if (selectedFile is not null) selectedFile.PropertyChanged += OnSelectedFileChanged;
            SelectedSegment = null;
            Raise();
            Raise(nameof(HasSelection));
            Raise(nameof(EffectiveGainDb));
            RaiseListen();
            if (advanced) EnsureWaveform(value);
        }
    }

    public bool HasSelection => selectedFile is not null;

    /// <summary>The overall change the selected file will get, so its waveform can show the result.</summary>
    public double EffectiveGainDb =>
        IsAutoLevelOn ? 0 : selectedFile is { UseOwnGain: true } f ? f.OwnGainDb : gainDb;

    private void OnSelectedFileChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FileItem.UseOwnGain) or nameof(FileItem.OwnGainDb)) Raise(nameof(EffectiveGainDb));
        if (e.PropertyName is nameof(FileItem.Info) or nameof(FileItem.OutputPath) or nameof(FileItem.OutputInfo) or nameof(FileItem.State))
            RaiseListen();
    }

    private SegmentItem? selectedSegment;
    public SegmentItem? SelectedSegment { get => selectedSegment; set => Set(ref selectedSegment, value); }

    private double? playhead;
    public double? Playhead { get => playhead; private set { if (Set(ref playhead, value)) Raise(nameof(IsPreviewPlaying)); } }

    public bool IsPreviewPlaying => playhead is not null;

    private bool isPreviewRendering;
    public bool IsPreviewRendering { get => isPreviewRendering; private set => Set(ref isPreviewRendering, value); }

    private bool isAnalyzing;
    public bool IsAnalyzing { get => isAnalyzing; private set => Set(ref isAnalyzing, value); }

    public const double PreviewSeconds = 10;

    /// <summary>Loads the waveform of the selected track the first time it is needed.</summary>
    public void EnsureWaveform(FileItem? f)
    {
        if (f?.Info is null || f.WaveformTrack is null || f.Waveform is not null || f.WaveformLoading) return;
        _ = LoadWaveformAsync(f, f.WaveformTrack);
    }

    private async Task LoadWaveformAsync(FileItem f, TrackItem track)
    {
        f.WaveformLoading = true;
        f.WaveformError = null;
        try
        {
            var engine = await EngineHost.GetAsync();
            var wave = await engine.ReadWaveformAsync(f.Info!, track.Info, 2400);
            if (f.WaveformTrack == track) f.Waveform = wave;
        }
        catch (Exception ex)
        {
            f.WaveformError = $"Could not draw the waveform: {ex.Message}";
        }
        finally
        {
            f.WaveformLoading = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Called by the view when the track to show changes.</summary>
    public void OnWaveformTrackChanged(FileItem f)
    {
        StopPreview();
        EnsureWaveform(f);
        if (f == selectedFile && audio.IsActive) RestartListening();
    }

    private void AddSegmentAtCursor()
    {
        if (SelectedFile is not { Waveform: { } w } f) return;
        double start = Math.Clamp(f.CursorSeconds, 0, Math.Max(0, w.DurationSeconds - 1));
        double end = Math.Min(w.DurationSeconds, start + Math.Max(1, Math.Min(10, w.DurationSeconds / 5)));
        var seg = new SegmentItem(start, end, 6);
        f.Segments.Add(seg);
        SelectedSegment = seg;
    }

    private string AnalysisKey(FileItem f, TrackItem t, BoostSettings s) =>
        $"{t.Info.Ordinal}|{FilterBuilder.BuildPreLevelChain(s, f.ToEdits(), t.Info, f.Info?.DurationSeconds)}";

    private async Task<LoudnessAnalysis> GetAnalysisAsync(FileItem f, TrackItem t, BoostSettings s, CancellationToken ct = default)
    {
        string key = AnalysisKey(f, t, s);
        if (f.AnalysisCache.TryGetValue(key, out var cached)) return cached;
        var engine = await EngineHost.GetAsync();
        var a = await engine.AnalyzeAsync(f.Info!, t.Info, s, EditsFor(f), null, ct);
        f.AnalysisCache[key] = a;
        return a;
    }

    private async Task AnalyzeSelectedAsync()
    {
        if (SelectedFile is not { Info: not null, WaveformTrack: { } t } f) return;
        IsAnalyzing = true;
        f.AnalysisText = "Measuring...";
        try
        {
            var settings = BuildSettings();
            var a = await GetAnalysisAsync(f, t, settings);
            if (a.IsSilent)
            {
                f.AnalysisText = "This track is silent.";
                f.HeadroomDb = null;
            }
            else
            {
                string lufs = a.IntegratedLufs is double i ? $"{Decibels.FormatNumber(i)} LUFS" : "too quiet to measure";
                f.AnalysisText = $"Loudest peak {Decibels.Format(a.PeakDb)}  ·  Loudness {lufs}  ·  "
                    + $"Room before clipping {Decibels.Format(a.HeadroomDb)}";
                f.HeadroomDb = a.HeadroomDb;
            }
        }
        catch (Exception ex)
        {
            f.AnalysisText = $"Could not measure: {ex.Message}";
        }
        finally
        {
            IsAnalyzing = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task PreviewAsync(bool processed)
    {
        if (SelectedFile is not { Info: { } info, WaveformTrack: { } track } f) return;
        StopPreview();
        if (audio.IsPlaying) { audio.Pause(); listenPosition = audio.Position; RaiseListen(); }
        previewCts = new CancellationTokenSource();
        var ct = previewCts.Token;
        IsPreviewRendering = true;
        string wav = PreviewPlayer.NewTempWav();
        try
        {
            double duration = f.Waveform?.DurationSeconds ?? info.DurationSeconds ?? PreviewSeconds;
            double start = f.CursorSeconds;
            if (start > duration - 0.5) start = 0;
            double length = Math.Min(PreviewSeconds, duration - start);

            var settings = BuildSettings();
            var edits = EditsFor(f);
            bool apply = processed && track.IsBoosted;
            double gain = edits.EffectiveGain(settings);
            if (apply && settings.AutoLevel != AutoLevel.Off)
                gain = BoostEngine.AutoLevelGain(settings, await GetAnalysisAsync(f, track, settings, ct)).GainDb;

            var engine = await EngineHost.GetAsync();
            await engine.RenderPreviewAsync(info, track.Info, settings, edits, gain, start, length, apply, wav, ct);
            ct.ThrowIfCancellationRequested();
            IsPreviewRendering = false;
            player.Play(wav, start, length);
        }
        catch (OperationCanceledException) { TryDelete(wav); }
        catch (Exception ex)
        {
            TryDelete(wav);
            StatusText = $"Preview failed: {ex.Message}";
        }
        finally
        {
            IsPreviewRendering = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public void StopPreview()
    {
        previewCts?.Cancel();
        previewCts = null;
        player.Stop();
        Playhead = null;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ================================================================ listening to the selected file

    private readonly AudioPlayer audio = new();
    private readonly System.Windows.Threading.DispatcherTimer listenTimer;
    private bool listenToOriginal;
    private double listenPosition;
    private bool isScrubbing;
    private FileItem? playingItem;

    /// <summary>The selected file has sound the player can play.</summary>
    public bool CanListen => selectedFile?.Info is { AudioStreams.Count: > 0 };

    /// <summary>The selected file has a boosted copy on disk.</summary>
    public bool HasNewFile => selectedFile is { OutputPath: { } o } && File.Exists(o);

    /// <summary>Compare: play the original instead of the new file (switches at the same spot while playing).</summary>
    public bool ListenToOriginal
    {
        get => listenToOriginal;
        set { if (Set(ref listenToOriginal, value)) RestartListening(); }
    }

    public bool IsListening => audio.IsPlaying;
    /// <summary>Playback position. The progress bar writes it while you drag; the jump itself happens when you let go.</summary>
    public double ListenPosition
    {
        get => listenPosition;
        set { if (double.IsFinite(value)) listenPosition = value; }
    }
    public double ListenDuration => CurrentSource() is { Info: { DurationSeconds: double d } } ? d : 0;

    private bool UsingNewFile => !listenToOriginal && HasNewFile;

    public string ListenTitle => (UsingNewFile ? selectedFile?.OutputName : selectedFile?.Name) ?? "";

    public string ListenSourceLabel =>
        UsingNewFile ? "New file"
        : HasNewFile ? "Original"
        : selectedFile?.IsDone == true ? "Original (the new file was moved or deleted)"
        : "Original (not boosted yet)";

    private (string Path, MediaInfo? Info)? CurrentSource()
    {
        if (selectedFile?.Info is not { } info) return null;
        if (UsingNewFile) return (selectedFile.OutputPath!, selectedFile.OutputInfo);
        return (selectedFile.Path, info);
    }

    /// <summary>The track to hear: the one picked for the waveform in advanced mode, else the file's default track.</summary>
    private int TrackFor(MediaInfo? source)
    {
        var f = selectedFile!;
        int wanted = advanced && f.WaveformTrack is not null
            ? f.WaveformTrack.Info.Ordinal
            : f.Info!.AudioStreams.FirstOrDefault(a => a.IsDefault)?.Ordinal ?? 0;
        return source is not null && wanted < source.AudioStreams.Count ? wanted : 0;
    }

    private async Task PlayPauseAsync()
    {
        if (audio.IsPlaying)
        {
            audio.Pause();
            listenPosition = audio.Position;
            RaiseListen();
            return;
        }
        if (audio.IsPaused && audio.Path == CurrentSource()?.Path)
        {
            StopPreview();
            audio.Resume();
            listenTimer.Start();
            RaiseListen();
            return;
        }
        await StartListeningAsync(listenPosition);
    }

    private async Task StartListeningAsync(double from)
    {
        if (CurrentSource() is not { } src || selectedFile is not { } f) return;
        if (!File.Exists(src.Path))
        {
            StatusText = "That file is not there any more.";
            return;
        }
        StopPreview();
        double duration = ListenDuration;
        if (duration > 0 && from >= duration - 0.25) from = 0;
        try
        {
            var engine = await EngineHost.GetAsync();
            if (selectedFile != f) return; // the selection changed while ffmpeg was starting
            audio.Play(engine, src.Path, TrackFor(src.Info), from);
            if (playingItem is not null && playingItem != f) playingItem.IsPlayingNow = false;
            playingItem = f;
            f.IsPlayingNow = true;
            listenPosition = from;
            listenTimer.Start();
        }
        catch (Exception ex)
        {
            StatusText = $"Could not play: {ex.Message}";
        }
        RaiseListen();
    }

    /// <summary>Jump to a time (from the progress bar). Keeps playing if it was playing.</summary>
    public void SeekListening(double seconds)
    {
        double max = ListenDuration;
        seconds = max > 0 ? Math.Clamp(seconds, 0, max) : Math.Max(0, seconds);
        if (audio.IsPlaying)
        {
            _ = StartListeningAsync(seconds);
            return;
        }
        audio.Stop(seconds);
        listenPosition = seconds;
        RaiseListen();
    }

    public void BeginScrub() => isScrubbing = true;

    public void EndScrub(double seconds)
    {
        isScrubbing = false;
        SeekListening(seconds);
    }

    /// <summary>Same spot, different source or track (the New file / Original switch).</summary>
    private void RestartListening()
    {
        double at = audio.IsActive ? audio.Position : listenPosition;
        double max = ListenDuration;
        if (max > 0) at = Math.Min(at, Math.Max(0, max - 0.5));
        if (audio.IsPlaying) _ = StartListeningAsync(at);
        else
        {
            audio.Stop(at);
            listenPosition = at;
            RaiseListen();
        }
    }

    public void StopListening()
    {
        audio.Stop();
        listenTimer.Stop();
        listenPosition = 0;
        if (playingItem is not null) playingItem.IsPlayingNow = false;
        playingItem = null;
        RaiseListen();
    }

    private void OnListenEnded(string? errors)
    {
        listenTimer.Stop();
        bool failedEarly = errors is not null && audio.Position < 0.5;
        listenPosition = 0;
        if (playingItem is not null) playingItem.IsPlayingNow = false;
        playingItem = null;
        if (failedEarly) StatusText = $"Could not play: {errors!.Split('\n')[0].Trim()}";
        RaiseListen();
    }

    private void RaiseListen()
    {
        Raise(nameof(CanListen));
        Raise(nameof(HasNewFile));
        Raise(nameof(IsListening));
        Raise(nameof(ListenPosition));
        Raise(nameof(ListenDuration));
        Raise(nameof(ListenTitle));
        Raise(nameof(ListenSourceLabel));
        Raise(nameof(ListenToOriginal));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Starts ffmpeg early (the first start unpacks it) and says so if that takes a moment or fails.</summary>
    public async Task WarmUpAsync()
    {
        var task = EngineHost.GetAsync();
        if (!task.IsCompleted)
        {
            await Task.WhenAny(task, Task.Delay(400));
            if (!task.IsCompleted) StatusText = "Getting ready (the first start unpacks the audio engine)...";
        }
        try
        {
            await task;
            if (StatusText.StartsWith("Getting ready", StringComparison.Ordinal)) StatusText = "";
        }
        catch (Exception ex)
        {
            StatusText = $"The audio engine (ffmpeg) could not start: {ex.Message}";
        }
    }

    // ================================================================ persistence

    public void SaveInto(AppSettings s)
    {
        s.GainDb = gainDb;
        s.Advanced = advanced;
        s.PreventClipping = preventClipping;
        s.CeilingDb = ceilingDb;
        s.AutoLevel = autoLevel;
        s.LoudnessTarget = loudnessTarget;
        s.PeakTarget = peakTarget;
        s.LeftDb = leftDb;
        s.RightDb = rightDb;
        s.BassDb = bassDb;
        s.TrebleDb = trebleDb;
        s.FadeInSeconds = fadeIn;
        s.FadeOutSeconds = fadeOut;
        s.Format = format;
        s.BitrateKbps = bitrate;
        s.UseOutputFolder = useOutputFolder;
        s.OutputFolder = outputFolder;
    }

    public void Shutdown()
    {
        listenTimer.Stop();
        audio.Dispose();
        runCts?.Cancel();
        StopPreview();
        PreviewPlayer.CleanTempFolder();
        Native.KeepAwake(false);
    }

    // ================================================================ commands

    public ICommand AddFilesCommand { get; }
    public ICommand AddFolderCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand ClearFinishedCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand RemoveSelectedCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand SetGainCommand { get; }
    public ICommand ChooseFolderCommand { get; }
    public ICommand UseOriginalFolderCommand { get; }
    public ICommand OpenOutputFolderCommand { get; }
    public ICommand ShowInExplorerCommand { get; }
    public ICommand OpenResultCommand { get; }
    public ICommand AnalyzeCommand { get; }
    public ICommand UseHeadroomCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand PreviewOriginalCommand { get; }
    public ICommand StopPreviewCommand { get; }
    public ICommand AddSegmentCommand { get; }
    public ICommand RemoveSegmentCommand { get; }
    public ICommand ClearSegmentsCommand { get; }
    public ICommand PlayPauseCommand { get; }
}
