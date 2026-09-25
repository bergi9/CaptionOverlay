using NAudio.Wave;

namespace CaptionOverlay.Core.Audio;

public interface IAudioSource : IAsyncDisposable
{
    /// <summary>Format of the samples currently being delivered. Can change after a device switch.</summary>
    WaveFormat? SourceFormat { get; }

    /// <summary>
    /// True for live sources (loopback). Live sources stop delivering data during silence,
    /// so the pipeline injects synthetic silence driven by the wall clock.
    /// </summary>
    bool IsLive { get; }

    /// <summary>Raised on the capture thread with interleaved float samples. Handlers must not block.</summary>
    event Action<AudioChunk>? SamplesAvailable;

    /// <summary>Raised when the source switched devices or stopped unexpectedly (message is loggable, never contains audio).</summary>
    event Action<string>? StatusChanged;

    /// <summary>Raised once a finite source (e.g. a WAV file) has delivered all of its samples.</summary>
    event Action? Completed;

    Task StartAsync(CancellationToken ct);

    Task StopAsync();
}
