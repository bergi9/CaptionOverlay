using System.Collections.ObjectModel;
using System.Windows;
using CaptionOverlay.Core.Diagnostics;
using CaptionOverlay.Core.Models;
using CaptionOverlay.Core.Settings;
using CaptionOverlay.Core.Transcription;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace CaptionOverlay.App.Models;

public sealed partial class ModelItemViewModel : ObservableObject
{
    private readonly ModelManagerViewModel _owner;

    public ModelItemViewModel(ModelManagerViewModel owner, ModelCatalogEntry? entry, CustomModel? custom)
    {
        _owner = owner;
        Entry = entry;
        Custom = custom;
    }

    public ModelCatalogEntry? Entry { get; }

    public CustomModel? Custom { get; }

    public string Id => Entry?.Id ?? Custom!.Id;

    public string Name => Entry?.DisplayName ?? Custom!.DisplayName;

    public string Details => Entry is { } e
        ? $"{FormatSize(e.SizeBytes)} · {(e.IsMultilingual ? "multilingual" : string.Join(", ", e.Languages))} · license: {e.License ?? "unknown"}"
        : $"Custom file · {Custom!.Path}{(Custom.ForceLanguage is { } l ? $" · language: {l}" : "")}";

    public string? Notes => Entry?.Notes;

    public bool IsCustom => Custom is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload), nameof(CanUse))]
    public partial bool IsInstalled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    public partial bool IsRecommended { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string StateText { get; set; } = "";

    [ObservableProperty]
    public partial string? BenchmarkText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    public partial bool HasPartial { get; set; }

    public bool CanDownload => Entry is not null && !IsInstalled && !IsBusy;

    public bool CanUse => IsInstalled;

    public string DownloadLabel => HasPartial ? "Resume" : "Download";

    partial void OnHasPartialChanged(bool value) => OnPropertyChanged(nameof(DownloadLabel));

    [RelayCommand]
    private void Download() => _owner.Download(this);

    [RelayCommand]
    private void Pause() => _owner.Downloader.Pause(Id);

    [RelayCommand]
    private void Cancel() => _owner.Downloader.Cancel(Id);

    [RelayCommand]
    private void Delete() => _owner.Delete(this);

    [RelayCommand]
    private void Use() => _owner.Use(this);

    [RelayCommand]
    private Task Benchmark() => _owner.BenchmarkAsync(this);

    internal static string FormatSize(long bytes) =>
        bytes >= 1_000_000_000 ? $"{bytes / 1e9:F2} GB" : $"{bytes / 1e6:F0} MB";
}

/// <summary>Models tab: catalog + custom models, downloads, hardware hint, benchmark.</summary>
public sealed partial class ModelManagerViewModel : ObservableObject, IDisposable
{
    private readonly AppController _app;

    public ModelManagerViewModel(AppController app)
    {
        _app = app;
        Downloader.ProgressChanged += OnProgress;
        _app.CatalogChanged += OnCatalogChanged;
        var hw = HardwareInfo.Query();
        HardwareText = hw.ToString();
        var rec = ModelAdvisor.Recommend(hw, app.Settings.Engine.Language);
        RecommendedId = rec.ModelId;
        RecommendationText = $"Recommended: {app.Catalog.Find(rec.ModelId)?.DisplayName ?? rec.ModelId}{(rec.SuggestApi ? " — or API mode" : "")}. {rec.Reason}";
        Rebuild();
    }

    public ModelDownloader Downloader => _app.Downloader;

    public ObservableCollection<ModelItemViewModel> Items { get; } = [];

    public string HardwareText { get; }

    public string RecommendationText { get; }

    public string? RecommendedId { get; }

    [ObservableProperty]
    public partial string CustomName { get; set; } = "";

    [ObservableProperty]
    public partial string CustomLanguage { get; set; } = "";

    [ObservableProperty]
    public partial string? Message { get; set; }

    /// <summary>Raised when the set of installed models changed (engine model list must refresh).</summary>
    public event Action? InstalledChanged;

    public void Rebuild()
    {
        Items.Clear();
        foreach (var entry in _app.Catalog.Models)
        {
            Items.Add(new ModelItemViewModel(this, entry, null));
        }
        foreach (var custom in _app.ModelStore.GetCustomModels())
        {
            Items.Add(new ModelItemViewModel(this, null, custom));
        }
        foreach (var item in Items)
        {
            RefreshState(item);
        }
    }

    internal void Download(ModelItemViewModel item)
    {
        if (item.Entry is null)
        {
            return;
        }
        Message = null;
        Downloader.Enqueue(item.Entry);
    }

    internal void Delete(ModelItemViewModel item)
    {
        if (item.IsActive && _app.IsListening && _app.Settings.Engine.Mode == EngineMode.Local)
        {
            Message = "This model is in use. Switch to another model (or stop listening) before deleting it.";
            return;
        }
        string what = item.IsCustom ? "Remove this custom model from the list? (The file itself is not deleted.)" : $"Delete {item.Name} from disk?";
        if (MessageBox.Show(what, "CaptionOverlay", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        if (item.Entry is not null)
        {
            Downloader.Cancel(item.Id);
            _app.ModelStore.Delete(item.Entry);
        }
        else
        {
            _app.ModelStore.RemoveCustomModel(item.Id);
            Items.Remove(item);
        }
        RefreshState(item);
        InstalledChanged?.Invoke();
    }

    internal void Use(ModelItemViewModel item)
    {
        _app.SwitchEngine(EngineMode.Local, item.Id);
        foreach (var i in Items)
        {
            i.IsActive = i.Id == item.Id;
        }
        InstalledChanged?.Invoke();
    }

    internal async Task BenchmarkAsync(ModelItemViewModel item)
    {
        var model = _app.ModelStore.Resolve(item.Id, _app.Catalog);
        if (model is null)
        {
            return;
        }
        item.IsBusy = true;
        item.BenchmarkText = "Benchmarking…";
        try
        {
            await using var transcriber = await LocalWhisperTranscriber.LoadAsync(
                new LocalWhisperOptions { ModelPath = model.Path, DisplayName = model.DisplayName },
                _app.Settings.Engine.Gpu, null);
            var result = await ModelAdvisor.BenchmarkAsync(transcriber);
            item.BenchmarkText = $"RTF {result.RealTimeFactor:F2} on {result.Runtime}: {result.RatingText}";
        }
        catch (Exception ex)
        {
            item.BenchmarkText = $"Benchmark failed: {ex.Message}";
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task AddCustomModel()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a whisper.cpp GGML model",
            Filter = "Whisper GGML model (*.bin)|*.bin|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        string language = CustomLanguage.Trim().ToLowerInvariant();
        if (language.Length > 0 && (language.Length is < 2 or > 3 || !language.All(char.IsAsciiLetterLower)))
        {
            Message = "Forced language must be an ISO code like \"de\" (or empty).";
            return;
        }
        Message = "Checking model file…";
        try
        {
            await LocalWhisperTranscriber.ValidateModelAsync(dialog.FileName);
        }
        catch (Exception ex)
        {
            Message = ex.Message;
            return;
        }
        string name = string.IsNullOrWhiteSpace(CustomName) ? System.IO.Path.GetFileNameWithoutExtension(dialog.FileName) : CustomName.Trim();
        var custom = _app.ModelStore.AddCustomModel(dialog.FileName, name, language.Length == 0 ? null : language);
        var item = new ModelItemViewModel(this, null, custom);
        Items.Add(item);
        RefreshState(item);
        CustomName = "";
        CustomLanguage = "";
        Message = $"Added {name}.";
        InstalledChanged?.Invoke();
    }

    private void RefreshState(ModelItemViewModel item)
    {
        item.IsActive = item.Id == _app.Settings.Engine.ModelId;
        item.IsRecommended = item.Id == RecommendedId;
        if (item.Entry is { } entry)
        {
            item.IsInstalled = _app.ModelStore.IsInstalled(entry);
            long partial = _app.ModelStore.PartialBytes(entry);
            item.HasPartial = !item.IsInstalled && partial > 0;
            var state = Downloader.GetState(entry.Id);
            if (state is { State: DownloadState.Downloading or DownloadState.Verifying or DownloadState.Queued })
            {
                Apply(item, state);
            }
            else
            {
                item.IsBusy = false;
                item.Progress = item.IsInstalled ? 1 : (double)partial / entry.SizeBytes;
                item.StateText = item.IsInstalled ? "Installed"
                    : partial > 0 ? $"Paused at {item.Progress:P0}"
                    : state?.State == DownloadState.Failed ? state.Error ?? "Failed" : "";
            }
        }
        else
        {
            item.IsInstalled = System.IO.File.Exists(item.Custom!.Path);
            item.StateText = item.IsInstalled ? "Available" : "File missing";
        }
    }

    private void OnProgress(DownloadProgress p) => Application.Current.Dispatcher.InvokeAsync(() =>
    {
        var item = Items.FirstOrDefault(i => i.Id == p.ModelId);
        if (item is null)
        {
            return;
        }
        Apply(item, p);
        if (p.State is DownloadState.Completed or DownloadState.Failed or DownloadState.Paused or DownloadState.Canceled)
        {
            RefreshState(item);
            if (p.State == DownloadState.Failed)
            {
                item.StateText = p.Error ?? "Failed";
            }
            if (p.State == DownloadState.Completed)
            {
                if (_app.ModelStore.Resolve(_app.Settings.Engine.ModelId, _app.Catalog) is null)
                {
                    Use(item); // first model: select it automatically
                }
                InstalledChanged?.Invoke();
            }
        }
    });

    private static void Apply(ModelItemViewModel item, DownloadProgress p)
    {
        item.IsBusy = p.State is DownloadState.Downloading or DownloadState.Verifying or DownloadState.Queued;
        item.Progress = p.Fraction;
        item.StateText = p.State switch
        {
            DownloadState.Queued => "Queued…",
            DownloadState.Downloading => $"{p.Fraction:P0} · {p.BytesPerSecond / 1e6:F1} MB/s · {(p.Eta is { } eta ? $"{(int)eta.TotalMinutes}:{eta.Seconds:00} left" : "")}",
            DownloadState.Verifying => "Verifying (SHA-256)…",
            DownloadState.Completed => "Installed",
            DownloadState.Paused => $"Paused at {p.Fraction:P0}",
            DownloadState.Canceled => "",
            DownloadState.Failed => p.Error ?? "Failed",
            _ => "",
        };
    }

    private void OnCatalogChanged() => Application.Current.Dispatcher.InvokeAsync(Rebuild);

    public void Dispose()
    {
        Downloader.ProgressChanged -= OnProgress;
        _app.CatalogChanged -= OnCatalogChanged;
    }
}
