using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CaptionOverlay.Core.Captions;
using CaptionOverlay.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CaptionOverlay.App.Overlay;

/// <summary>One overlay line: the in-progress (tentative) text of an utterance, later the same line committed.</summary>
public sealed partial class CaptionLineViewModel(Guid id) : ObservableObject
{
    public Guid Id { get; } = id;

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    /// <summary>Still being spoken: italic and dimmed until the final text arrives.</summary>
    [ObservableProperty]
    public partial bool IsTentative { get; set; }

    /// <summary>Scrolled out or discarded: animates away, then is removed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShown))]
    public partial bool IsLeaving { get; set; }

    public bool IsShown => !IsLeaving;

    internal DispatcherTimer? RemovalTimer { get; set; }
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

    public bool HasContent => Lines.Any(l => !l.IsLeaving);

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
        RevealDecorator.Enabled = s.AnimateLines;
        UpdateBackground();
    }

    /// <summary>
    /// Renders the newest committed lines plus the tentative line. UI thread only. Lines are diffed, never rebuilt:
    /// the tentative line of an utterance becomes its committed line in place (italic → normal, no jump), new lines
    /// grow in at the bottom and old ones animate out at the top (see <see cref="RevealDecorator"/>).
    /// </summary>
    public void Update(CaptionSnapshot snapshot)
    {
        // The tentative line takes one of the visible slots.
        var wanted = snapshot.Lines.TakeLast(Math.Max(0, LinesShown - (snapshot.Tentative is null ? 0 : 1)))
            .Select(l => (Id: l.UtteranceId, l.Text, Tentative: false))
            .ToList();
        if (snapshot.Tentative is { } tentative)
        {
            wanted.Add((tentative.UtteranceId, tentative.Text, true));
        }

        foreach (var line in Lines.Where(l => !l.IsLeaving && !wanted.Any(w => w.Id == l.Id)).ToList())
        {
            Leave(line);
        }

        // Insert or update in the wanted order; leaving lines keep their place until their animation is over.
        for (int i = 0; i < wanted.Count; i++)
        {
            var (id, text, isTentative) = wanted[i];
            var line = Lines.FirstOrDefault(l => l.Id == id);
            if (line is null)
            {
                line = new CaptionLineViewModel(id);
                var nextWanted = wanted.Skip(i + 1).Select(w => Lines.FirstOrDefault(l => l.Id == w.Id)).FirstOrDefault(l => l is not null);
                Lines.Insert(nextWanted is null ? Lines.Count : Lines.IndexOf(nextWanted), line);
            }
            else if (line.IsLeaving)
            {
                // Wanted again (e.g. committed right after the next utterance's tentative text replaced it).
                line.RemovalTimer?.Stop();
                line.IsLeaving = false;
            }
            line.Text = text;
            line.IsTentative = isTentative;
        }

        _lastActivity = DateTime.UtcNow;
        IsFaded = false;
        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(ShowPanel));
        OnPropertyChanged(nameof(ShowPlaceholder));
    }

    private void Leave(CaptionLineViewModel line)
    {
        line.IsLeaving = true;
        line.RemovalTimer ??= new DispatcherTimer(RevealDecorator.Duration + TimeSpan.FromMilliseconds(60), DispatcherPriority.Background, (_, _) =>
        {
            line.RemovalTimer!.Stop();
            if (line.IsLeaving)
            {
                Lines.Remove(line);
            }
        }, Dispatcher.CurrentDispatcher);
        line.RemovalTimer.Stop();
        line.RemovalTimer.Start();
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
