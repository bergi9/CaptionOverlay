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
        ? Loc.Format("Models_Details", FormatSize(e.SizeBytes), e.IsMultilingual ? Loc.Get("Models_Multilingual") : string.Join(", ", e.Languages), e.License ?? Loc.Get("Models_UnknownLicense"))
        : Custom!.ForceLanguage is { } l ? Loc.Format("Models_CustomDetailsLanguage", Custom.Path, l) : Loc.Format("Models_CustomDetails", Custom.Path);

    /// <summary>Catalog note; translations are keyed by model id ("ModelNote_" + id) and fall back to the catalog text.</summary>
    public string? Notes => Entry is null ? null : Loc.TryGet("ModelNote_" + Entry.Id) ?? Entry.Notes;

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

    public string DownloadLabel => Loc.Get(HasPartial ? "Models_Resume" : "Models_Download");

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
        bytes >= 1_000_000_000 ? string.Format(Loc.Culture, "{0:F2} GB", bytes / 1e9) : string.Format(Loc.Culture, "{0:F0} MB", bytes / 1e6);
}

/// <summary>Models tab: catalog + custom models, downloads, hardware hint, benchmark.</summary>
public sealed partial class ModelManagerViewModel : ObservableObject, IDisposable
{
    private readonly AppController _app;
    private readonly HardwareSummary _hardware;

    public ModelManagerViewModel(AppController app)
    {
        _app = app;
        Downloader.ProgressChanged += OnProgress;
        _app.CatalogChanged += OnCatalogChanged;
        _hardware = HardwareInfo.Query();
        RecommendedId = ModelAdvisor.Recommend(_hardware, app.Settings.Engine.Language).ModelId;
        Rebuild();
    }

    public ModelDownloader Downloader => _app.Downloader;

    public ObservableCollection<ModelItemViewModel> Items { get; } = [];

    public string HardwareText => _hardware.Describe();

    public string RecommendationText
    {
        get
        {
            var rec = ModelAdvisor.Recommend(_hardware, _app.Settings.Engine.Language);
            string name = _app.Catalog.Find(rec.ModelId)?.DisplayName ?? rec.ModelId ?? "";
            return Loc.Format(rec.SuggestApi ? "Models_RecommendedOrApi" : "Models_RecommendedModel", name, rec.Reason);
        }
    }

    public string? RecommendedId { get; }

    [ObservableProperty]
    public partial string CustomName { get; set; } = "";

    [ObservableProperty]
    public partial string CustomLanguage { get; set; } = "";

    [ObservableProperty]
    public partial string? Message { get; set; }

    /// <summary>Raised when the set of installed models changed (engine model list must refresh).</summary>
    public event Action? InstalledChanged;

    /// <summary>Recreates the rows (also after a UI language change, so every text is re-translated).</summary>
    public void Rebuild()
    {
        OnPropertyChanged(nameof(HardwareText));
        OnPropertyChanged(nameof(RecommendationText));
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
            Message = Loc.Get("Models_InUseCannotDelete");
            return;
        }
        string what = item.IsCustom ? Loc.Get("Models_ConfirmRemoveCustom") : Loc.Format("Models_ConfirmDelete", item.Name);
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
        item.BenchmarkText = Loc.Get("Models_Benchmarking");
        try
        {
            await using var transcriber = await LocalWhisperTranscriber.LoadAsync(
                new LocalWhisperOptions { ModelPath = model.Path, DisplayName = model.DisplayName },
                _app.Settings.Engine.Gpu, null);
            var result = await ModelAdvisor.BenchmarkAsync(transcriber);
            item.BenchmarkText = Loc.Format("Models_BenchmarkResult", result.RealTimeFactor, result.Runtime, result.RatingText);
        }
        catch (Exception ex)
        {
            item.BenchmarkText = Loc.Format("Common_BenchmarkFailed", ex.Message);
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
            Title = Loc.Get("Models_ChooseFileTitle"),
            Filter = Loc.Get("Models_ChooseFileFilter"),
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        string language = CustomLanguage.Trim().ToLowerInvariant();
        if (language.Length > 0 && (language.Length is < 2 or > 3 || !language.All(char.IsAsciiLetterLower)))
        {
            Message = Loc.Get("Models_InvalidLanguage");
            return;
        }
        Message = Loc.Get("Models_Checking");
        try
        {
            await LocalWhisperTranscriber.ValidateModelAsync(dialog.FileName, _app.Settings.Engine.Gpu);
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
        Message = Loc.Format("Models_Added", name);
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
                item.StateText = item.IsInstalled ? Loc.Get("Models_Installed")
                    : partial > 0 ? Loc.Format("Models_PausedAt", item.Progress)
                    : state?.State == DownloadState.Failed ? state.Error ?? Loc.Get("Models_Failed") : "";
            }
        }
        else
        {
            item.IsInstalled = System.IO.File.Exists(item.Custom!.Path);
            item.StateText = Loc.Get(item.IsInstalled ? "Models_Available" : "Models_FileMissing");
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
                item.StateText = p.Error ?? Loc.Get("Models_Failed");
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
            DownloadState.Queued => Loc.Get("Models_Queued"),
            DownloadState.Downloading => p.Eta is { } eta
                ? Loc.Format("Models_DownloadingEta", p.Fraction, p.BytesPerSecond / 1e6, $"{(int)eta.TotalMinutes}:{eta.Seconds:00}")
                : Loc.Format("Models_Downloading", p.Fraction, p.BytesPerSecond / 1e6),
            DownloadState.Verifying => Loc.Get("Models_Verifying"),
            DownloadState.Completed => Loc.Get("Models_Installed"),
            DownloadState.Paused => Loc.Format("Models_PausedAt", p.Fraction),
            DownloadState.Canceled => "",
            DownloadState.Failed => p.Error ?? Loc.Get("Models_Failed"),
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
