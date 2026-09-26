using System.Collections.ObjectModel;
using System.Windows;
using CaptionOverlay.App.Settings;
using CaptionOverlay.Core.Diagnostics;
using CaptionOverlay.Core.Models;
using CaptionOverlay.Core.Settings;
using CaptionOverlay.Core.Transcription;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CaptionOverlay.App.FirstRun;

/// <summary>Three steps: language → engine (local model or API) → download / test, then place the overlay.</summary>
public sealed partial class FirstRunViewModel : ObservableObject, IDisposable
{
    private readonly AppController _app;
    private readonly HardwareSummary _hardware;

    public FirstRunViewModel(AppController app)
    {
        _app = app;
        _hardware = HardwareInfo.Query();
        HardwareText = _hardware.Describe();
        Language = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is var ui && SettingsViewModel.Languages.Any(l => l.Value == ui) ? ui : "auto";
        ApiProvider = ApiProviderPreset.Groq.Id;
        _app.Downloader.ProgressChanged += OnProgress;
        UpdateCandidates();
    }

    /// <summary>Raised when the wizard is finished and the window should close.</summary>
    public event Action? Finished;

    public IReadOnlyList<Choice> Languages => SettingsViewModel.Languages;

    public IReadOnlyList<Choice> ApiProviders { get; } =
        [.. ApiProviderPreset.All.Select(p => p == ApiProviderPreset.Custom ? Choice.Localized(p.Id, "Api_CustomProvider") : new Choice(p.Id, p.Name))];

    public ObservableCollection<Choice> CandidateModels { get; } = [];

    public string HardwareText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack), nameof(NextLabel))]
    public partial int Step { get; set; }

    [ObservableProperty]
    public partial string? Language { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseApi))]
    public partial bool UseLocal { get; set; } = true;

    public bool UseApi
    {
        get => !UseLocal;
        set => UseLocal = !value;
    }

    [ObservableProperty]
    public partial string RecommendationText { get; set; } = "";

    [ObservableProperty]
    public partial string? SelectedModelId { get; set; }

    [ObservableProperty]
    public partial string? ApiProvider { get; set; }

    [ObservableProperty]
    public partial string? ApiResult { get; set; }

    [ObservableProperty]
    public partial double DownloadProgress { get; set; }

    [ObservableProperty]
    public partial string DownloadText { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    public partial bool IsReady { get; set; } = true;

    [ObservableProperty]
    public partial string? BenchmarkText { get; set; }

    public bool CanGoBack => Step > 0;

    public string NextLabel => Loc.Get(Step == 2 ? "Wizard_Finish" : "Wizard_Next");

    partial void OnLanguageChanged(string? value) => UpdateCandidates();

    public void SetApiKey(string key) => _app.SetApiKey(ApiProvider ?? "", string.IsNullOrWhiteSpace(key) ? null : key.Trim());

    private void UpdateCandidates()
    {
        var rec = ModelAdvisor.Recommend(_hardware, Language);
        CandidateModels.Clear();
        foreach (var m in _app.Catalog.Models.Where(m => m.IsMultilingual || m.Languages.Contains(Language ?? "")))
        {
            string size = Models.ModelItemViewModel.FormatSize(m.SizeBytes);
            CandidateModels.Add(new Choice(m.Id, m.Id == rec.ModelId ? Loc.Format("Wizard_ModelRecommended", m.DisplayName, size) : Loc.Format("Wizard_Model", m.DisplayName, size)));
        }
        SelectedModelId = rec.ModelId;
        UseLocal = !rec.SuggestApi || _hardware.PhysicalCores >= 4;
        RecommendationText = rec.SuggestApi ? Loc.Format("Wizard_RecommendationApi", rec.Reason) : rec.Reason;
    }

    [RelayCommand]
    private void Back()
    {
        if (Step > 0)
        {
            Step--;
            IsReady = true;
        }
    }

    [RelayCommand(CanExecute = nameof(IsReady))]
    private async Task Next()
    {
        switch (Step)
        {
            case 0:
                Step = 1;
                break;
            case 1:
                ApplyEngineChoice();
                Step = 2;
                if (UseLocal)
                {
                    StartDownload();
                }
                else
                {
                    await TestApiAsync();
                }
                break;
            case 2:
                await FinishAsync();
                break;
        }
    }

    [RelayCommand]
    private async Task Skip()
    {
        _app.Settings.FirstRunCompleted = true;
        _app.SaveNow();
        Finished?.Invoke();
        await Task.CompletedTask;
    }

    private void ApplyEngineChoice()
    {
        var engine = _app.Settings.Engine;
        engine.Language = Language ?? "auto";
        engine.Mode = UseLocal ? EngineMode.Local : EngineMode.Api;
        if (UseLocal)
        {
            engine.ModelId = SelectedModelId;
        }
        else
        {
            var preset = ApiProviderPreset.Find(ApiProvider);
            _app.Settings.Api.Provider = preset.Id;
            _app.Settings.Api.BaseUrl = preset.BaseUrl;
            _app.Settings.Api.Model = preset.DefaultModel;
        }
        _app.SaveNow();
    }

    private void StartDownload()
    {
        var entry = _app.Catalog.Find(SelectedModelId);
        if (entry is null || _app.ModelStore.IsInstalled(entry))
        {
            DownloadProgress = 1;
            DownloadText = Loc.Get("Wizard_ModelReady");
            IsReady = true;
            return;
        }
        IsReady = false;
        DownloadText = Loc.Get("Wizard_StartingDownload");
        _app.Downloader.Enqueue(entry);
    }

    private void OnProgress(DownloadProgress p) => Application.Current.Dispatcher.InvokeAsync(() =>
    {
        if (p.ModelId != SelectedModelId || Step != 2)
        {
            return;
        }
        DownloadProgress = p.Fraction;
        DownloadText = p.State switch
        {
            DownloadState.Downloading => Loc.Format("Wizard_Downloading", p.Fraction, p.BytesDownloaded / 1e6, p.TotalBytes / 1e6, p.BytesPerSecond / 1e6),
            DownloadState.Verifying => Loc.Get("Wizard_Verifying"),
            DownloadState.Completed => Loc.Get("Wizard_ModelReady"),
            DownloadState.Failed => Loc.Format("Wizard_DownloadFailed", p.Error),
            DownloadState.Paused => Loc.Get("Wizard_DownloadPaused"),
            _ => DownloadText,
        };
        IsReady = p.State == DownloadState.Completed;
    });

    [RelayCommand]
    private async Task Benchmark()
    {
        var model = _app.ModelStore.Resolve(SelectedModelId, _app.Catalog);
        if (model is null)
        {
            return;
        }
        BenchmarkText = Loc.Get("Wizard_RunningBenchmark");
        try
        {
            await using var t = await LocalWhisperTranscriber.LoadAsync(new LocalWhisperOptions { ModelPath = model.Path }, _app.Settings.Engine.Gpu);
            var r = await ModelAdvisor.BenchmarkAsync(t);
            BenchmarkText = Loc.Format("Wizard_BenchmarkResult", r.RealTimeFactor, r.Runtime, r.RatingText);
        }
        catch (Exception ex)
        {
            BenchmarkText = Loc.Format("Common_BenchmarkFailed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task TestApiAsync()
    {
        ApiResult = Loc.Get("Api_Testing");
        var preset = ApiProviderPreset.Find(ApiProvider);
        try
        {
            await using var t = new OpenAiCompatibleTranscriber(new ApiTranscriberOptions
            {
                BaseUrl = preset.BaseUrl,
                Model = preset.DefaultModel,
                ApiKey = _app.GetApiKey(preset.Id),
                ProviderName = preset.Name,
            });
            var latency = await t.TestConnectionAsync(CancellationToken.None);
            ApiResult = Loc.Format("Api_Connected", latency.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            ApiResult = Loc.Format("Wizard_ApiFailed", ex.Message);
        }
    }

    private async Task FinishAsync()
    {
        Finished?.Invoke();
        await _app.CompleteFirstRunAsync();
    }

    public void Dispose() => _app.Downloader.ProgressChanged -= OnProgress;
}
