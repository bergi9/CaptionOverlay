using System.Windows;
using CaptionOverlay.Core.Settings;

namespace CaptionOverlay.App.Infrastructure;

/// <summary>
/// Light/dark mode through WPF's Fluent theme (App.xaml merges Fluent.xaml, which follows Windows). "System" keeps
/// following the Windows app mode live; Light/Dark pin it. The caption overlay draws its own colors and is unaffected.
/// </summary>
public static class ThemeService
{
#pragma warning disable WPF0001 // ThemeMode is marked experimental in .NET 10
    public static void Apply(AppTheme theme)
    {
        var mode = theme switch
        {
            AppTheme.Light => ThemeMode.Light,
            AppTheme.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
        if (Application.Current is { } app && app.ThemeMode != mode)
        {
            app.ThemeMode = mode;
        }
    }
#pragma warning restore WPF0001
}
