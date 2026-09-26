using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CaptionOverlay.App.Interop;
using CaptionOverlay.Core.Settings;
using static CaptionOverlay.App.Interop.NativeMethods;

namespace CaptionOverlay.App.Overlay;

/// <summary>
/// Transparent, always-on-top, click-through caption window. Positioned in physical pixels relative to a
/// monitor's work area (DPI independent) and anchored at its bottom edge: it grows upwards with more text.
/// </summary>
public partial class OverlayWindow : Window
{
    private const double MinWidthFraction = 0.15;

    /// <summary>The panel's fade-out (0.6 s in OverlayWindow.xaml) plus a margin: after it the panel is invisible.</summary>
    private static readonly TimeSpan PanelFadeOut = TimeSpan.FromMilliseconds(700);
    private readonly OverlayViewModel _vm;
    private readonly OverlaySettings _settings;
    private readonly DispatcherTimer _topmostTimer;
    private readonly WinEventDelegate _foregroundHook; // keep delegate alive
    private IntPtr _hook;
    private IntPtr _hwnd;
    private int _bottomPx;
    private bool _heightUpdateQueued;
    private long _lastContentChange;
    private DispatcherTimer? _shrinkTimer;

    public OverlayWindow(OverlayViewModel vm, OverlaySettings settings)
    {
        InitializeComponent();
        _vm = vm;
        _settings = settings;
        DataContext = vm;
        _foregroundHook = (_, _, _, _, _, _, _) => ReassertTopmost();
        _topmostTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => ReassertTopmost(), Dispatcher);

        vm.PropertyChanged += (_, _) => OnContentChanged();
        vm.Lines.CollectionChanged += (_, _) => OnContentChanged();
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        DpiChanged += (_, _) => QueueHeightUpdate();
        LinesHost.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged)
            {
                _vm.SetTextLayout(LinesHost.ActualWidth, MeasureCaption);
            }
        };
    }

    /// <summary>Raised after the user moved or resized the overlay (placement already written to settings).</summary>
    public event Action? PlacementChanged;

    public bool IsEditMode => _vm.IsEditMode;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        ApplyExtendedStyle(editMode: false);
        ApplyPlacement();
        _topmostTimer.Start();
        // Some apps (games, video players) grab topmost when they come to the foreground: re-assert then.
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _foregroundHook, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    protected override void OnClosed(EventArgs e)
    {
        _topmostTimer.Stop();
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
        }
        base.OnClosed(e);
    }

    public void SetEditMode(bool edit)
    {
        _vm.IsEditMode = edit;
        ApplyExtendedStyle(edit);
        QueueHeightUpdate();
    }

    /// <summary>Places the overlay on the saved monitor, or bottom-centre of the primary monitor if it is gone.</summary>
    public void ApplyPlacement()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }
        var monitor = MonitorHelper.Find(_settings.LastMonitor) ?? MonitorHelper.Primary();
        var placement = _settings.Placements.GetValueOrDefault(monitor.DeviceName) ?? DefaultPlacement();
        var work = monitor.WorkArea;
        int width = (int)Math.Round(work.Width * Math.Clamp(placement.Width, MinWidthFraction, 1));
        int left = work.Left + (int)Math.Round(work.Width * Math.Clamp(placement.Left, 0, 1));
        left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - width));
        _bottomPx = Math.Clamp(work.Top + (int)Math.Round(work.Height * placement.Bottom), work.Top + 50, work.Bottom);
        int height = CurrentHeightPx();
        SetWindowPos(_hwnd, HWND_TOPMOST, left, _bottomPx - height, width, height, SWP_NOACTIVATE);
        QueueHeightUpdate();
    }

    private OverlayPlacement DefaultPlacement()
    {
        double w = Math.Clamp(_settings.DefaultWidthFraction, MinWidthFraction, 1);
        return new OverlayPlacement { Left = (1 - w) / 2, Bottom = 0.93, Width = w };
    }

    /// <summary>Stores the current rect as fractions of the monitor it is on.</summary>
    private void SavePlacement()
    {
        if (!GetWindowRect(_hwnd, out var rect))
        {
            return;
        }
        var monitor = MonitorHelper.ForWindow(_hwnd);
        var work = monitor.WorkArea;
        _bottomPx = rect.Bottom;
        _settings.LastMonitor = monitor.DeviceName;
        _settings.Placements[monitor.DeviceName] = new OverlayPlacement
        {
            Left = (rect.Left - work.Left) / (double)Math.Max(1, work.Width),
            Bottom = (rect.Bottom - work.Top) / (double)Math.Max(1, work.Height),
            Width = rect.Width / (double)Math.Max(1, work.Width),
        };
        PlacementChanged?.Invoke();
    }

    private void ApplyExtendedStyle(bool editMode)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }
        long style = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        style |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        style &= ~WS_EX_APPWINDOW;
        style = editMode ? style & ~WS_EX_TRANSPARENT : style | WS_EX_TRANSPARENT;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(style));
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    private void ReassertTopmost()
    {
        if (_hwnd != IntPtr.Zero && IsVisible)
        {
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }
    }

    /// <summary>Width of <paramref name="text"/> on one line in the caption font (DIPs), for splitting captions into rows.</summary>
    private double MeasureCaption(string text) =>
        new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(_vm.FontFamily, FontStyles.Normal, _vm.FontWeight, FontStretches.Normal), Math.Max(1, _vm.FontSize),
            Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;

    private void OnContentChanged()
    {
        _lastContentChange = Stopwatch.GetTimestamp();
        QueueHeightUpdate();
    }

    private void QueueHeightUpdate()
    {
        if (_heightUpdateQueued)
        {
            return;
        }
        _heightUpdateQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, UpdateHeight);
    }

    /// <summary>
    /// Resizes the window to fit its content while keeping the bottom edge in place, sized for the end of the running
    /// line animations. Resizing a layered window shows its previous picture at the new position for a frame or two
    /// (measured: the text jumped by the height change and back), so while captions are visible the window only grows,
    /// with one text row of room to spare, and it shrinks only once the panel has faded out. The extra height above the
    /// panel is transparent (and click-through).
    /// </summary>
    private void UpdateHeight()
    {
        _heightUpdateQueued = false;
        if (_hwnd == IntPtr.Zero || !GetWindowRect(_hwnd, out var rect))
        {
            return;
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        // Flush pending layout first: a child whose visibility just changed only marks its ancestors
        // dirty during a layout pass, otherwise Measure below would return a stale cached size.
        UpdateLayout();
        var available = new Size(rect.Width / dpi.DpiScaleX, double.PositiveInfinity);
        RevealDecorator.MeasureFinalLayout = true;
        try
        {
            RevealDecorator.InvalidateAll(Root);
            Root.Measure(available);
        }
        finally
        {
            RevealDecorator.MeasureFinalLayout = false;
            RevealDecorator.InvalidateAll(Root);
        }
        int height = Math.Max(1, (int)Math.Ceiling(Root.DesiredSize.Height * dpi.DpiScaleY));
        Root.Measure(available);

        int target;
        if (_vm.IsEditMode || rect.Bottom != _bottomPx)
        {
            target = height; // the user is arranging the overlay (or it moved): fit exactly
        }
        else if (height > rect.Height)
        {
            target = height + (int)Math.Ceiling(_vm.FontSize * _vm.FontFamily.LineSpacing * dpi.DpiScaleY);
        }
        else if (height < rect.Height && !_vm.ShowPanel)
        {
            var sinceHidden = Stopwatch.GetElapsedTime(_lastContentChange);
            if (sinceHidden < PanelFadeOut)
            {
                _shrinkTimer ??= new DispatcherTimer(DispatcherPriority.Render, Dispatcher);
                _shrinkTimer.Stop();
                _shrinkTimer.Interval = PanelFadeOut - sinceHidden;
                _shrinkTimer.Tick -= OnShrinkTimer;
                _shrinkTimer.Tick += OnShrinkTimer;
                _shrinkTimer.Start();
                return;
            }
            target = height;
        }
        else
        {
            return;
        }
        if (target != rect.Height || rect.Bottom != _bottomPx)
        {
            SetWindowPos(_hwnd, HWND_TOPMOST, rect.Left, _bottomPx - target, rect.Width, target, SWP_NOACTIVATE);
        }
    }

    private void OnShrinkTimer(object? sender, EventArgs e)
    {
        _shrinkTimer!.Stop();
        QueueHeightUpdate();
    }

    private int CurrentHeightPx() => GetWindowRect(_hwnd, out var r) && r.Height > 0 ? r.Height : 100;

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_vm.IsEditMode || e.OriginalSource is Thumb)
        {
            return;
        }
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        SavePlacement();
        QueueHeightUpdate();
    }

    private void LeftGrip_OnDragDelta(object sender, DragDeltaEventArgs e) => ResizeHorizontally(e.HorizontalChange, fromLeft: true);

    private void RightGrip_OnDragDelta(object sender, DragDeltaEventArgs e) => ResizeHorizontally(e.HorizontalChange, fromLeft: false);

    private void Grip_OnDragCompleted(object sender, DragCompletedEventArgs e) => SavePlacement();

    private void ResizeHorizontally(double deltaDip, bool fromLeft)
    {
        if (!GetWindowRect(_hwnd, out var rect))
        {
            return;
        }
        int delta = (int)Math.Round(deltaDip * VisualTreeHelper.GetDpi(this).DpiScaleX);
        var work = MonitorHelper.ForWindow(_hwnd).WorkArea;
        int minWidth = (int)(work.Width * MinWidthFraction);
        int left = rect.Left;
        int width = rect.Width;
        if (fromLeft)
        {
            int newWidth = Math.Max(minWidth, width - delta);
            left += width - newWidth;
            width = newWidth;
        }
        else
        {
            width = Math.Max(minWidth, width + delta);
        }
        SetWindowPos(_hwnd, HWND_TOPMOST, left, rect.Top, width, rect.Height, SWP_NOACTIVATE);
        QueueHeightUpdate();
    }

    private void OpacitySlider_OnMouseUp(object sender, MouseButtonEventArgs e) => _vm.CommitOpacity();
}
