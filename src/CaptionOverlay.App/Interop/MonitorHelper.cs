using System.Runtime.InteropServices;
using static CaptionOverlay.App.Interop.NativeMethods;

namespace CaptionOverlay.App.Interop;

internal sealed record MonitorInfo(string DeviceName, RECT WorkArea, bool IsPrimary);

/// <summary>Monitor lookups in physical pixels (the overlay is positioned with SetWindowPos, DPI-independent).</summary>
internal static class MonitorHelper
{
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var result = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr handle, IntPtr _, ref RECT _, IntPtr _) =>
        {
            if (Query(handle) is { } info)
            {
                result.Add(info);
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static MonitorInfo Primary() =>
        GetMonitors().FirstOrDefault(m => m.IsPrimary)
        ?? Query(MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY))
        ?? throw new InvalidOperationException("No monitor found.");

    public static MonitorInfo? Find(string? deviceName) =>
        deviceName is null ? null : GetMonitors().FirstOrDefault(m => m.DeviceName == deviceName);

    public static MonitorInfo ForWindow(IntPtr hwnd) =>
        Query(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)) ?? Primary();

    private static MonitorInfo? Query(IntPtr monitor)
    {
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        return GetMonitorInfo(monitor, ref info)
            ? new MonitorInfo(info.szDevice, info.rcWork, (info.dwFlags & MONITORINFOF_PRIMARY) != 0)
            : null;
    }
}
