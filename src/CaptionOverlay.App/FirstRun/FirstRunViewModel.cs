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
        HardwareText = _hardware.ToString();
        Language = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is var ui && SettingsViewModel.Languages.Any(l => l.Value == ui) ? ui : "auto";
        ApiProvider = ApiProviderPreset.Groq.Id;
        _app.Downloader.ProgressChanged += OnProgress;
        UpdateCandidates();
    }

    /// <summary>Raised when the wizard is finished and the window should close.</summary>
    public event Action? Finished;

    public IReadOnlyList<Choice> Languages => SettingsViewModel.Languages;

    public IReadOnlyList<Choice> ApiProviders { get; } = [.. ApiProviderPreset.All.Select(p => new Choice(p.Id, p.Name))];

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

    public string NextLabel => Step == 2 ? "Finish" : "Next";

    partial void OnLanguageChanged(string? value) => UpdateCandidates();

    public void SetApiKey(string key) => _app.SetApiKey(ApiProvider ?? "", string.IsNullOrWhiteSpace(key) ? null : key.Trim());

    private void UpdateCandidates()
    {
        var rec = ModelAdvisor.Recommend(_hardware, Language);
        CandidateModels.Clear();
        foreach (var m in _app.Catalog.Models.Where(m => m.IsMultilingual || m.Languages.Contains(Language ?? "")))
        {
            string size = m.SizeBytes >= 1_000_000_000 ? $"{m.SizeBytes / 1e9:F1} GB" : $"{m.SizeBytes / 1e6:F0} MB";
            CandidateModels.Add(new Choice(m.Id, $"{m.DisplayName} — {size}{(m.Id == rec.ModelId ? "  (recommended)" : "")}"));
        }
        SelectedModelId = rec.ModelId;
        UseLocal = !rec.SuggestApi || _hardware.PhysicalCores >= 4;
        RecommendationText = rec.Reason + (rec.SuggestApi ? " API mode is a good alternative on this PC." : "");
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
            DownloadText = "Model is ready.";
            IsReady = true;
            return;
        }
        IsReady = false;
        DownloadText = "Starting download…";
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
            DownloadState.Downloading => $"Downloading… {p.Fraction:P0} ({p.BytesDownloaded / 1e6:F0} of {p.TotalBytes / 1e6:F0} MB, {p.BytesPerSecond / 1e6:F1} MB/s)",
            DownloadState.Verifying => "Verifying download…",
            DownloadState.Completed => "Model is ready.",
            DownloadState.Failed => $"Download failed: {p.Error}. Go back and try again.",
            DownloadState.Paused => "Download paused.",
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
        BenchmarkText = "Running benchmark…";
        try
        {
            await using var t = await LocalWhisperTranscriber.LoadAsync(new LocalWhisperOptions { ModelPath = model.Path }, _app.Settings.Engine.Gpu);
            var r = await ModelAdvisor.BenchmarkAsync(t);
            BenchmarkText = $"Real-time factor {r.RealTimeFactor:F2} on {r.Runtime}: {r.RatingText}.";
        }
        catch (Exception ex)
        {
            BenchmarkText = $"Benchmark failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task TestApiAsync()
    {
        ApiResult = "Testing connection…";
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
            ApiResult = $"✔ Connected ({latency.TotalMilliseconds:F0} ms).";
        }
        catch (Exception ex)
        {
            ApiResult = $"✖ {ex.Message} You can finish anyway and fix this later in Settings → API.";
        }
    }

    private async Task FinishAsync()
    {
        Finished?.Invoke();
        await _app.CompleteFirstRunAsync();
    }

    public void Dispose() => _app.Downloader.ProgressChanged -= OnProgress;
}
