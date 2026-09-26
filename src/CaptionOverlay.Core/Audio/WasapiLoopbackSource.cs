using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CaptionOverlay.Core.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CaptionOverlay.Core.Audio;

/// <summary>
/// Captures whatever the PC is playing via WASAPI loopback. Follows the system default render
/// device (or a fixed device id) and transparently restarts capture when the device changes.
/// </summary>
public sealed class WasapiLoopbackSource : IAudioSource
{
    private readonly string? _deviceId;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _restartGate = new(1, 1);
    private MMDeviceEnumerator? _enumerator;
    private MMDeviceNotificationClient? _notifications;
    private WasapiRecorder? _capture;
    private MMDevice? _device;
    private WaveFormat? _captureFormat;

    [ThreadStatic]
    private static bool t_threadRaised;

    private bool _running;
    private CancellationToken _ct;

    /// <param name="deviceId">Endpoint id to capture, or null to follow the system default output device.</param>
    public WasapiLoopbackSource(string? deviceId = null, ILogger<WasapiLoopbackSource>? logger = null)
    {
        _deviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public WaveFormat? SourceFormat { get; private set; }

    public bool IsLive => true;

    public string? CurrentDeviceName { get; private set; }

    public event Action<AudioChunk>? SamplesAvailable;

    public event Action<string>? StatusChanged;

    public event Action? Completed
    {
        add { }
        remove { }
    }

    public static IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        if (enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var def) && def is not null)
        {
            defaultId = def.ID;
            def.Dispose();
        }

        var list = new List<AudioDeviceInfo>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                list.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, device.ID == defaultId));
            }
        }
        return list;
    }

    public Task StartAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_running)
            {
                return Task.CompletedTask;
            }
            _running = true;
            _ct = ct;
            _enumerator = new MMDeviceEnumerator();
            _notifications = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
            _notifications.DefaultDeviceChanged += OnDefaultDeviceChanged;
            _notifications.DeviceStateChanged += OnDeviceStateChanged;
            StartCaptureLocked();
        }
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            if (!_running)
            {
                return Task.CompletedTask;
            }
            _running = false;
            if (_notifications is not null)
            {
                _notifications.DefaultDeviceChanged -= OnDefaultDeviceChanged;
                _notifications.DeviceStateChanged -= OnDeviceStateChanged;
                _notifications.Dispose();
                _notifications = null;
            }
            StopCaptureLocked();
            _enumerator?.Dispose();
            _enumerator = null;
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _restartGate.Dispose();
    }

    private void StartCaptureLocked()
    {
        var enumerator = _enumerator ?? throw new InvalidOperationException("Not started.");
        MMDevice? device = null;
        if (_deviceId is not null)
        {
            try
            {
                device = enumerator.GetDevice(_deviceId);
                if (device.State != DeviceState.Active)
                {
                    _logger.LogWarning("Selected output device is not active; falling back to the default device");
                    device.Dispose();
                    device = null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Selected output device not found; falling back to the default device");
                device = null;
            }
        }

        if (device is null && !enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out device))
        {
            device = null;
        }

        if (device is null)
        {
            _logger.LogWarning("No active output device; waiting for one to appear");
            StatusChanged?.Invoke(Loc.Get("Audio_NoDevice"));
            return;
        }

        _device = device;
        CurrentDeviceName = device.FriendlyName;
        _capture = new WasapiRecorderBuilder()
            .WithDevice(device)
            .WithLoopbackCapture()
            .WithSharedMode()
            .Build();
        SourceFormat = _captureFormat = _capture.WaveFormat;
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        _capture.StartRecording();
        _logger.LogInformation("Loopback capture started on {Device} ({Rate} Hz, {Channels} ch, {Bits} bit {Encoding})",
            device.FriendlyName, SourceFormat.SampleRate, SourceFormat.Channels, SourceFormat.BitsPerSample, SourceFormat.Encoding);
        StatusChanged?.Invoke(Loc.Format("Audio_Capturing", device.FriendlyName));
    }

    private void StopCaptureLocked()
    {
        var capture = _capture;
        _capture = null;
        if (capture is not null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            try
            {
                capture.StopRecording();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "StopRecording failed (device probably gone)");
            }
            capture.Dispose();
        }
        _device?.Dispose();
        _device = null;
    }

    private void OnDataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        var format = _captureFormat;
        if (buffer.IsEmpty || format is null)
        {
            return;
        }
        if (!t_threadRaised)
        {
            // The process may run at background priority (local inference); capture must not lose audio.
            t_threadRaised = true;
            ProcessPriority.KeepThreadResponsive();
        }

        // Silent packets may carry garbage; the flag says to treat them as zeros.
        float[] samples = (flags & AudioClientBufferFlags.Silent) != 0
            ? new float[SampleConverter.SampleCount(buffer.Length, format)]
            : SampleConverter.ToFloat(buffer, format);
        SamplesAvailable?.Invoke(new AudioChunk(samples, format.SampleRate, format.Channels));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null || !_running)
        {
            return;
        }
        _logger.LogWarning(e.Exception, "Loopback capture stopped unexpectedly; restarting");
        ScheduleRestart("capture stopped unexpectedly");
    }

    private void OnDefaultDeviceChanged(object? sender, DefaultDeviceChangedEventArgs e)
    {
        // Called on a Windows audio worker thread holding a lock: never block here.
        if (_deviceId is null && e.Flow == DataFlow.Render && e.Role == Role.Multimedia)
        {
            ScheduleRestart("default output device changed");
        }
    }

    private void OnDeviceStateChanged(object? sender, DeviceStateChangedEventArgs e)
    {
        if (_capture is null || (_deviceId is not null && e.DeviceId == _deviceId))
        {
            ScheduleRestart("device state changed");
        }
    }

    private void ScheduleRestart(string reason)
    {
        _ = Task.Run(async () =>
        {
            // Coalesce bursts of notifications (Windows often fires several at once).
            if (!await _restartGate.WaitAsync(0).ConfigureAwait(false))
            {
                return;
            }
            try
            {
                // Devices are often not ready immediately (resume from sleep, driver reload): retry with back-off.
                for (int attempt = 1; ; attempt++)
                {
                    await Task.Delay(attempt == 1 ? 300 : 2000, _ct).ConfigureAwait(false);
                    try
                    {
                        lock (_gate)
                        {
                            if (!_running)
                            {
                                return;
                            }
                            _logger.LogInformation("Restarting loopback capture: {Reason} (attempt {Attempt})", reason, attempt);
                            StopCaptureLocked();
                            StartCaptureLocked();
                        }
                        break;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && attempt < 5)
                    {
                        _logger.LogWarning(ex, "Restarting loopback capture failed; will retry");
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restart loopback capture");
                StatusChanged?.Invoke(Loc.Format("Audio_CaptureError", ex.Message));
            }
            finally
            {
                _restartGate.Release();
            }
        });
    }
}

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);
