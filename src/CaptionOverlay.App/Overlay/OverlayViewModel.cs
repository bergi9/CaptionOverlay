using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CaptionOverlay.Core.Captions;
using CaptionOverlay.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CaptionOverlay.App.Overlay;

/// <summary>
/// One overlay row: a piece of an utterance (see <see cref="CaptionLineSplitter"/>), in progress (tentative) and later the
/// same row committed. Key = utterance id + row index, so a row keeps its place when the utterance is committed.
/// </summary>
public sealed partial class CaptionLineViewModel(string key) : ObservableObject
{
    public string Key { get; } = key;

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
    private CaptionSnapshot? _lastSnapshot;
    private double _rowWidth;
    private Func<string, double>? _measure;

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
        Refresh(); // the font may have changed: split again
    }

    /// <summary>
    /// Width available for one row of text and a function measuring text in the caption font (both in DIPs), set by
    /// the window. Until they are known, each utterance is one row.
    /// </summary>
    public void SetTextLayout(double rowWidth, Func<string, double> measure)
    {
        if (Math.Abs(rowWidth - _rowWidth) < 0.5 && _measure is not null)
        {
            return;
        }
        _rowWidth = rowWidth;
        _measure = measure;
        Refresh();
    }

    /// <summary>Splits the last captions again (width or font changed).</summary>
    public void Refresh()
    {
        if (_lastSnapshot is { } snapshot)
        {
            Render(snapshot);
        }
    }

    /// <summary>
    /// Renders the newest rows: committed captions and the tentative one, each split into rows that fit the width
    /// (<see cref="CaptionLineSplitter"/>); <see cref="LinesShown"/> counts rows. UI thread only. Rows are diffed,
    /// never rebuilt: the tentative rows of an utterance become its committed rows in place (italic → normal, no jump),
    /// new rows grow in at the bottom and old ones animate out at the top (see <see cref="RevealDecorator"/>).
    /// </summary>
    public void Update(CaptionSnapshot snapshot)
    {
        _lastSnapshot = snapshot;
        Render(snapshot);
        _lastActivity = DateTime.UtcNow;
        IsFaded = false;
        OnPropertyChanged(nameof(ShowPanel));
    }

    private void Render(CaptionSnapshot snapshot)
    {
        var rows = new List<(string Key, string Text, bool Tentative)>();
        foreach (var line in snapshot.Lines)
        {
            AddRows(rows, line.UtteranceId, line.Text, tentative: false);
        }
        if (snapshot.Tentative is { } tentative)
        {
            AddRows(rows, tentative.UtteranceId, tentative.Text, tentative: true);
        }
        var wanted = rows.TakeLast(LinesShown).ToList();

        foreach (var line in Lines.Where(l => !l.IsLeaving && !wanted.Any(w => w.Key == l.Key)).ToList())
        {
            Leave(line);
        }

        // Insert or update in the wanted order; leaving rows keep their place until their animation is over.
        for (int i = 0; i < wanted.Count; i++)
        {
            var (key, text, isTentative) = wanted[i];
            var line = Lines.FirstOrDefault(l => l.Key == key);
            if (line is null)
            {
                line = new CaptionLineViewModel(key);
                var nextWanted = wanted.Skip(i + 1).Select(w => Lines.FirstOrDefault(l => l.Key == w.Key)).FirstOrDefault(l => l is not null);
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

        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(ShowPanel));
        OnPropertyChanged(nameof(ShowPlaceholder));
    }

    private void AddRows(List<(string Key, string Text, bool Tentative)> rows, Guid utteranceId, string text, bool tentative)
    {
        // OutlinedTextBlock wraps at its width minus the outline on both sides; 3 % spare for italic (tentative) text, so a
        // row never wraps when it turns from italic to normal or back.
        double width = (_rowWidth - 2 * OutlineThickness) * 0.97;
        var pieces = _measure is { } measure ? CaptionLineSplitter.Split(text, width, measure) : [text];
        for (int i = 0; i < pieces.Count; i++)
        {
            rows.Add((string.Create(CultureInfo.InvariantCulture, $"{utteranceId:N}/{i}"), pieces[i], tentative));
        }
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

    partial void OnLinesShownChanged(int value) => Refresh();

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
