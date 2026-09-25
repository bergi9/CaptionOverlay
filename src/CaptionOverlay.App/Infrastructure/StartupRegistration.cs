using Microsoft.Win32;

namespace CaptionOverlay.App.Infrastructure;

/// <summary>Per-user "Start with Windows" via HKCU\...\Run (no admin rights needed).</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CaptionOverlay";
    public const string AutostartArgument = "--autostart";

    public static string CurrentExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "CaptionOverlay.exe");

    private static string Command => $"\"{CurrentExePath}\" {AutostartArgument}";

    public static string? GetRegisteredCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) as string;
    }

    public static bool IsRegistered => GetRegisteredCommand() is not null;

    /// <summary>True if an entry exists but points at a different exe (e.g. the user extracted a newer version elsewhere).</summary>
    public static bool IsStale
    {
        get
        {
            string? command = GetRegisteredCommand();
            return command is not null && !string.Equals(command, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(ValueName, Command);
        }
        else if (key.GetValue(ValueName) is not null)
        {
            key.DeleteValue(ValueName);
        }
    }
}
