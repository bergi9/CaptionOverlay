using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CaptionOverlay.App.FirstRun;
using CaptionOverlay.App.Hotkeys;
using CaptionOverlay.App.Infrastructure;
using CaptionOverlay.App.Overlay;
using CaptionOverlay.App.Settings;
using CaptionOverlay.App.Tray;
using CaptionOverlay.Core;
using CaptionOverlay.Core.Audio;
using CaptionOverlay.Core.Captions;
using CaptionOverlay.Core.Diagnostics;
using CaptionOverlay.Core.Export;
using CaptionOverlay.Core.Models;
using CaptionOverlay.Core.Pipeline;
using CaptionOverlay.Core.Segmentation;
using CaptionOverlay.Core.Settings;
using CaptionOverlay.Core.Transcription;
using CaptionOverlay.Core.Vad;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace CaptionOverlay.App;

public enum SettingsSection
{
    General,
    Audio,
    Engine,
    Api,
    Overlay,
    Hotkeys,
    Transcripts,
}

/// <summary>Composition root: owns settings, the pipeline, overlay, tray and hotkeys, and wires them together.</summary>
public sealed partial class AppController : ObservableObject, IAsyncDisposable
{
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;
    private readonly Dispatcher _dispatcher;
    private readonly SettingsStore _settingsStore;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _reloadTimer;
    private readonly DispatcherTimer _metricsTimer;
    private readonly SemaphoreSlim _engineGate = new(1, 1);
    private OverlayWindow? _overlay;
    private TrayIconManager? _tray;
    private HotkeyService? _hotkeys;
    private SettingsWindow? _settingsWindow;
    private FirstRunWindow? _firstRunWindow;
    private TranscriptSession? _transcript;
    private bool _quitting;
    private bool _startError;
    private (string? Language, bool Partials, EngineMode Mode, bool Streaming) _runningEngineParams;

    public AppController(ILoggerFactory loggers)
    {
        _loggers = loggers;
        _logger = loggers.CreateLogger<AppController>();
        _dispatcher = Dispatcher.CurrentDispatcher;
        _settingsStore = new SettingsStore(logger: loggers.CreateLogger<SettingsStore>());
        Settings = _settingsStore.Load();
        Secrets = new SecretStore();
        Http = new HttpClient();
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"CaptionOverlay/{UpdateChecker.CurrentVersion.ToString(3)}");
        Catalog = ModelCatalog.LoadBundled();
        ModelStore = new ModelStore();
        Downloader = new ModelDownloader(Http, ModelStore, loggers.CreateLogger<ModelDownloader>());
        Pipeline = new CaptionPipeline(loggers);
        OverlayViewModel = new OverlayViewModel(Settings.Overlay);

        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => SaveNow(), _dispatcher) { IsEnabled = false };
        _reloadTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, async (_, _) => await ReloadEngineAsync(), _dispatcher) { IsEnabled = false };
        _metricsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateStatusText(), _dispatcher);

        Pipeline.StatusChanged += s => _dispatcher.InvokeAsync(() => OnPipelineStatus(s));
        Pipeline.Captions.Changed += (_, _) => _dispatcher.InvokeAsync(RenderCaptions, DispatcherPriority.Render);
        Pipeline.LineCommitted += OnLineCommitted;
    }

    public AppSettings Settings { get; }

    public SecretStore Secrets { get; }

    public HttpClient Http { get; }

    public ModelCatalog Catalog { get; private set; }

    public ModelStore ModelStore { get; }

    public ModelDownloader Downloader { get; }

    public CaptionPipeline Pipeline { get; }

    public OverlayViewModel OverlayViewModel { get; }

    public ILoggerFactory Loggers => _loggers;

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsListening { get; private set; }

    [ObservableProperty]
    public partial bool IsPaused { get; private set; }

    /// <summary>Raised when the model catalog was refreshed from the remote copy.</summary>
    public event Action? CatalogChanged;

    public async Task InitializeAsync(bool autostart)
    {
        ApplyAppearance();
        Loc.CultureChanged += OnCultureChanged;
        StatusText = Loc.Get("Status_NotListening");
        _overlay = new OverlayWindow(OverlayViewModel, Settings.Overlay);
        _overlay.PlacementChanged += ScheduleSave;
        OverlayViewModel.StyleEdited += ScheduleSave;
        OverlayViewModel.EditFinished += () => SetEditMode(false);
        _overlay.Show();

        _tray = new TrayIconManager(this);
        _hotkeys = new HotkeyService(_loggers.CreateLogger<HotkeyService>());
        RegisterHotkeys();
        _metricsTimer.Start();

        _ = RefreshCatalogAsync();
        CheckStartupEntry();
        if (Settings.General.CheckForUpdates)
        {
            _ = CheckForUpdatesAsync();
        }

        if (!Settings.FirstRunCompleted)
        {
            ShowFirstRun();
            return;
        }
        if (Settings.General.StartListeningOnLaunch || autostart)
        {
            await StartListeningAsync();
        }
    }

    // ───────────────────────────── Listening ─────────────────────────────

    public async Task StartListeningAsync()
    {
        if (Pipeline.IsRunning)
        {
            return;
        }
        await _engineGate.WaitAsync();
        try
        {
            var config = BuildPipelineConfig();
            _startError = false;
            await Pipeline.StartAsync(config, BuildTranscriberFactory());
            StartTranscriptSession();
            IsListening = true;
            IsPaused = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not start listening");
            StatusText = ex is TranscriptionException or InvalidOperationException ? ex.Message : Loc.Format("Status_CouldNotStart", ex.Message);
            _startError = true;
            _tray?.Notify("CaptionOverlay", StatusText);
            IsListening = false;
        }
        finally
        {
            _engineGate.Release();
            UpdateStatusText();
        }
    }

    public async Task StopListeningAsync()
    {
        await _engineGate.WaitAsync();
        try
        {
            await Pipeline.StopAsync();
            _transcript?.Dispose();
            _transcript = null;
            IsListening = false;
            IsPaused = false;
        }
        finally
        {
            _engineGate.Release();
            UpdateStatusText();
        }
    }

    public Task ToggleListeningAsync() => Pipeline.IsRunning ? StopListeningAsync() : StartListeningAsync();

    public void TogglePause()
    {
        if (!Pipeline.IsRunning)
        {
            return;
        }
        Pipeline.SetPaused(!Pipeline.IsPaused);
        IsPaused = Pipeline.IsPaused;
        if (IsPaused)
        {
            OverlayViewModel.Update(new CaptionSnapshot([], null));
        }
        UpdateStatusText();
    }

    public void ClearOverlay() => Pipeline.Captions.ClearDisplay();

    public void ToggleEditMode() => SetEditMode(!(_overlay?.IsEditMode ?? false));

    public void SetEditMode(bool edit)
    {
        _overlay?.SetEditMode(edit);
        if (!edit)
        {
            ScheduleSave();
        }
        UpdateStatusText();
    }

    /// <summary>Switch engine quickly from the tray: local model id, or API mode.</summary>
    public void SwitchEngine(EngineMode mode, string? modelId = null)
    {
        Settings.Engine.Mode = mode;
        if (modelId is not null)
        {
            Settings.Engine.ModelId = modelId;
        }
        ApplySettings(SettingsSection.Engine);
    }

    // ───────────────────────────── Settings ─────────────────────────────

    /// <summary>Applies changed settings live and persists them.</summary>
    public void ApplySettings(SettingsSection section)
    {
        switch (section)
        {
            case SettingsSection.Overlay:
                OverlayViewModel.ApplyStyle(Settings.Overlay);
                RenderCaptions();
                break;
            case SettingsSection.Hotkeys:
                RegisterHotkeys();
                break;
            case SettingsSection.Engine:
            case SettingsSection.Api:
                if (Pipeline.IsRunning)
                {
                    _reloadTimer.Stop();
                    _reloadTimer.Start(); // debounce while the user is still typing
                }
                break;
            case SettingsSection.Audio:
                if (Pipeline.IsRunning)
                {
                    _ = RestartAsync();
                }
                break;
            case SettingsSection.General:
                ApplyAppearance();
                try
                {
                    if (Settings.General.StartWithWindows != StartupRegistration.IsRegistered || StartupRegistration.IsStale)
                    {
                        StartupRegistration.Set(Settings.General.StartWithWindows);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not update the Windows startup entry");
                }
                break;
        }
        ScheduleSave();
    }

    public void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        _saveTimer.Stop();
        try
        {
            _settingsStore.Save(Settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving settings failed");
        }
    }

    public string? GetApiKey(string provider) => Secrets.Get(SecretStore.ApiKeyName(provider));

    public void SetApiKey(string provider, string? key)
    {
        Secrets.Set(SecretStore.ApiKeyName(provider), key);
        ApplySettings(SettingsSection.Api);
    }

    private async Task RestartAsync()
    {
        await StopListeningAsync();
        await StartListeningAsync();
    }

    private async Task ReloadEngineAsync()
    {
        _reloadTimer.Stop();
        if (!Pipeline.IsRunning)
        {
            return;
        }
        // Language / partial settings live in the pipeline config and need a restart;
        // a model or API change alone swaps the transcriber without interrupting capture.
        if (NeedsRestartForEngineChange())
        {
            await RestartAsync();
            return;
        }
        await _engineGate.WaitAsync();
        try
        {
            await Pipeline.ReloadTranscriberAsync(BuildTranscriberFactory());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Switching transcriber failed");
            _tray?.Notify(Loc.Get("Notify_EngineSwitchFailed"), ex.Message);
        }
        finally
        {
            _engineGate.Release();
        }
        UpdateStatusText();
    }

    private bool NeedsRestartForEngineChange()
    {
        var wanted = (EffectiveLanguage(), PartialsEnabled(), Settings.Engine.Mode, IsStreaming());
        return wanted != _runningEngineParams;
    }

    // ───────────────────────────── Engine construction ─────────────────────────────

    private PipelineConfig BuildPipelineConfig()
    {
        var audio = Settings.Audio;
        string vadPath = SileroVad.DefaultModelPath;
        bool useVad = audio.UseVad && File.Exists(vadPath);
        if (audio.UseVad && !useVad)
        {
            _logger.LogWarning("Silero VAD model missing at {Path}; falling back to fixed windows", vadPath);
        }
        _runningEngineParams = (EffectiveLanguage(), PartialsEnabled(), Settings.Engine.Mode, IsStreaming());
        bool streaming = IsStreaming();
        bool api = Settings.Engine.Mode == EngineMode.Api;
        return new PipelineConfig
        {
            // Developer switch: caption a WAV file in real time instead of system audio (repeatable overlay checks).
            AudioSourceFactory = Environment.GetEnvironmentVariable("CAPTIONOVERLAY_DEBUG_AUDIO_FILE") is { Length: > 0 } debugWav && File.Exists(debugWav)
                ? () => new WavFileAudioSource(debugWav, realtime: true)
                : () => new WasapiLoopbackSource(audio.DeviceId, _loggers.CreateLogger<WasapiLoopbackSource>()),
            VadFactory = useVad ? () => new SileroVad(vadPath) : null,
            Segmenter = new SegmenterOptions
            {
                SpeechThreshold = audio.SpeechThreshold,
                SilenceThreshold = Math.Min(audio.SilenceThreshold, audio.SpeechThreshold),
                EndSilenceMs = audio.EndSilenceMs,
                MaxUtteranceSec = Math.Clamp(audio.MaxUtteranceSec, 3, 30),
                // Streaming engines upload audio with each partial pass: a short interval keeps the upload close to live.
                PartialIntervalMs = streaming ? 250 : Math.Max(200, Settings.Engine.PartialIntervalMs),
            },
            Language = EffectiveLanguage(),
            EnablePartials = PartialsEnabled(),
            Scheduler = new SchedulerOptions { MinPartialInterval = api && !streaming ? TimeSpan.FromSeconds(1.5) : TimeSpan.Zero },
        };
    }

    /// <summary>
    /// Local models share the CPU and GPU with the video being captioned, so the process runs in the background while
    /// one is loaded; the UI thread (caller) keeps a normal app's priority so the overlay stays smooth.
    /// </summary>
    private void ApplyInferencePriority(bool local)
    {
        ProcessPriority.SetBackground(local, _logger);
        Thread.CurrentThread.Priority = local ? ThreadPriority.Highest : ThreadPriority.Normal;
    }

    private string? EffectiveLanguage() => Settings.Engine.Language is "" or "auto" ? null : Settings.Engine.Language;

    /// <summary>Streaming models always run partial passes: they carry the audio upload, and the live text costs nothing extra.</summary>
    private bool PartialsEnabled() => Settings.Engine.Mode == EngineMode.Api ? Settings.Api.EnablePartials || IsStreaming() : Settings.Engine.EnablePartials;

    private bool IsStreaming() => Settings.Engine.Mode == EngineMode.Api && OpenAiRealtimeTranscriber.IsStreamingModel(Settings.Api.Model);

    public TranscriberFactory BuildTranscriberFactory()
    {
        var engine = Settings.Engine;
        ApplyInferencePriority(engine.Mode == EngineMode.Local);
        if (engine.Mode == EngineMode.Api)
        {
            var api = Settings.Api;
            var preset = ApiProviderPreset.Find(api.Provider);
            string? key = GetApiKey(api.Provider);
            var options = new ApiTranscriberOptions
            {
                BaseUrl = string.IsNullOrWhiteSpace(api.BaseUrl) ? preset.BaseUrl : api.BaseUrl,
                Model = string.IsNullOrWhiteSpace(api.Model) ? preset.DefaultModel : api.Model,
                ApiKey = key,
                ProviderName = preset.Name,
                EnablePartials = api.EnablePartials,
                Language = EffectiveLanguage(),
            };
            // Streaming models connect here, so a wrong key or model shows up as a start error.
            return async ct => new TranscriberSet(await ApiTranscribers.CreateAsync(options, _loggers.CreateLogger("ApiTranscriber"), ct));
        }

        var model = ModelStore.Resolve(engine.ModelId, Catalog)
            ?? throw new InvalidOperationException(Loc.Get("Status_NoModel"));
        var partialModel = engine.PartialModelId is { } pid && pid != engine.ModelId ? ModelStore.Resolve(pid, Catalog) : null;
        var gpu = engine.Gpu;
        return async ct =>
        {
            var final = await LocalWhisperTranscriber.LoadAsync(new LocalWhisperOptions
            {
                ModelPath = model.Path,
                DisplayName = model.DisplayName,
                ForcedLanguage = model.ForceLanguage,
                EnablePartials = engine.EnablePartials,
            }, gpu, _loggers.CreateLogger<LocalWhisperTranscriber>(), ct);
            if (partialModel is null)
            {
                return new TranscriberSet(final);
            }
            try
            {
                var partial = await LocalWhisperTranscriber.LoadAsync(new LocalWhisperOptions
                {
                    ModelPath = partialModel.Path,
                    DisplayName = partialModel.DisplayName,
                    ForcedLanguage = model.ForceLanguage ?? partialModel.ForceLanguage,
                    EnablePartials = true,
                }, gpu, _loggers.CreateLogger<LocalWhisperTranscriber>(), ct);
                return new TranscriberSet(final, partial);
            }
            catch
            {
                await final.DisposeAsync();
                throw;
            }
        };
    }

    // ───────────────────────────── Captions / status ─────────────────────────────

    private void RenderCaptions()
    {
        if (!Pipeline.IsPaused)
        {
            OverlayViewModel.Update(Pipeline.Captions.GetSnapshot(Settings.Overlay.LinesShown));
        }
    }

    private void OnLineCommitted(CaptionLine line)
    {
        try
        {
            _transcript?.Append(line);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Writing transcript failed");
        }
        if (Settings.General.LogTranscriptText)
        {
            _logger.LogDebug("Caption {Start}-{End}: {Text}", line.Start, line.End, line.Text);
        }
    }

    private void StartTranscriptSession()
    {
        _transcript?.Dispose();
        _transcript = null;
        var t = Settings.Transcripts;
        if (!t.AutoSave || (!t.Srt && !t.Txt))
        {
            return;
        }
        try
        {
            _transcript = new TranscriptSession(TranscriptsFolder, t.Srt, t.Txt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Cannot create transcript files");
        }
    }

    public string TranscriptsFolder => string.IsNullOrWhiteSpace(Settings.Transcripts.Folder) ? TranscriptSession.DefaultFolder : Settings.Transcripts.Folder;

    private void OnPipelineStatus(PipelineStatus status)
    {
        IsListening = Pipeline.IsRunning;
        IsPaused = Pipeline.IsPaused;
        if (status.State == PipelineState.Error && !Pipeline.IsRunning)
        {
            _transcript?.Dispose();
            _transcript = null;
            _tray?.Notify(Loc.Get("Notify_Stopped"), status.Message ?? Loc.Get("Common_Error"));
        }
        UpdateStatusText();
    }

    private void UpdateStatusText()
    {
        var status = Pipeline.Status;
        var metrics = Pipeline.Metrics;
        string engine = Pipeline.TranscriberName is { } name ? $" · {name}" : "";
        string text = status.State switch
        {
            PipelineState.Idle => Loc.Get("Status_NotListening"),
            PipelineState.LoadingModel => Loc.Get("Pipeline_LoadingModel"),
            PipelineState.Paused => Loc.Get("Pipeline_Paused"),
            PipelineState.Error => Loc.Format("Status_Error", status.Message),
            _ => Loc.Format("Status_Listening", metrics.Runtime ?? status.Message, engine),
        };
        if (status.State is PipelineState.Listening or PipelineState.Transcribing && metrics.IsLagging)
        {
            text = Loc.Format("Status_Lagging", metrics.LagSeconds);
        }
        if (status.State == PipelineState.Idle && _startError)
        {
            return; // keep the start error visible
        }
        StatusText = text;
        OverlayViewModel.StatusText = text;
        _tray?.SetToolTip($"CaptionOverlay — {text}");
    }

    // ───────────────────────────── Language / theme ─────────────────────────────

    /// <summary>Applies the UI language and light/dark theme from the settings (both switch live).</summary>
    private void ApplyAppearance()
    {
        Loc.SetLanguage(Settings.General.UiLanguage);
        ThemeService.Apply(Settings.General.Theme);
    }

    /// <summary>Re-creates the texts built in code; XAML text and the tray menu (rebuilt on open) follow by themselves.</summary>
    private void OnCultureChanged()
    {
        if (HotkeyErrors is not null)
        {
            RegisterHotkeys(notify: false);
        }
        if (StartupEntryWarning is not null)
        {
            StartupEntryWarning = Loc.Get("General_StartupEntryStale");
        }
        UpdateStatusText();
    }

    // ───────────────────────────── Windows / misc ─────────────────────────────

    public void ShowSettings(string? tab = null)
    {
        if (_firstRunWindow is { IsVisible: true })
        {
            _firstRunWindow.Activate();
            return;
        }
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(new SettingsViewModel(this));
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.SelectTab(tab);
        _settingsWindow.Show();
        if (_settingsWindow.WindowState == WindowState.Minimized)
        {
            _settingsWindow.WindowState = WindowState.Normal;
        }
        _settingsWindow.Activate();
    }

    public void ShowFirstRun()
    {
        if (_firstRunWindow is null)
        {
            _firstRunWindow = new FirstRunWindow(new FirstRunViewModel(this));
            _firstRunWindow.Closed += (_, _) => _firstRunWindow = null;
        }
        _firstRunWindow.Show();
        _firstRunWindow.Activate();
    }

    /// <summary>Called by the wizard when done: start listening and let the user place the overlay.</summary>
    public async Task CompleteFirstRunAsync()
    {
        Settings.FirstRunCompleted = true;
        SaveNow();
        await StartListeningAsync();
        SetEditMode(true);
    }

    public void CopyTranscript()
    {
        string text = Pipeline.Captions.GetTranscriptText();
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            _logger.LogWarning(ex, "Clipboard busy");
        }
    }

    public void OpenTranscriptsFolder() => OpenFolder(TranscriptsFolder);

    public static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    public static void OpenUrl(string url)
    {
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
    }

    public void RegisterHotkeys(bool notify = true)
    {
        if (_hotkeys is null)
        {
            return;
        }
        _hotkeys.UnregisterAll();
        var errors = new[]
        {
            _hotkeys.Register(Settings.Hotkeys.ToggleEditMode, ToggleEditMode),
            _hotkeys.Register(Settings.Hotkeys.PauseResume, TogglePause),
            _hotkeys.Register(Settings.Hotkeys.ClearOverlay, ClearOverlay),
        }.Where(e => e is not null).ToList();
        HotkeyErrors = errors.Count == 0 ? null : string.Join(Environment.NewLine, errors);
        if (HotkeyErrors is not null && notify)
        {
            _tray?.Notify(Loc.Get("Notify_HotkeyUnavailable"), HotkeyErrors);
        }
    }

    [ObservableProperty]
    public partial string? HotkeyErrors { get; private set; }

    [ObservableProperty]
    public partial string? StartupEntryWarning { get; private set; }

    private void CheckStartupEntry()
    {
        try
        {
            if (Settings.General.StartWithWindows && StartupRegistration.IsStale)
            {
                StartupEntryWarning = Loc.Get("General_StartupEntryStale");
                _tray?.Notify(Loc.Get("General_StartWithWindows"), StartupEntryWarning);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Startup entry check failed");
        }
    }

    public void FixStartupEntry()
    {
        StartupRegistration.Set(true);
        StartupEntryWarning = null;
    }

    private async Task RefreshCatalogAsync()
    {
        var catalog = await ModelCatalog.LoadAsync(Http, logger: _loggers.CreateLogger("Catalog"));
        if (catalog.Source != Catalog.Source)
        {
            Catalog = catalog;
            CatalogChanged?.Invoke();
        }
    }

    [ObservableProperty]
    public partial UpdateInfo? AvailableUpdate { get; private set; }

    public async Task CheckForUpdatesAsync()
    {
        var update = await UpdateChecker.CheckAsync(Http, _logger, CancellationToken.None);
        AvailableUpdate = update;
        if (update is not null)
        {
            _tray?.Notify(Loc.Get("Notify_UpdateTitle"), Loc.Format("Notify_UpdateText", update.Tag), () => OpenUrl(update.Url));
        }
    }

    public async Task QuitAsync()
    {
        if (_quitting)
        {
            return;
        }
        _quitting = true;
        SaveNow();
        await DisposeAsync();
        Application.Current.Shutdown();
    }

    public async ValueTask DisposeAsync()
    {
        Loc.CultureChanged -= OnCultureChanged;
        _metricsTimer.Stop();
        Downloader.Dispose();
        await Pipeline.DisposeAsync();
        _transcript?.Dispose();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _overlay?.Close();
        _settingsWindow?.Close();
        _firstRunWindow?.Close();
        Http.Dispose();
    }
}
