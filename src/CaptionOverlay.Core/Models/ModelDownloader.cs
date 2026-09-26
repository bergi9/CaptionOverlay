using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaptionOverlay.Core.Models;

public enum DownloadState
{
    Queued,
    Downloading,
    Verifying,
    Paused,
    Completed,
    Failed,
    Canceled,
}

public sealed record DownloadProgress(
    string ModelId,
    DownloadState State,
    long BytesDownloaded,
    long TotalBytes,
    double BytesPerSecond,
    TimeSpan? Eta,
    string? Error = null)
{
    public double Fraction => TotalBytes > 0 ? Math.Clamp((double)BytesDownloaded / TotalBytes, 0, 1) : 0;
}

public sealed class ModelDownloadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Downloads catalog models one at a time: resumable (<c>Range</c>), SHA-256 verified, atomic rename,
/// free-space check. Pause keeps the <c>.part</c> file; cancel deletes it.
/// </summary>
public sealed class ModelDownloader : IDisposable
{
    private readonly HttpClient _http;
    private readonly ModelStore _store;
    private readonly ILogger _logger;
    private readonly Func<ModelCatalogEntry, Uri> _urlResolver;
    private readonly Lock _gate = new();
    private readonly LinkedList<ModelCatalogEntry> _queue = new();
    private readonly Dictionary<string, DownloadProgress> _states = [];
    private (ModelCatalogEntry Entry, CancellationTokenSource Cts, bool DeleteOnCancel)? _current;
    private Task? _pump;

    public ModelDownloader(HttpClient http, ModelStore store, ILogger? logger = null, Func<ModelCatalogEntry, Uri>? urlResolver = null)
    {
        _http = http;
        _store = store;
        _logger = logger ?? NullLogger.Instance;
        _urlResolver = urlResolver ?? (e => e.DownloadUri);
    }

    /// <summary>Raised from a background thread.</summary>
    public event Action<DownloadProgress>? ProgressChanged;

    public DownloadProgress? GetState(string modelId)
    {
        lock (_gate)
        {
            return _states.GetValueOrDefault(modelId);
        }
    }

    public void Enqueue(ModelCatalogEntry entry)
    {
        lock (_gate)
        {
            if (_current?.Entry.Id == entry.Id || _queue.Any(e => e.Id == entry.Id))
            {
                return;
            }
            _queue.AddLast(entry);
            Report(new DownloadProgress(entry.Id, DownloadState.Queued, _store.PartialBytes(entry), entry.SizeBytes, 0, null));
            if (_pump is null || _pump.IsCompleted)
            {
                _pump = Task.Run(PumpAsync);
            }
        }
    }

    /// <summary>Stops the download but keeps the partial file for a later resume.</summary>
    public void Pause(string modelId) => Stop(modelId, delete: false);

    /// <summary>Stops the download and deletes the partial file.</summary>
    public void Cancel(string modelId) => Stop(modelId, delete: true);

    public void Dispose()
    {
        lock (_gate)
        {
            _queue.Clear();
            _current?.Cts.Cancel();
        }
    }

    /// <summary>Downloads (resuming if possible) and verifies one model. Throws on failure.</summary>
    public async Task DownloadAsync(ModelCatalogEntry entry, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        string finalPath = _store.GetPath(entry);
        string partPath = _store.GetPartialPath(entry);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        long existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        if (existing > entry.SizeBytes)
        {
            File.Delete(partPath);
            existing = 0;
        }
        EnsureFreeSpace(finalPath, entry.SizeBytes - existing);

        if (existing < entry.SizeBytes)
        {
            existing = await FetchAsync(entry, partPath, existing, progress, ct).ConfigureAwait(false);
        }

        progress?.Report(new DownloadProgress(entry.Id, DownloadState.Verifying, existing, entry.SizeBytes, 0, null));
        string hash = await ComputeSha256Async(partPath, ct).ConfigureAwait(false);
        if (!string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partPath);
            throw new ModelDownloadException(Loc.Get("Download_HashMismatch"));
        }

        File.Move(partPath, finalPath, overwrite: true);
        _logger.LogInformation("Model {Id} downloaded and verified", entry.Id);
        progress?.Report(new DownloadProgress(entry.Id, DownloadState.Completed, entry.SizeBytes, entry.SizeBytes, 0, TimeSpan.Zero));
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous);
        byte[] hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private async Task<long> FetchAsync(ModelCatalogEntry entry, string partPath, long existing, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _urlResolver(entry));
        if (existing > 0)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            return existing; // already complete; verification decides
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new ModelDownloadException(Loc.Format("Download_HttpFailed", (int)response.StatusCode, response.ReasonPhrase));
        }

        bool resumed = response.StatusCode == HttpStatusCode.PartialContent;
        if (!resumed)
        {
            existing = 0; // server ignored the range: start over
        }
        _logger.LogInformation("Downloading model {Id} ({Mode} at {Offset} bytes)", entry.Id, resumed ? "resuming" : "starting", existing);

        await using var file = new FileStream(partPath, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, FileOptions.Asynchronous);
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var buffer = new byte[1 << 16];
        long total = existing;
        var clock = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        long windowStartBytes = total;
        var windowStart = TimeSpan.Zero;
        double speed = 0;
        int read;
        while ((read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            total += read;
            var now = clock.Elapsed;
            if (now - lastReport >= TimeSpan.FromMilliseconds(250))
            {
                double seconds = (now - windowStart).TotalSeconds;
                if (seconds >= 1)
                {
                    double instant = (total - windowStartBytes) / seconds;
                    speed = speed == 0 ? instant : speed * 0.6 + instant * 0.4;
                    windowStart = now;
                    windowStartBytes = total;
                }
                TimeSpan? eta = speed > 0 ? TimeSpan.FromSeconds((entry.SizeBytes - total) / speed) : null;
                progress?.Report(new DownloadProgress(entry.Id, DownloadState.Downloading, total, entry.SizeBytes, speed, eta));
                lastReport = now;
            }
        }
        await file.FlushAsync(ct).ConfigureAwait(false);
        return total;
    }

    private static void EnsureFreeSpace(string path, long needed)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root))
        {
            return;
        }
        try
        {
            var drive = new DriveInfo(root);
            long required = (long)(needed * 1.1);
            if (drive.IsReady && drive.AvailableFreeSpace < required)
            {
                throw new ModelDownloadException(
                    Loc.Format("Download_DiskSpace", required / (1024 * 1024), drive.AvailableFreeSpace / (1024 * 1024), root));
            }
        }
        catch (ArgumentException)
        {
            // UNC or unusual path: skip the check.
        }
    }

    private void Stop(string modelId, bool delete)
    {
        ModelCatalogEntry? removed = null;
        lock (_gate)
        {
            var node = _queue.First;
            while (node is not null)
            {
                if (node.Value.Id == modelId)
                {
                    removed = node.Value;
                    _queue.Remove(node);
                    break;
                }
                node = node.Next;
            }
            if (_current is { } current && current.Entry.Id == modelId)
            {
                _current = current with { DeleteOnCancel = delete };
                current.Cts.Cancel();
                return;
            }
        }
        if (removed is not null)
        {
            if (delete)
            {
                TryDelete(_store.GetPartialPath(removed));
            }
            Report(new DownloadProgress(modelId, delete ? DownloadState.Canceled : DownloadState.Paused,
                _store.PartialBytes(removed), removed.SizeBytes, 0, null));
        }
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            ModelCatalogEntry entry;
            CancellationTokenSource cts;
            lock (_gate)
            {
                if (_queue.First is null)
                {
                    _current = null;
                    return;
                }
                entry = _queue.First.Value;
                _queue.RemoveFirst();
                cts = new CancellationTokenSource();
                _current = (entry, cts, false);
            }

            var progress = new Progress(this);
            try
            {
                await DownloadAsync(entry, progress, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                bool delete;
                lock (_gate)
                {
                    delete = _current?.DeleteOnCancel ?? false;
                }
                if (delete)
                {
                    TryDelete(_store.GetPartialPath(entry));
                }
                Report(new DownloadProgress(entry.Id, delete ? DownloadState.Canceled : DownloadState.Paused,
                    _store.PartialBytes(entry), entry.SizeBytes, 0, null));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Model download {Id} failed", entry.Id);
                string message = ex switch
                {
                    ModelDownloadException => ex.Message,
                    HttpRequestException => Loc.Format("Common_NetworkError", ex.Message),
                    IOException => Loc.Format("Download_DiskError", ex.Message),
                    _ => ex.Message,
                };
                Report(new DownloadProgress(entry.Id, DownloadState.Failed, _store.PartialBytes(entry), entry.SizeBytes, 0, null, message));
            }
            finally
            {
                cts.Dispose();
            }
        }
    }

    private void Report(DownloadProgress progress)
    {
        lock (_gate)
        {
            _states[progress.ModelId] = progress;
        }
        ProgressChanged?.Invoke(progress);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private sealed class Progress(ModelDownloader owner) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => owner.Report(value);
    }
}
