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

/// <summary>
/// ComboBox entry (bind with <c>DisplayMemberPath="Label"</c>). The label is a function, so <see cref="Refresh"/> can
/// re-translate it in place after a UI language change without touching the selection.
/// </summary>
public sealed class Choice : ObservableObject
{
    private readonly Func<string> _label;

    public Choice(string? value, string label)
        : this(value, () => label)
    {
    }

    public Choice(string? value, Func<string> label)
    {
        Value = value;
        _label = label;
    }

    public string? Value { get; }

    public string Label => _label();

    public static Choice Localized(string? value, string key) => new(value, () => Loc.Get(key));

    public void Refresh() => OnPropertyChanged(nameof(Label));

    public override string ToString() => Label;
}

/// <summary>
/// Settings window. Every property is prefixed with its settings section; any change is written back to
/// <see cref="AppSettings"/> immediately and applied live by the controller.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    /// <summary>Spoken languages for Whisper (labels in the UI language).</summary>
    public static readonly IReadOnlyList<Choice> Languages =
    [
        .. new[]
        {
            "auto", "en", "de", "fr", "es", "it", "nl", "pt", "pl", "cs", "sv", "da", "no", "fi", "hu", "ro", "el", "tr", "ru", "uk",
            "ar", "hi", "ja", "ko", "zh",
        }.Select(code => Choice.Localized(code, "Lang_" + code)),
    ];

    private readonly AppController _app;
    private readonly AppSettings _s;
    private bool _loading = true;
    private Choice? _selectedApiModel;
    private CancellationTokenSource? _modelsCts;

    public SettingsViewModel(AppController app)
    {
        _app = app;
        _s = app.Settings;
        Models = new ModelManagerViewModel(app);
        Models.InstalledChanged += RefreshModelChoices;
        _app.PropertyChanged += OnAppChanged;
        Loc.CultureChanged += OnCultureChanged;

        FontFamilies = [.. Fonts.SystemFontFamilies.Select(f => f.Source).Order(StringComparer.CurrentCultureIgnoreCase)];
        ApiProviders = [.. ApiProviderPreset.All.Select(p => p == ApiProviderPreset.Custom ? Choice.Localized(p.Id, "Api_CustomProvider") : new Choice(p.Id, p.Name))];
        RefreshDevices();
        RefreshModelChoices();
        Load();
        _loading = false;
        _ = LoadApiModelsAsync();
    }

    public ModelManagerViewModel Models { get; }

    public IReadOnlyList<Choice> LanguageChoices => Languages;

    /// <summary>UI languages: "system" plus each translation, named in its own language.</summary>
    public IReadOnlyList<Choice> UiLanguageChoices { get; } =
    [
        new(Loc.SystemLanguage, () => Loc.Format("General_UiLanguageSystem", NativeName(Loc.Resolve(Loc.SystemLanguage, Loc.SystemUICulture)))),
        .. Loc.SupportedLanguages.Select(code => new Choice(code, NativeName(CultureInfo.GetCultureInfo(code)))),
    ];

    public IReadOnlyList<Choice> ThemeChoices { get; } =
    [
        Choice.Localized(nameof(AppTheme.System), "General_ThemeSystem"),
        Choice.Localized(nameof(AppTheme.Light), "General_ThemeLight"),
        Choice.Localized(nameof(AppTheme.Dark), "General_ThemeDark"),
    ];

    public IReadOnlyList<Choice> TranscriptSplitChoices { get; } =
    [
        Choice.Localized(nameof(TranscriptSplit.AfterBreak), "General_SplitAfterBreak"),
        Choice.Localized(nameof(TranscriptSplit.EachStart), "General_SplitEachStart"),
        Choice.Localized(nameof(TranscriptSplit.Manual), "General_SplitManual"),
    ];

    public IReadOnlyList<string> FontFamilies { get; }

    public IReadOnlyList<Choice> ApiProviders { get; }

    /// <summary>Models offered by the API provider that work for captions (loaded with the saved key).</summary>
    public ObservableCollection<Choice> ApiModelChoices { get; } = [];

    /// <summary>Loading / error state of <see cref="ApiModelChoices"/>, null when the list is fine.</summary>
    [ObservableProperty]
    public partial string? ModelListStatus { get; set; }

    /// <summary>
    /// The selected entry of <see cref="ApiModelChoices"/>. Nulls pushed by the ComboBox while the list is rebuilt are
    /// ignored, so the saved model is never cleared by a refresh. Picking a model tests it right away.
    /// </summary>
    public Choice? SelectedApiModel
    {
        get => _selectedApiModel;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedApiModel))
            {
                return;
            }
            _selectedApiModel = value;
            OnPropertyChanged();
            if (value.Value is { } id && id != ApiModel)
            {
                ApiModel = id;
                if (!_loading)
                {
                    _ = TestConnection();
                }
            }
        }
    }

    public IReadOnlyList<Choice> EngineModes { get; } =
    [
        Choice.Localized(nameof(Core.Settings.EngineMode.Local), "Engine_ModeLocal"),
        Choice.Localized(nameof(Core.Settings.EngineMode.Api), "Engine_ModeApi"),
    ];

    public IReadOnlyList<Choice> GpuChoices { get; } =
    [
        Choice.Localized(nameof(GpuPreference.Auto), "Engine_GpuAuto"),
        Choice.Localized(nameof(GpuPreference.CpuOnly), "Engine_GpuCpuOnly"),
    ];

    public ObservableCollection<Choice> Devices { get; } = [];

    public ObservableCollection<Choice> ModelChoices { get; } = [];

    public ObservableCollection<Choice> PartialModelChoices { get; } = [];

    public string StatusText => _app.StatusText;

    public string? HotkeyErrors => _app.HotkeyErrors;

    public string? StartupEntryWarning => _app.StartupEntryWarning;

    public string VersionText => $"CaptionOverlay {UpdateChecker.CurrentVersionText}";

    public string RuntimeText => Loc.Format("About_Runtime", _app.Pipeline.RuntimeDescription ?? Loc.Get("About_RuntimeNotLoaded"), Environment.Version);

    public string LogsFolder => AppPaths.LogsDir;

    public string? UpdateText => _app.AvailableUpdate is { } u ? Loc.Format("About_UpdateAvailable", u.Tag) : null;

    // ───── General ─────
    [ObservableProperty] public partial string? GeneralUiLanguage { get; set; }
    [ObservableProperty] public partial string? GeneralTheme { get; set; }
    [ObservableProperty] public partial bool GeneralStartWithWindows { get; set; }
    [ObservableProperty] public partial bool GeneralStartListeningOnLaunch { get; set; }
    [ObservableProperty] public partial bool GeneralCheckForUpdates { get; set; }
    [ObservableProperty] public partial bool GeneralLogTranscriptText { get; set; }

    // ───── Transcripts (shown on the General tab) ─────
    [ObservableProperty] public partial bool TranscriptsAutoSave { get; set; }
    [ObservableProperty] public partial string TranscriptsFolder { get; set; } = "";
    [ObservableProperty] public partial bool TranscriptsSrt { get; set; }
    [ObservableProperty] public partial bool TranscriptsTxt { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSplitAfterBreak))]
    public partial string? TranscriptsSplit { get; set; }

    [ObservableProperty] public partial double TranscriptsSplitAfterMinutes { get; set; }

    public bool IsSplitAfterBreak => TranscriptsSplit == nameof(TranscriptSplit.AfterBreak);

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
    [ObservableProperty] public partial bool OverlayAnimateLines { get; set; }
    [ObservableProperty] public partial bool OverlayShowSoundTags { get; set; }

    // ───── Hotkeys ─────
    [ObservableProperty] public partial string HotkeysToggleEditMode { get; set; } = "";
    [ObservableProperty] public partial string HotkeysPauseResume { get; set; } = "";
    [ObservableProperty] public partial string HotkeysClearOverlay { get; set; } = "";

    [ObservableProperty]
    public partial string? DiagnosticsResult { get; set; }

    public bool IsLocalMode => EngineMode == nameof(Core.Settings.EngineMode.Local);

    public bool IsApiMode => !IsLocalMode;

    /// <summary>The model is typed (Custom); the other providers offer their <c>/models</c> list.</summary>
    public bool IsCustomProvider => !ApiProviderPreset.Find(ApiProvider).ListsModels;

    public bool IsPresetProvider => !IsCustomProvider;

    /// <summary>Speaches and Custom run on the user's server, so the URL field is explained.</summary>
    public bool IsSelfHostedProvider => ApiProviderPreset.Find(ApiProvider).SelfHosted;

    /// <summary>Streaming models always show live text (no extra cost per update), so the partials option does not apply.</summary>
    public bool IsStreamingModel => OpenAiRealtimeTranscriber.IsStreamingModel(ApiModel);

    public bool IsUploadModel => !IsStreamingModel;

    public string? ApiKeyForDisplay => Loc.Get(_app.GetApiKey(ApiProvider ?? "") is { Length: > 0 } ? "Api_KeyStored" : "Api_NoKey");

    private static string NativeName(CultureInfo culture)
    {
        var neutral = culture.IsNeutralCulture ? culture : culture.Parent;
        string name = neutral.NativeName;
        return name.Length > 0 ? char.ToUpper(name[0], neutral) + name[1..] : neutral.Name;
    }

    private void Load()
    {
        var g = _s.General;
        GeneralUiLanguage = Loc.SupportedLanguages.Contains(g.UiLanguage) ? g.UiLanguage : Loc.SystemLanguage;
        GeneralTheme = g.Theme.ToString();
        GeneralStartWithWindows = g.StartWithWindows;
        GeneralStartListeningOnLaunch = g.StartListeningOnLaunch;
        GeneralCheckForUpdates = g.CheckForUpdates;
        GeneralLogTranscriptText = g.LogTranscriptText;

        var t = _s.Transcripts;
        TranscriptsAutoSave = t.AutoSave;
        TranscriptsFolder = t.Folder ?? "";
        TranscriptsSrt = t.Srt;
        TranscriptsTxt = t.Txt;
        TranscriptsSplit = t.Split.ToString();
        TranscriptsSplitAfterMinutes = t.SplitAfterMinutes;

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
        OverlayAnimateLines = o.AnimateLines;
        OverlayShowSoundTags = o.ShowSoundTags;

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
        else if (e.PropertyName == nameof(ApiBaseUrl) && IsPresetProvider)
        {
            _ = LoadApiModelsAsync(); // another server has other models
        }
        if (e.PropertyName is nameof(ApiModel))
        {
            OnPropertyChanged(nameof(IsStreamingModel));
            OnPropertyChanged(nameof(IsUploadModel));
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
        g.UiLanguage = GeneralUiLanguage ?? Loc.SystemLanguage;
        g.Theme = Enum.TryParse<AppTheme>(GeneralTheme, out var theme) ? theme : AppTheme.System;
        g.StartWithWindows = GeneralStartWithWindows;
        g.StartListeningOnLaunch = GeneralStartListeningOnLaunch;
        g.CheckForUpdates = GeneralCheckForUpdates;
        g.LogTranscriptText = GeneralLogTranscriptText;

        var t = _s.Transcripts;
        t.AutoSave = TranscriptsAutoSave;
        t.Folder = string.IsNullOrWhiteSpace(TranscriptsFolder) ? null : TranscriptsFolder.Trim();
        t.Srt = TranscriptsSrt;
        t.Txt = TranscriptsTxt;
        t.Split = Enum.TryParse<TranscriptSplit>(TranscriptsSplit, out var split) ? split : TranscriptSplit.AfterBreak;
        t.SplitAfterMinutes = (int)Math.Round(TranscriptsSplitAfterMinutes);

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
        api.Model = (ApiModel ?? "").Trim();
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
        o.AnimateLines = OverlayAnimateLines;
        o.ShowSoundTags = OverlayShowSoundTags;

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
        OnPropertyChanged(nameof(IsPresetProvider));
        OnPropertyChanged(nameof(IsSelfHostedProvider));
        OnPropertyChanged(nameof(ApiKeyForDisplay));
        ApiTestResult = null;
        _ = LoadApiModelsAsync();
    }

    public void SetApiKey(string key)
    {
        _app.SetApiKey(ApiProvider ?? "", string.IsNullOrWhiteSpace(key) ? null : key.Trim());
        OnPropertyChanged(nameof(ApiKeyForDisplay));
        _ = LoadApiModelsAsync();
    }

    [RelayCommand]
    private Task RefreshApiModels() => LoadApiModelsAsync();

    /// <summary>
    /// Loads the provider's model list with the saved key and keeps only models usable for captions
    /// (<see cref="ApiTranscribers.IsUsableModel(ApiModelInfo)"/>). Without a key or connection the list holds the
    /// saved model and the provider default, so there is always a valid choice. A self-hosted server may run without a key.
    /// </summary>
    private async Task LoadApiModelsAsync()
    {
        _modelsCts?.Cancel();
        var cts = _modelsCts = new CancellationTokenSource();
        var preset = ApiProviderPreset.Find(ApiProvider);
        string? key = _app.GetApiKey(preset.Id);
        IReadOnlyList<string>? models = null;
        if (!preset.ListsModels)
        {
            ModelListStatus = null; // free text: self-hosted servers often have no model list
        }
        else if (string.IsNullOrEmpty(key) && !preset.SelfHosted)
        {
            ModelListStatus = Loc.Get("Api_ModelsNeedKey");
        }
        else
        {
            ModelListStatus = Loc.Get("Api_ModelsLoading");
            try
            {
                await using var t = new OpenAiCompatibleTranscriber(new ApiTranscriberOptions
                {
                    BaseUrl = string.IsNullOrWhiteSpace(ApiBaseUrl) ? preset.BaseUrl : ApiBaseUrl,
                    Model = preset.DefaultModel,
                    ApiKey = key,
                    ProviderName = preset.Name,
                });
                var all = await t.ListModelsAsync(cts.Token);
                models = [.. all.Where(ApiTranscribers.IsUsableModel).Select(m => m.Id)];
                ModelListStatus = models.Count == 0 ? Loc.Get("Api_ModelsNone") : null;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                return; // superseded by a newer load
            }
            catch (TranscriptionException ex)
            {
                ModelListStatus = Loc.Format("Api_ModelsFailed", ex.Message);
            }
        }
        if (!cts.IsCancellationRequested)
        {
            SetModelChoices(preset, models is { Count: > 0 } ? models : null);
        }
    }

    private void SetModelChoices(ApiProviderPreset preset, IReadOnlyList<string>? available)
    {
        string current = ApiModel ?? "";
        bool wasLoading = _loading;
        _loading = true;
        ApiModelChoices.Clear();
        var ids = available ?? [.. new[] { current, preset.DefaultModel }.Where(id => id.Length > 0).Distinct(StringComparer.Ordinal)];
        if (available is not null && current.Length > 0 && !available.Contains(current))
        {
            // Keep the saved model visible (and selected) but say why it fails, instead of silently switching.
            ApiModelChoices.Add(new Choice(current, () => Loc.Format("Api_ModelNotUsable", current)));
        }
        foreach (string id in ids)
        {
            ApiModelChoices.Add(id == preset.DefaultModel ? new Choice(id, () => Loc.Format("Api_ModelDefault", id))
                : OpenAiRealtimeTranscriber.IsStreamingModel(id) ? new Choice(id, () => Loc.Format("Api_ModelStreaming", id))
                : new Choice(id, id));
        }
        _selectedApiModel = ApiModelChoices.FirstOrDefault(c => c.Value == current);
        OnPropertyChanged(nameof(SelectedApiModel));
        _loading = wasLoading;
    }

    [RelayCommand]
    private async Task TestConnection()
    {
        ApiTestResult = Loc.Get("Api_Testing");
        var preset = ApiProviderPreset.Find(ApiProvider);
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // Streaming models open their session in CreateAsync; that handshake is the test.
            await using var t = await ApiTranscribers.CreateAsync(new ApiTranscriberOptions
            {
                BaseUrl = string.IsNullOrWhiteSpace(ApiBaseUrl) ? preset.BaseUrl : ApiBaseUrl,
                Model = string.IsNullOrWhiteSpace(ApiModel) ? preset.DefaultModel : ApiModel,
                ApiKey = _app.GetApiKey(ApiProvider ?? ""),
                ProviderName = preset.Name,
            });
            var latency = t is OpenAiRealtimeTranscriber ? sw.Elapsed : await t.TestConnectionAsync(CancellationToken.None);
            ApiTestResult = Loc.Format("Api_Connected", latency.TotalMilliseconds);
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
        Devices.Add(Choice.Localized("", "Audio_FollowDefault"));
        try
        {
            foreach (var d in WasapiLoopbackSource.GetOutputDevices())
            {
                Devices.Add(new Choice(d.Id, () => d.IsDefault ? Loc.Format("Audio_CurrentDefault", d.Name) : d.Name));
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
        PartialModelChoices.Add(Choice.Localized("", "Engine_PartialSameModel"));
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
        OverlayAnimateLines = d.AnimateLines;
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
            DiagnosticsResult = Loc.Format("About_DiagnosticsSaved", zip);
        }
        catch (Exception ex)
        {
            DiagnosticsResult = Loc.Format("About_DiagnosticsFailed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task CheckUpdates()
    {
        await _app.CheckForUpdatesAsync();
        OnPropertyChanged(nameof(UpdateText));
        if (_app.AvailableUpdate is null)
        {
            DiagnosticsResult = Loc.Get("About_UpToDate");
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

    /// <summary>
    /// UI language changed: XAML text follows by itself ({l:Loc}); re-translate choice labels in place and re-read
    /// every computed text. Deferred, because the change arrives from inside the language ComboBox's selection update.
    /// </summary>
    private void OnCultureChanged() => System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
    {
        foreach (var choice in Languages.Concat(UiLanguageChoices).Concat(ThemeChoices).Concat(TranscriptSplitChoices).Concat(ApiProviders).Concat(EngineModes)
                     .Concat(GpuChoices).Concat(Devices).Concat(PartialModelChoices).Concat(ApiModelChoices))
        {
            choice.Refresh();
        }
        bool wasLoading = _loading;
        _loading = true;
        OnPropertyChanged(string.Empty);
        _loading = wasLoading;
        Models.Rebuild();
    });

    public void Dispose()
    {
        Loc.CultureChanged -= OnCultureChanged;
        _modelsCts?.Cancel();
        _app.PropertyChanged -= OnAppChanged;
        Models.InstalledChanged -= RefreshModelChoices;
        Models.Dispose();
    }
}
