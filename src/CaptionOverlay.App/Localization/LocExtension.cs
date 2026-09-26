using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using CaptionOverlay.Core.Localization;

namespace CaptionOverlay.App.Localization;

/// <summary>The app's UI strings: <c>Localization/Strings*.resx</c>, registered with <see cref="Loc"/> at startup.</summary>
internal static class UiStrings
{
    public static ResourceManager ResourceManager { get; } = new("CaptionOverlay.App.Localization.Strings", typeof(UiStrings).Assembly);
}

/// <summary>Bindable view of <see cref="Loc"/>: bindings to <c>[Key]</c> refresh when the UI language changes.</summary>
public sealed class LocalizationSource : INotifyPropertyChanged
{
    private LocalizationSource() => Loc.CultureChanged += () => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));

    public static LocalizationSource Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => Loc.Get(key);
}

/// <summary>
/// <c>{l:Loc Tab_General}</c> → localized text that follows language changes at runtime.
/// <c>{l:Loc Overlay_Size, Value={Binding OverlayFontSize}}</c> → the text used as a format string for the bound value (e.g. "Size: {0:F0}").
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    /// <summary>Optional value inserted into the text as <c>{0}</c>.</summary>
    public BindingBase? Value { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var text = new Binding($"[{Key}]") { Source = LocalizationSource.Instance, Mode = BindingMode.OneWay };
        if (Value is null)
        {
            return text.ProvideValue(serviceProvider);
        }
        var multi = new MultiBinding { Converter = FormatConverter.Instance, Mode = BindingMode.OneWay };
        multi.Bindings.Add(text);
        multi.Bindings.Add(Value);
        return multi.ProvideValue(serviceProvider);
    }

    private sealed class FormatConverter : IMultiValueConverter
    {
        public static readonly FormatConverter Instance = new();

        public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values is not [string format, var value] || value == DependencyProperty.UnsetValue)
            {
                return "";
            }
            try
            {
                return string.Format(Loc.Culture, format, value);
            }
            catch (FormatException)
            {
                return format; // a broken translation must not crash the window
            }
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
