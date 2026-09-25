using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CaptionOverlay.Core.Captions;
using CaptionOverlay.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CaptionOverlay.App.Overlay;

public sealed partial class CaptionLineViewModel(Guid id, string text) : ObservableObject
{
    public Guid Id { get; } = id;

    public string Text { get; } = text;
}

public sealed partial class OverlayViewModel : ObservableObject
{
    private readonly DispatcherTimer _fadeTimer;
    private DateTime _lastActivity = DateTime.UtcNow;
    private OverlaySettings _settings;

    public OverlayViewModel(OverlaySettings settings)
    {
        _settings = settings;
        ApplyStyle(settings);
        _fadeTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => CheckFade(), Dispatcher.CurrentDispatcher);
        _fadeTimer.Start();
    }

    /// <summary>Raised when the user changed styling from the edit toolbar (persist it).</summary>
    public event Action? StyleEdited;

    /// <summary>Raised when the user clicks "Done" in edit mode.</summary>
    public event Action? EditFinished;

    public ObservableCollection<CaptionLineViewModel> Lines { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContent), nameof(ShowPanel))]
    public partial string? TentativeText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPanel), nameof(ShowPlaceholder))]
    public partial bool IsEditMode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPanel))]
    public partial bool IsFaded { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial FontFamily FontFamily { get; set; } = new("Segoe UI");

    [ObservableProperty]
    public partial double FontSize { get; set; }

    [ObservableProperty]
    public partial FontWeight FontWeight { get; set; }

    [ObservableProperty]
    public partial Brush TextBrush { get; set; } = Brushes.White;

    [ObservableProperty]
    public partial Brush OutlineBrush { get; set; } = Brushes.Black;

    [ObservableProperty]
    public partial double OutlineThickness { get; set; }

    [ObservableProperty]
    public partial Brush BackgroundBrush { get; set; } = Brushes.Transparent;

    [ObservableProperty]
    public partial double BackgroundOpacity { get; set; }

    [ObservableProperty]
    public partial int LinesShown { get; set; }

    public bool HasContent => Lines.Count > 0 || !string.IsNullOrEmpty(TentativeText);

    public bool ShowPanel => IsEditMode || (HasContent && !IsFaded);

    public bool ShowPlaceholder => IsEditMode && !HasContent;

    public void ApplyStyle(OverlaySettings s)
    {
        _settings = s;
        FontFamily = new FontFamily(s.FontFamily);
        FontSize = s.FontSize;
        FontWeight = FontWeight.FromOpenTypeWeight(Math.Clamp(s.FontWeight, 100, 950));
        TextBrush = ParseBrush(s.TextColor, Colors.White);
        OutlineBrush = ParseBrush(s.OutlineColor, Colors.Black);
        OutlineThickness = s.OutlineThickness;
        BackgroundOpacity = s.BackgroundOpacity;
        LinesShown = Math.Max(1, s.LinesShown);
        UpdateBackground();
    }

    /// <summary>Renders the newest committed lines plus the tentative line. UI thread only.</summary>
    public void Update(CaptionSnapshot snapshot)
    {
        // The tentative line takes one of the visible slots.
        var wanted = snapshot.Lines.TakeLast(LinesShown - (snapshot.Tentative is null ? 0 : 1)).ToList();

        // Diff instead of rebuild, so unchanged lines keep their visuals (no flicker, fade-in only on new ones).
        for (int i = Lines.Count - 1; i >= 0; i--)
        {
            if (!wanted.Any(w => w.UtteranceId == Lines[i].Id))
            {
                Lines.RemoveAt(i);
            }
        }
        foreach (var line in wanted)
        {
            if (!Lines.Any(l => l.Id == line.UtteranceId))
            {
                Lines.Add(new CaptionLineViewModel(line.UtteranceId, line.Text));
            }
        }

        TentativeText = snapshot.Tentative?.Text;
        _lastActivity = DateTime.UtcNow;
        IsFaded = false;
        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(ShowPanel));
        OnPropertyChanged(nameof(ShowPlaceholder));
    }

    partial void OnBackgroundOpacityChanged(double value) => UpdateBackground();

    [RelayCommand]
    private void IncreaseFont() => EditStyle(() => _settings.FontSize = FontSize = Math.Min(96, FontSize + 2));

    [RelayCommand]
    private void DecreaseFont() => EditStyle(() => _settings.FontSize = FontSize = Math.Max(10, FontSize - 2));

    [RelayCommand]
    private void MoreLines() => EditStyle(() => _settings.LinesShown = LinesShown = Math.Min(8, LinesShown + 1));

    [RelayCommand]
    private void FewerLines() => EditStyle(() => _settings.LinesShown = LinesShown = Math.Max(1, LinesShown - 1));

    [RelayCommand]
    private void Done() => EditFinished?.Invoke();

    /// <summary>Called by the opacity slider after the user releases it.</summary>
    public void CommitOpacity() => EditStyle(() => _settings.BackgroundOpacity = BackgroundOpacity);

    private void EditStyle(Action change)
    {
        change();
        StyleEdited?.Invoke();
    }

    private void UpdateBackground()
    {
        var color = ParseBrush(_settings.BackgroundColor, Colors.Black).Color;
        color.A = (byte)Math.Round(Math.Clamp(BackgroundOpacity, 0, 1) * 255);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        BackgroundBrush = brush;
    }

    private void CheckFade()
    {
        double timeout = _settings.FadeTimeoutSec;
        if (!IsEditMode && timeout > 0 && HasContent && !IsFaded && DateTime.UtcNow - _lastActivity > TimeSpan.FromSeconds(timeout))
        {
            IsFaded = true;
        }
    }

    private static SolidColorBrush ParseBrush(string? text, Color fallback)
    {
        Color color;
        try
        {
            color = text is null ? fallback : (Color)ColorConverter.ConvertFromString(text);
        }
        catch (FormatException)
        {
            color = fallback;
        }
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
