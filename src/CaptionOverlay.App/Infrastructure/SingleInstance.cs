namespace CaptionOverlay.App.Infrastructure;

/// <summary>
/// One instance per user session (named mutex). A second launch signals the first one, which brings its
/// settings window forward, then exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\CaptionOverlay.SingleInstance";
    private const string EventName = @"Local\CaptionOverlay.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly RegisteredWaitHandle? _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle activate, Action onActivate)
    {
        _mutex = mutex;
        _activate = activate;
        _registration = ThreadPool.RegisterWaitForSingleObject(activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Returns null if another instance is already running (it has been asked to show itself).</summary>
    public static SingleInstance? TryAcquire(Action onActivate)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool created);
        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        if (!created)
        {
            activate.Set();
            activate.Dispose();
            mutex.Dispose();
            return null;
        }
        return new SingleInstance(mutex, activate, onActivate);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _activate.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }
        _mutex.Dispose();
    }
}
