using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaptionOverlay.Core.Diagnostics;

/// <summary>
/// Lets local inference yield to what the user is watching: the whole process runs at below-normal CPU priority and
/// below-normal GPU scheduling priority (whisper.cpp's worker threads and its Vulkan queue cannot be reached one by one).
/// The GPU part had no measurable effect on an RTX 4090 with Vulkan: a running pass is not interrupted (ADR-023).
/// Threads that must stay responsive (UI, audio capture) raise themselves with <see cref="KeepThreadResponsive"/>.
/// </summary>
public static partial class ProcessPriority
{
    private const int D3DKMT_SCHEDULINGPRIORITYCLASS_BELOW_NORMAL = 1;
    private const int D3DKMT_SCHEDULINGPRIORITYCLASS_NORMAL = 2;

    public static bool IsBackground { get; private set; }

    /// <summary>Switches between background priority (local models) and normal priority. Failures are logged, never thrown.</summary>
    public static void SetBackground(bool background, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        if (background == IsBackground)
        {
            return;
        }
        IsBackground = background;
        try
        {
            using var process = Process.GetCurrentProcess();
            process.PriorityClass = background ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogDebug(ex, "Could not change the CPU priority");
        }
        try
        {
            int status = D3DKMTSetProcessSchedulingPriorityClass(GetCurrentProcess(),
                background ? D3DKMT_SCHEDULINGPRIORITYCLASS_BELOW_NORMAL : D3DKMT_SCHEDULINGPRIORITYCLASS_NORMAL);
            if (status != 0)
            {
                logger.LogDebug("Could not change the GPU scheduling priority (NTSTATUS 0x{Status:X8})", status);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            logger.LogDebug(ex, "GPU scheduling priority not available");
        }
        logger.LogInformation("Process priority: {Priority}", background ? "below normal (CPU and GPU), local inference yields to other apps" : "normal");
    }

    /// <summary>
    /// Raises the calling thread so it keeps the priority of a normal app's thread even while the process runs in the
    /// background (below-normal class base 6 + highest 2 = 8, the normal base).
    /// </summary>
    public static void KeepThreadResponsive()
    {
        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
        }
        catch (ThreadStateException)
        {
        }
    }

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTSetProcessSchedulingPriorityClass(IntPtr process, int priorityClass);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();
}
