using System.Windows.Input;
using System.Windows.Interop;
using CaptionOverlay.App.Interop;
using Microsoft.Extensions.Logging;

namespace CaptionOverlay.App.Hotkeys;

public sealed record Hotkey(ModifierKeys Modifiers, Key Key)
{
    /// <summary>Parses "Ctrl+Alt+C" style strings. Returns null for empty/invalid input.</summary>
    public static Hotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var modifiers = ModifierKeys.None;
        Key? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= ModifierKeys.Control;
                    break;
                case "alt":
                    modifiers |= ModifierKeys.Alt;
                    break;
                case "shift":
                    modifiers |= ModifierKeys.Shift;
                    break;
                case "win" or "windows":
                    modifiers |= ModifierKeys.Windows;
                    break;
                default:
                    if (Enum.TryParse<Key>(raw, ignoreCase: true, out var k))
                    {
                        key = k;
                    }
                    else if (raw.Length == 1 && char.IsDigit(raw[0]))
                    {
                        key = Key.D0 + (raw[0] - '0');
                    }
                    else
                    {
                        return null;
                    }
                    break;
            }
        }
        return key is null || modifiers == ModifierKeys.None ? null : new Hotkey(modifiers, key.Value);
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }
        if (Modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }
        if (Modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }
        if (Modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }
        parts.Add(Key is >= Key.D0 and <= Key.D9 ? ((int)(Key - Key.D0)).ToString(System.Globalization.CultureInfo.InvariantCulture) : Key.ToString());
        return string.Join('+', parts);
    }
}

/// <summary>Global hotkeys via RegisterHotKey on a hidden message-only window.</summary>
public sealed class HotkeyService : IDisposable
{
    private readonly ILogger _logger;
    private readonly HwndSource _window;
    private readonly Dictionary<int, Action> _handlers = [];
    private int _nextId = 1;

    public HotkeyService(ILogger logger)
    {
        _logger = logger;
        var parameters = new HwndSourceParameters("CaptionOverlayHotkeys")
        {
            ParentWindow = NativeMethods.HWND_MESSAGE,
            WindowStyle = 0,
        };
        _window = new HwndSource(parameters);
        _window.AddHook(WndProc);
    }

    /// <summary>Registers a hotkey. Returns an error message if it is invalid or already taken by another app.</summary>
    public string? Register(string? text, Action handler)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null; // unbound
        }
        var hotkey = Hotkey.Parse(text);
        if (hotkey is null)
        {
            return $"'{text}' is not a valid hotkey (use e.g. Ctrl+Alt+C).";
        }
        uint mods = NativeMethods.MOD_NOREPEAT;
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Control))
        {
            mods |= NativeMethods.MOD_CONTROL;
        }
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            mods |= NativeMethods.MOD_ALT;
        }
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            mods |= NativeMethods.MOD_SHIFT;
        }
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Windows))
        {
            mods |= NativeMethods.MOD_WIN;
        }

        int id = _nextId++;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(hotkey.Key);
        if (!NativeMethods.RegisterHotKey(_window.Handle, id, mods, vk))
        {
            _logger.LogWarning("Hotkey {Hotkey} could not be registered (already in use?)", hotkey);
            return $"{hotkey} is already used by another application.";
        }
        _handlers[id] = handler;
        _logger.LogInformation("Registered hotkey {Hotkey}", hotkey);
        return null;
    }

    public void UnregisterAll()
    {
        foreach (int id in _handlers.Keys)
        {
            NativeMethods.UnregisterHotKey(_window.Handle, id);
        }
        _handlers.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && _handlers.TryGetValue(wParam.ToInt32(), out var handler))
        {
            handled = true;
            handler();
        }
        return IntPtr.Zero;
    }
}
