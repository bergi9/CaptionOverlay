using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace CaptionOverlay.App.Overlay;

/// <summary>
/// Animates the height (and opacity) of a caption line instead of letting it jump, so the other lines slide:
/// a new line grows in at the bottom, a leaving line shrinks away at the top, and a line that wraps to another row
/// grows smoothly. The child stays bottom-aligned and is clipped while the height changes.
/// </summary>
public sealed class RevealDecorator : Decorator
{
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(350);

    public static readonly DependencyProperty IsShownProperty = DependencyProperty.Register(
        nameof(IsShown), typeof(bool), typeof(RevealDecorator),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsMeasure, (d, _) => ((RevealDecorator)d).OnIsShownChanged()));

    /// <summary>The height currently shown (animated towards the child's height, or 0 when hidden); NaN = follow directly.</summary>
    public static readonly DependencyProperty DisplayHeightProperty = DependencyProperty.Register(
        nameof(DisplayHeight), typeof(double), typeof(RevealDecorator),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure));

    // Ease-out: the movement starts right away and settles gently (ease-in-out bunched it into ~100 ms mid-way).
    private static readonly IEasingFunction Ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
    private readonly TranslateTransform _offset = new();
    private double _target = double.NaN;

    public RevealDecorator()
    {
        ClipToBounds = true;
        if (Enabled)
        {
            // A new line starts collapsed and invisible, then grows in (see MeasureOverride).
            DisplayHeight = 0;
            Opacity = 0;
            Loaded += (_, _) => AnimateOpacity(IsShown ? 1 : 0);
        }
    }

    /// <summary>Overlay setting "Animate line changes" (on by default, independent of the Windows animation setting).</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>
    /// While true, every decorator measures at its final size (shown: full height, hidden: none). The overlay window
    /// uses this to size itself for the end of the running animations up front.
    /// </summary>
    public static bool MeasureFinalLayout { get; set; }

    public bool IsShown
    {
        get => (bool)GetValue(IsShownProperty);
        set => SetValue(IsShownProperty, value);
    }

    public double DisplayHeight
    {
        get => (double)GetValue(DisplayHeightProperty);
        set => SetValue(DisplayHeightProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is null)
        {
            return default;
        }
        Child.Measure(constraint);
        double target = IsShown ? Child.DesiredSize.Height : 0;
        if (MeasureFinalLayout)
        {
            return new Size(Child.DesiredSize.Width, target);
        }
        if (target != _target)
        {
            _target = target;
            if (Enabled && !double.IsNaN(DisplayHeight))
            {
                // Start after this layout pass (changing an animated property inside Measure would re-enter layout).
                Dispatcher.BeginInvoke(DispatcherPriority.Render, () => BeginAnimation(DisplayHeightProperty,
                    new DoubleAnimation(target, Duration) { EasingFunction = Ease }));
            }
            else
            {
                BeginAnimation(DisplayHeightProperty, null);
                DisplayHeight = double.NaN;
            }
        }
        double shown = double.IsNaN(DisplayHeight) ? target : Math.Max(0, DisplayHeight);
        return new Size(Child.DesiredSize.Width, shown);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (Child is { } child)
        {
            // Same rect every frame, so the text is not laid out again; the bottom alignment is a render offset.
            double height = child.DesiredSize.Height;
            child.Arrange(new Rect(0, 0, arrangeSize.Width, height));
            child.RenderTransform = _offset;
            _offset.Y = arrangeSize.Height - height;
        }
        return arrangeSize;
    }

    private void OnIsShownChanged()
    {
        if (IsLoaded)
        {
            AnimateOpacity(IsShown ? 1 : 0);
        }
    }

    private void AnimateOpacity(double to)
    {
        if (!Enabled)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = to;
            return;
        }
        // From the current (possibly mid-animation) value, so reversing halfway does not jump.
        BeginAnimation(OpacityProperty, new DoubleAnimation(to, Duration) { EasingFunction = Ease });
    }

    /// <summary>Invalidates the measure of every decorator below <paramref name="root"/> (after toggling <see cref="MeasureFinalLayout"/>).</summary>
    public static void InvalidateAll(DependencyObject root)
    {
        if (root is RevealDecorator decorator)
        {
            decorator.InvalidateMeasure();
        }
        for (int i = 0, n = VisualTreeHelper.GetChildrenCount(root); i < n; i++)
        {
            InvalidateAll(VisualTreeHelper.GetChild(root, i));
        }
    }
}
