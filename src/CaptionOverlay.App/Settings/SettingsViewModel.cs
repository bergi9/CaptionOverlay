using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;
using CaptionOverlay.App.Infrastructure;
using CaptionOverlay.App.Models;
using CaptionOverlay.Core;
using CaptionOverlay.Core.Audio;
using CaptionOverlay.Core.Settings;
using CaptionOverlay.Core.Transcription;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CaptionOverlay.App.Settings;

public sealed record Choice(string? Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Settings window. Every property is prefixed with its settings section; any change is written back to
/// <see cref="AppSettings"/> immediately and applied live by the controller.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    public static readonly IReadOnlyList<Choice> Languages =
    [
        new("auto", "Auto-detect"), new("en", "English"), new("de", "German"), new("fr", "French"), new("es", "Spanish"),
        new("it", "Italian"), new("nl", "Dutch"), new("pt", "Portuguese"), new("pl", "Polish"), new("cs", "Czech"),
        new("sv", "Swedish"), new("da", "Danish"), new("no", "Norwegian"), new("fi", "Finnish"), new("hu", "Hungarian"),
        new("ro", "Romanian"), new("el", "Greek"), new("tr", "Turkish"), new("ru", "Russian"), new("uk", "Ukrainian"),
        new("ar", "Arabic"), new("hi", "Hindi"), new("ja", "Japanese"), new("ko", "Korean"), new("zh", "Chinese"),
    ];

    private readonly AppController _app;
    private readonly AppSettings _s;
    private bool _loading = true;

    public SettingsViewModel(AppController app)
    {
        _app = app;
        _s = app.Settings;
        Models = new ModelManagerViewModel(app);
        Models.InstalledChanged += RefreshModelChoices;
        _app.PropertyChanged += OnAppChanged;

        FontFamilies = [.. Fonts.SystemFontFamilies.Select(f => f.Source).Order(StringComparer.CurrentCultureIgnoreCase)];
        ApiProviders = [.. ApiProviderPreset.All.Select(p => new Choice(p.Id, p.Name))];
        RefreshDevices();
        RefreshModelChoices();
        Load();
        _loading = false;
    }

    public ModelManagerViewModel Models { get; }

    public IReadOnlyList<Choice> LanguageChoices => Languages;

    public IReadOnlyList<string> FontFamilies { get; }

    public IReadOnlyList<Choice> ApiProviders { get; }

    public IReadOnlyList<Choice> EngineModes { get; } = [new(nameof(Core.Settings.EngineMode.Local), "Local (Whisper on this PC)"), new(nameof(Core.Settings.EngineMode.Api), "API (OpenAI-compatible)")];

    public IReadOnlyList<Choice> GpuChoices { get; } = [new(nameof(GpuPreference.Auto), "Auto (GPU if available)"), new(nameof(GpuPreference.CpuOnly), "CPU only")];

    public ObservableCollection<Choice> Devices { get; } = [];

    public ObservableCollection<Choice> ModelChoices { get; } = [];

    public ObservableCollection<Choice> PartialModelChoices { get; } = [];

    public string StatusText => _app.StatusText;

    public string? HotkeyErrors => _app.HotkeyErrors;

    public string? StartupEntryWarning => _app.StartupEntryWarning;

    public string VersionText => $"CaptionOverlay {UpdateChecker.CurrentVersionText}";

    public string RuntimeText => $"Whisper runtime: {_app.Pipeline.RuntimeDescription ?? "not loaded yet"} · .NET {Environment.Version}";

    public string LogsFolder => AppPaths.LogsDir;

    public string? UpdateText => _app.AvailableUpdate is { } u ? $"Version {u.Tag} is available." : null;

    // ───── General ─────
    [ObservableProperty] public partial bool GeneralStartWithWindows { get; set; }
    [ObservableProperty] public partial bool GeneralStartListeningOnLaunch { get; set; }
    [ObservableProperty] public partial bool GeneralCheckForUpdates { get; set; }
    [ObservableProperty] public partial bool GeneralLogTranscriptText { get; set; }

    // ───── Transcripts (shown on the General tab) ─────
    [ObservableProperty] public partial bool TranscriptsAutoSave { get; set; }
    [ObservableProperty] public partial string TranscriptsFolder { get; set; } = "";
    [ObservableProperty] public partial bool TranscriptsSrt { get; set; }
    [ObservableProperty] public partial bool TranscriptsTxt { get; set; }

    // ───── Audio ─────
    [ObservableProperty] public partial string? AudioDeviceId { get; set; }
    [ObservableProperty] public partial bool AudioUseVad { get; set; }
    [ObservableProperty] public partial double AudioSpeechThreshold { get; set; }
    [ObservableProperty] public partial double AudioSilenceThreshold { get; set; }
    [ObservableProperty] public partial double AudioEndSilenceMs { get; set; }
    [ObservableProperty] public partial double AudioMaxUtteranceSec { get; set; }

    // ───── Engine ─────
    [ObservableProperty] public partial string? EngineMode { get; set; }
    [ObservableProperty] public partial string? EngineModelId { get; set; }
    [ObservableProperty] public partial string? EngineLanguage { get; set; }
    [ObservableProperty] public partial bool EngineEnablePartials { get; set; }
    [ObservableProperty] public partial double EnginePartialIntervalMs { get; set; }
    [ObservableProperty] public partial string? EnginePartialModelId { get; set; }
    [ObservableProperty] public partial string? EngineGpu { get; set; }

    // ───── API ─────
    [ObservableProperty] public partial string? ApiProvider { get; set; }
    [ObservableProperty] public partial string ApiBaseUrl { get; set; } = "";
    [ObservableProperty] public partial string ApiModel { get; set; } = "";
    [ObservableProperty] public partial bool ApiEnablePartials { get; set; }

    [ObservableProperty]
    public partial string? ApiTestResult { get; set; }

    // ───── Overlay ─────
    [ObservableProperty] public partial string OverlayFontFamily { get; set; } = "";
    [ObservableProperty] public partial double OverlayFontSize { get; set; }
    [ObservableProperty] public partial double OverlayFontWeight { get; set; }
    [ObservableProperty] public partial string OverlayTextColor { get; set; } = "";
    [ObservableProperty] public partial string OverlayOutlineColor { get; set; } = "";
    [ObservableProperty] public partial double OverlayOutlineThickness { get; set; }
    [ObservableProperty] public partial string OverlayBackgroundColor { get; set; } = "";
    [ObservableProperty] public partial double OverlayBackgroundOpacity { get; set; }
    [ObservableProperty] public partial double OverlayLinesShown { get; set; }
    [ObservableProperty] public partial double OverlayFadeTimeoutSec { get; set; }

    // ───── Hotkeys ─────
    [ObservableProperty] public partial string HotkeysToggleEditMode { get; set; } = "";
    [ObservableProperty] public partial string HotkeysPauseResume { get; set; } = "";
    [ObservableProperty] public partial string HotkeysClearOverlay { get; set; } = "";

    [ObservableProperty]
    public partial string? DiagnosticsResult { get; set; }

    public bool IsLocalMode => EngineMode == nameof(Core.Settings.EngineMode.Local);

    public bool IsApiMode => !IsLocalMode;

    public bool IsCustomProvider => ApiProvider == ApiProviderPreset.Custom.Id;

    public string? ApiKeyForDisplay => _app.GetApiKey(ApiProvider ?? "") is { Length: > 0 } ? "A key is stored (encrypted for your Windows user)." : "No key stored.";

    private void Load()
    {
        var g = _s.General;
        GeneralStartWithWindows = g.StartWithWindows;
        GeneralStartListeningOnLaunch = g.StartListeningOnLaunch;
        GeneralCheckForUpdates = g.CheckForUpdates;
        GeneralLogTranscriptText = g.LogTranscriptText;

        var t = _s.Transcripts;
        TranscriptsAutoSave = t.AutoSave;
        TranscriptsFolder = t.Folder ?? "";
        TranscriptsSrt = t.Srt;
        TranscriptsTxt = t.Txt;

        var a = _s.Audio;
        AudioDeviceId = a.DeviceId ?? "";
        AudioUseVad = a.UseVad;
        AudioSpeechThreshold = a.SpeechThreshold;
        AudioSilenceThreshold = a.SilenceThreshold;
        AudioEndSilenceMs = a.EndSilenceMs;
        AudioMaxUtteranceSec = a.MaxUtteranceSec;

        var e = _s.Engine;
        EngineMode = e.Mode.ToString();
        EngineModelId = e.ModelId;
        EngineLanguage = e.Language;
        EngineEnablePartials = e.EnablePartials;
        EnginePartialIntervalMs = e.PartialIntervalMs;
        EnginePartialModelId = e.PartialModelId ?? "";
        EngineGpu = e.Gpu.ToString();

        var api = _s.Api;
        ApiProvider = api.Provider;
        ApiBaseUrl = api.BaseUrl;
        ApiModel = api.Model;
        ApiEnablePartials = api.EnablePartials;

        var o = _s.Overlay;
        OverlayFontFamily = o.FontFamily;
        OverlayFontSize = o.FontSize;
        OverlayFontWeight = o.FontWeight;
        OverlayTextColor = o.TextColor;
        OverlayOutlineColor = o.OutlineColor;
        OverlayOutlineThickness = o.OutlineThickness;
        OverlayBackgroundColor = o.BackgroundColor;
        OverlayBackgroundOpacity = o.BackgroundOpacity;
        OverlayLinesShown = o.LinesShown;
        OverlayFadeTimeoutSec = o.FadeTimeoutSec;

        var h = _s.Hotkeys;
        HotkeysToggleEditMode = h.ToggleEditMode;
        HotkeysPauseResume = h.PauseResume;
        HotkeysClearOverlay = h.ClearOverlay;
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is null)
        {
            return;
        }
        if (e.PropertyName == nameof(ApiProvider))
        {
            OnProviderChanged();
        }
        if (e.PropertyName is nameof(EngineMode))
        {
            OnPropertyChanged(nameof(IsLocalMode));
            OnPropertyChanged(nameof(IsApiMode));
        }

        SettingsSection? section = e.PropertyName switch
        {
            var n when n.StartsWith("General", StringComparison.Ordinal) => SettingsSection.General,
            var n when n.StartsWith("Transcripts", StringComparison.Ordinal) => SettingsSection.Transcripts,
            var n when n.StartsWith("Audio", StringComparison.Ordinal) => SettingsSection.Audio,
            var n when n.StartsWith("Engine", StringComparison.Ordinal) => SettingsSection.Engine,
            var n when n.StartsWith("Api", StringComparison.Ordinal) && n != nameof(ApiTestResult) && n != nameof(ApiKeyForDisplay) => SettingsSection.Api,
            var n when n.StartsWith("Overlay", StringComparison.Ordinal) => SettingsSection.Overlay,
            var n when n.StartsWith("Hotkeys", StringComparison.Ordinal) => SettingsSection.Hotkeys,
            _ => null,
        };
        if (section is { } s)
        {
            WriteBack();
            _app.ApplySettings(s);
        }
    }

    private void WriteBack()
    {
        var g = _s.General;
        g.StartWithWindows = GeneralStartWithWindows;
        g.StartListeningOnLaunch = GeneralStartListeningOnLaunch;
        g.CheckForUpdates = GeneralCheckForUpdates;
        g.LogTranscriptText = GeneralLogTranscriptText;

        var t = _s.Transcripts;
        t.AutoSave = TranscriptsAutoSave;
        t.Folder = string.IsNullOrWhiteSpace(TranscriptsFolder) ? null : TranscriptsFolder.Trim();
        t.Srt = TranscriptsSrt;
        t.Txt = TranscriptsTxt;

        var a = _s.Audio;
        a.DeviceId = string.IsNullOrEmpty(AudioDeviceId) ? null : AudioDeviceId;
        a.UseVad = AudioUseVad;
        a.SpeechThreshold = (float)Math.Round(AudioSpeechThreshold, 2);
        a.SilenceThreshold = (float)Math.Round(Math.Min(AudioSilenceThreshold, AudioSpeechThreshold), 2);
        a.EndSilenceMs = (int)AudioEndSilenceMs;
        a.MaxUtteranceSec = Math.Round(AudioMaxUtteranceSec, 1);

        var e = _s.Engine;
        e.Mode = Enum.TryParse<Core.Settings.EngineMode>(EngineMode, out var mode) ? mode : Core.Settings.EngineMode.Local;
        e.ModelId = EngineModelId;
        e.Language = EngineLanguage ?? "auto";
        e.EnablePartials = EngineEnablePartials;
        e.PartialIntervalMs = (int)EnginePartialIntervalMs;
        e.PartialModelId = string.IsNullOrEmpty(EnginePartialModelId) ? null : EnginePartialModelId;
        e.Gpu = Enum.TryParse<GpuPreference>(EngineGpu, out var gpu) ? gpu : GpuPreference.Auto;

        var api = _s.Api;
        api.Provider = ApiProvider ?? ApiProviderPreset.Groq.Id;
        api.BaseUrl = ApiBaseUrl.Trim();
        api.Model = ApiModel.Trim();
        api.EnablePartials = ApiEnablePartials;

        var o = _s.Overlay;
        o.FontFamily = string.IsNullOrWhiteSpace(OverlayFontFamily) ? "Segoe UI" : OverlayFontFamily;
        o.FontSize = Math.Clamp(OverlayFontSize, 8, 120);
        o.FontWeight = (int)OverlayFontWeight;
        o.TextColor = OverlayTextColor;
        o.OutlineColor = OverlayOutlineColor;
        o.OutlineThickness = OverlayOutlineThickness;
        o.BackgroundColor = OverlayBackgroundColor;
        o.BackgroundOpacity = OverlayBackgroundOpacity;
        o.LinesShown = (int)OverlayLinesShown;
        o.FadeTimeoutSec = OverlayFadeTimeoutSec;

        var h = _s.Hotkeys;
        h.ToggleEditMode = HotkeysToggleEditMode.Trim();
        h.PauseResume = HotkeysPauseResume.Trim();
        h.ClearOverlay = HotkeysClearOverlay.Trim();
    }

    private void OnProviderChanged()
    {
        var preset = ApiProviderPreset.Find(ApiProvider);
        if (preset != ApiProviderPreset.Custom)
        {
            ApiBaseUrl = preset.BaseUrl;
            ApiModel = preset.DefaultModel;
        }
        OnPropertyChanged(nameof(IsCustomProvider));
        OnPropertyChanged(nameof(ApiKeyForDisplay));
        ApiTestResult = null;
    }

    public void SetApiKey(string key)
    {
        _app.SetApiKey(ApiProvider ?? "", string.IsNullOrWhiteSpace(key) ? null : key.Trim());
        OnPropertyChanged(nameof(ApiKeyForDisplay));
    }

    [RelayCommand]
    private async Task TestConnection()
    {
        ApiTestResult = "Testing…";
        var preset = ApiProviderPreset.Find(ApiProvider);
        try
        {
            await using var t = new OpenAiCompatibleTranscriber(new ApiTranscriberOptions
            {
                BaseUrl = string.IsNullOrWhiteSpace(ApiBaseUrl) ? preset.BaseUrl : ApiBaseUrl,
                Model = string.IsNullOrWhiteSpace(ApiModel) ? preset.DefaultModel : ApiModel,
                ApiKey = _app.GetApiKey(ApiProvider ?? ""),
                ProviderName = preset.Name,
            });
            var latency = await t.TestConnectionAsync(CancellationToken.None);
            ApiTestResult = $"✔ Connected — round trip {latency.TotalMilliseconds:F0} ms";
        }
        catch (Exception ex)
        {
            ApiTestResult = $"✖ {ex.Message}";
        }
    }

    [RelayCommand]
    private void RefreshDevices()
    {
        string? current = AudioDeviceId;
        Devices.Clear();
        Devices.Add(new Choice("", "Follow system default output"));
        try
        {
            foreach (var d in WasapiLoopbackSource.GetOutputDevices())
            {
                Devices.Add(new Choice(d.Id, d.Name + (d.IsDefault ? " (current default)" : "")));
            }
        }
        catch (Exception)
        {
            // Audio service unavailable: keep only the default entry.
        }
        bool wasLoading = _loading;
        _loading = true;
        AudioDeviceId = current;
        _loading = wasLoading;
    }

    private void RefreshModelChoices()
    {
        var installed = _app.ModelStore.GetInstalled(_app.Catalog);
        bool wasLoading = _loading;
        _loading = true;
        string? model = EngineModelId;
        string? partial = EnginePartialModelId;
        ModelChoices.Clear();
        PartialModelChoices.Clear();
        PartialModelChoices.Add(new Choice("", "Same as main model"));
        foreach (var m in installed)
        {
            ModelChoices.Add(new Choice(m.Id, m.DisplayName));
            PartialModelChoices.Add(new Choice(m.Id, m.DisplayName));
        }
        EngineModelId = _s.Engine.ModelId ?? model;
        EngineMode = _s.Engine.Mode.ToString();
        OnPropertyChanged(nameof(IsLocalMode));
        OnPropertyChanged(nameof(IsApiMode));
        EnginePartialModelId = partial;
        _loading = wasLoading;
    }

    [RelayCommand]
    private void EditOverlayPosition() => _app.SetEditMode(true);

    [RelayCommand]
    private void OpenLogsFolder() => AppController.OpenFolder(LogsFolder);

    [RelayCommand]
    private void OpenTranscriptsFolder() => _app.OpenTranscriptsFolder();

    [RelayCommand]
    private void ResetOverlayStyle()
    {
        var d = new OverlaySettings();
        OverlayFontFamily = d.FontFamily;
        OverlayFontSize = d.FontSize;
        OverlayFontWeight = d.FontWeight;
        OverlayTextColor = d.TextColor;
        OverlayOutlineColor = d.OutlineColor;
        OverlayOutlineThickness = d.OutlineThickness;
        OverlayBackgroundColor = d.BackgroundColor;
        OverlayBackgroundOpacity = d.BackgroundOpacity;
        OverlayLinesShown = d.LinesShown;
        OverlayFadeTimeoutSec = d.FadeTimeoutSec;
    }

    [RelayCommand]
    private void FixStartupEntry()
    {
        _app.FixStartupEntry();
        OnPropertyChanged(nameof(StartupEntryWarning));
    }

    [RelayCommand]
    private void ExportDiagnostics()
    {
        try
        {
            string folder = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string zip = DiagnosticsExporter.Export(_s, _app.Pipeline.RuntimeDescription, folder);
            DiagnosticsResult = $"Saved to {zip}";
        }
        catch (Exception ex)
        {
            DiagnosticsResult = $"Export failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CheckUpdates()
    {
        await _app.CheckForUpdatesAsync();
        OnPropertyChanged(nameof(UpdateText));
        if (_app.AvailableUpdate is null)
        {
            DiagnosticsResult = "You are running the latest version (or GitHub could not be reached).";
        }
    }

    [RelayCommand]
    private void OpenReleasePage() => AppController.OpenUrl(_app.AvailableUpdate?.Url ?? $"https://github.com/{UpdateChecker.Repository}/releases");

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppController.StatusText):
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(RuntimeText));
                break;
            case nameof(AppController.HotkeyErrors):
                OnPropertyChanged(nameof(HotkeyErrors));
                break;
            case nameof(AppController.StartupEntryWarning):
                OnPropertyChanged(nameof(StartupEntryWarning));
                break;
            case nameof(AppController.AvailableUpdate):
                OnPropertyChanged(nameof(UpdateText));
                break;
        }
    }

    public static string FormatMs(double ms) => ms.ToString("F0", CultureInfo.InvariantCulture) + " ms";

    public void Dispose()
    {
        _app.PropertyChanged -= OnAppChanged;
        Models.InstalledChanged -= RefreshModelChoices;
        Models.Dispose();
    }
}
