using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace CaptionOverlay.App.Overlay;

/// <summary>
/// Text drawn as geometry with an outline (stroke) behind the fill, readable on any background.
/// Wraps to the available width.
/// </summary>
public sealed class OutlinedTextBlock : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = Register(nameof(Text), typeof(string), "");
    public static readonly DependencyProperty FontFamilyProperty = Register(nameof(FontFamily), typeof(FontFamily), new FontFamily("Segoe UI"));
    public static readonly DependencyProperty FontSizeProperty = Register(nameof(FontSize), typeof(double), 28.0);
    public static readonly DependencyProperty FontWeightProperty = Register(nameof(FontWeight), typeof(FontWeight), FontWeights.SemiBold);
    public static readonly DependencyProperty FontStyleProperty = Register(nameof(FontStyle), typeof(FontStyle), FontStyles.Normal);
    public static readonly DependencyProperty FillProperty = Register(nameof(Fill), typeof(Brush), Brushes.White, affectsMeasure: false);
    public static readonly DependencyProperty StrokeProperty = Register(nameof(Stroke), typeof(Brush), Brushes.Black, affectsMeasure: false);
    public static readonly DependencyProperty StrokeThicknessProperty = Register(nameof(StrokeThickness), typeof(double), 2.0);
    public static readonly DependencyProperty TextAlignmentProperty = Register(nameof(TextAlignment), typeof(TextAlignment), TextAlignment.Center);

    private FormattedText? _formatted;
    private Geometry? _geometry;
    private double _geometryWidth = double.NaN;

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }

    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

    public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }

    public FontStyle FontStyle { get => (FontStyle)GetValue(FontStyleProperty); set => SetValue(FontStyleProperty, value); }

    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

    public TextAlignment TextAlignment { get => (TextAlignment)GetValue(TextAlignmentProperty); set => SetValue(TextAlignmentProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        _formatted = Create(double.IsInfinity(availableSize.Width) ? 10000 : availableSize.Width);
        _geometry = null;
        double pad = StrokeThickness;
        return new Size(
            Math.Min(availableSize.Width, _formatted.WidthIncludingTrailingWhitespace + 2 * pad),
            _formatted.Height + 2 * pad);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // Building the outline geometry is the expensive part: reuse it unless the text was re-measured (_geometry
        // reset) or the width changed. Animated line moves re-arrange every frame.
        if (_geometry is null || finalSize.Width != _geometryWidth)
        {
            _formatted = Create(finalSize.Width);
            _geometry = _formatted.BuildGeometry(new Point(StrokeThickness, StrokeThickness));
            _geometryWidth = finalSize.Width;
        }
        return finalSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_geometry is null)
        {
            return;
        }
        if (StrokeThickness > 0)
        {
            // Stroke twice as wide and draw the fill on top, so the outline sits outside the glyphs.
            var pen = new Pen(Stroke, StrokeThickness * 2) { LineJoin = PenLineJoin.Round };
            dc.DrawGeometry(null, pen, _geometry);
        }
        dc.DrawGeometry(Fill, null, _geometry);
    }

    private FormattedText Create(double width)
    {
        var ft = new FormattedText(
            Text ?? "",
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyle, FontWeight, FontStretches.Normal),
            Math.Max(1, FontSize),
            Fill,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, width - 2 * StrokeThickness),
            TextAlignment = TextAlignment,
            Trimming = TextTrimming.None,
        };
        return ft;
    }

    private static DependencyProperty Register(string name, Type type, object defaultValue, bool affectsMeasure = true) =>
        DependencyProperty.Register(name, type, typeof(OutlinedTextBlock), new FrameworkPropertyMetadata(defaultValue,
            affectsMeasure
                ? FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange | FrameworkPropertyMetadataOptions.AffectsRender
                : FrameworkPropertyMetadataOptions.AffectsRender));
}
