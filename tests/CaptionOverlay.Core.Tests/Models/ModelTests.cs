using System.Net;
using System.Security.Cryptography;
using CaptionOverlay.Core.Diagnostics;
using CaptionOverlay.Core.Models;

namespace CaptionOverlay.Core.Tests.Models;

public class ModelCatalogTests
{
    [Fact]
    public void Bundled_catalog_is_valid_and_complete()
    {
        var catalog = ModelCatalog.LoadBundled();
        catalog.Version.Should().BeGreaterThanOrEqualTo(1);
        catalog.Models.Select(m => m.Id).Should().Contain(["tiny-q5_1", "base-q5_1", "small-q5_1", "large-v3-turbo-q5_0",
            "large-v3-turbo", "large-v3-turbo-german-q5_0", "large-v3-turbo-german"]);
        catalog.Find("large-v3-turbo-german-q5_0")!.ForceLanguage.Should().Be("de");
        catalog.Find("tiny-q5_1")!.DownloadUri.ToString()
            .Should().Be("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny-q5_1.bin");
    }

    [Theory]
    [InlineData("""{"id":"x","displayName":"X","repo":"evil/../repo","file":"a.bin","sizeBytes":1,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"}""", "invalid repo")]
    [InlineData("""{"id":"x","displayName":"X","repo":"a/b","file":"../a.bin","sizeBytes":1,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"}""", "invalid file name")]
    [InlineData("""{"id":"x","displayName":"X","repo":"a/b","file":"ggml-base.en.bin","sizeBytes":1,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"}""", "English-only")]
    [InlineData("""{"id":"x","displayName":"X","repo":"a/b","file":"ggml-base-encoder.mlmodelc.zip","sizeBytes":1,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"}""", "invalid file name")]
    [InlineData("""{"id":"x","displayName":"X","repo":"a/b","file":"a.bin","sizeBytes":1,"sha256":"<fill>"}""", "invalid sha256")]
    [InlineData("""{"id":"Bad Id","displayName":"X","repo":"a/b","file":"a.bin","sizeBytes":1,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"}""", "invalid id")]
    public void Invalid_remote_entries_are_rejected(string entry, string reason)
    {
        var catalog = ModelCatalog.Parse($$"""{"version":2,"models":[{{entry}}]}""", out var rejected);
        catalog.Models.Should().BeEmpty();
        rejected.Should().ContainSingle().Which.Should().Contain(reason);
    }

    [Fact]
    public async Task Remote_catalog_is_used_only_when_valid_and_not_older()
    {
        var newer = """{"version":99,"models":[{"id":"remote-model","displayName":"R","languages":["multi"],"repo":"a/b","file":"r.bin","sizeBytes":5,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"}]}""";
        using var http = new HttpClient(new StaticHandler(newer));
        (await ModelCatalog.LoadAsync(http, "https://example.test/models.json", ct: TestContext.Current.CancellationToken))
            .Find("remote-model").Should().NotBeNull();

        var older = newer.Replace("\"version\":99", "\"version\":0", StringComparison.Ordinal);
        using var http2 = new HttpClient(new StaticHandler(older));
        (await ModelCatalog.LoadAsync(http2, "https://example.test/models.json", ct: TestContext.Current.CancellationToken))
            .Source.Should().Be("bundled");
    }

    private sealed class StaticHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}

public sealed class ModelDownloaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "co-dl-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _payload;
    private readonly RangeServer _server;

    public ModelDownloaderTests()
    {
        _payload = new byte[3 * 1024 * 1024 + 123];
        new Random(42).NextBytes(_payload);
        _server = new RangeServer(_payload);
    }

    private ModelCatalogEntry Entry(string? sha = null) => new()
    {
        Id = "test-model",
        DisplayName = "Test",
        Repo = "a/b",
        File = "ggml-test.bin",
        SizeBytes = _payload.Length,
        Sha256 = sha ?? Convert.ToHexStringLower(SHA256.HashData(_payload)),
    };

    private ModelDownloader CreateDownloader(ModelStore store) =>
        new(new HttpClient(), store, urlResolver: _ => _server.Url);

    [Fact]
    public async Task Downloads_verifies_and_renames()
    {
        var store = new ModelStore(_root);
        using var dl = CreateDownloader(store);
        var entry = Entry();
        await dl.DownloadAsync(entry, null, TestContext.Current.CancellationToken);
        store.IsInstalled(entry).Should().BeTrue();
        File.Exists(store.GetPartialPath(entry)).Should().BeFalse();
        (await File.ReadAllBytesAsync(store.GetPath(entry), TestContext.Current.CancellationToken)).Should().Equal(_payload);
    }

    [Fact]
    public async Task Resumes_from_partial_file_with_range_request()
    {
        var store = new ModelStore(_root);
        var entry = Entry();
        Directory.CreateDirectory(Path.GetDirectoryName(store.GetPartialPath(entry))!);
        await File.WriteAllBytesAsync(store.GetPartialPath(entry), _payload[..1_000_000], TestContext.Current.CancellationToken);

        using var dl = CreateDownloader(store);
        await dl.DownloadAsync(entry, null, TestContext.Current.CancellationToken);

        _server.LastRangeStart.Should().Be(1_000_000);
        _server.BytesServed.Should().Be(_payload.Length - 1_000_000);
        (await File.ReadAllBytesAsync(store.GetPath(entry), TestContext.Current.CancellationToken)).Should().Equal(_payload);
    }

    [Fact]
    public async Task Restarts_when_server_ignores_range()
    {
        _server.SupportRanges = false;
        var store = new ModelStore(_root);
        var entry = Entry();
        Directory.CreateDirectory(Path.GetDirectoryName(store.GetPartialPath(entry))!);
        await File.WriteAllBytesAsync(store.GetPartialPath(entry), new byte[500_000], TestContext.Current.CancellationToken);

        using var dl = CreateDownloader(store);
        await dl.DownloadAsync(entry, null, TestContext.Current.CancellationToken);
        (await File.ReadAllBytesAsync(store.GetPath(entry), TestContext.Current.CancellationToken)).Should().Equal(_payload);
    }

    [Fact]
    public async Task Tampered_file_is_rejected_and_deleted()
    {
        var store = new ModelStore(_root);
        var entry = Entry(sha: new string('a', 64));
        using var dl = CreateDownloader(store);
        var act = () => dl.DownloadAsync(entry, null, TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<ModelDownloadException>().WithMessage("*SHA-256*");
        store.IsInstalled(entry).Should().BeFalse();
        File.Exists(store.GetPartialPath(entry)).Should().BeFalse();
    }

    [Fact]
    public async Task Pause_keeps_partial_file_and_queue_reports_progress()
    {
        _server.ThrottleBytesPerChunk = 64 * 1024;
        var store = new ModelStore(_root);
        var entry = Entry();
        using var dl = CreateDownloader(store);
        var paused = new TaskCompletionSource<DownloadProgress>();
        dl.ProgressChanged += p =>
        {
            if (p.State == DownloadState.Downloading && p.BytesDownloaded > 200_000)
            {
                dl.Pause(entry.Id);
            }
            if (p.State == DownloadState.Paused)
            {
                paused.TrySetResult(p);
            }
        };
        dl.Enqueue(entry);
        var state = await paused.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        state.BytesDownloaded.Should().BeGreaterThan(0);
        store.PartialBytes(entry).Should().BeGreaterThan(0);
        store.IsInstalled(entry).Should().BeFalse();
    }

    public void Dispose()
    {
        _server.Dispose();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Minimal HTTP server supporting Range requests.</summary>
    private sealed class RangeServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly byte[] _payload;
        private readonly CancellationTokenSource _cts = new();

        public RangeServer(byte[] payload)
        {
            _payload = payload;
            int port = GetFreePort();
            Url = new Uri($"http://127.0.0.1:{port}/model.bin");
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        public Uri Url { get; }

        public bool SupportRanges { get; set; } = true;

        public int ThrottleBytesPerChunk { get; set; }

        public long? LastRangeStart { get; private set; }

        public long BytesServed { get; private set; }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }
                _ = Task.Run(() => ServeAsync(ctx));
            }
        }

        private async Task ServeAsync(HttpListenerContext ctx)
        {
            try
            {
                long start = 0;
                string? range = ctx.Request.Headers["Range"];
                if (SupportRanges && range is not null && range.StartsWith("bytes=", StringComparison.Ordinal))
                {
                    start = long.Parse(range[6..].TrimEnd('-').Split('-')[0], System.Globalization.CultureInfo.InvariantCulture);
                    LastRangeStart = start;
                    ctx.Response.StatusCode = 206;
                    ctx.Response.Headers["Content-Range"] = $"bytes {start}-{_payload.Length - 1}/{_payload.Length}";
                }
                ctx.Response.ContentLength64 = _payload.Length - start;
                BytesServed = 0;
                int chunk = ThrottleBytesPerChunk > 0 ? ThrottleBytesPerChunk : 256 * 1024;
                for (long pos = start; pos < _payload.Length; pos += chunk)
                {
                    int n = (int)Math.Min(chunk, _payload.Length - pos);
                    await ctx.Response.OutputStream.WriteAsync(_payload.AsMemory((int)pos, n), _cts.Token);
                    BytesServed += n;
                    if (ThrottleBytesPerChunk > 0)
                    {
                        await Task.Delay(20, _cts.Token);
                    }
                }
                ctx.Response.Close();
            }
            catch (Exception)
            {
                try
                {
                    ctx.Response.Abort();
                }
                catch (Exception)
                {
                }
            }
        }

        private static int GetFreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Close();
        }
    }
}

public class ModelAdvisorTests
{
    [Fact]
    public void Recommends_turbo_for_big_gpu_and_german_finetune_for_german()
    {
        var hw = new HardwareSummary(8, 16, 32L << 30, [new GpuInfo("RTX", "NVIDIA", 8L << 30, true)]);
        ModelAdvisor.Recommend(hw, "en").ModelId.Should().Be("large-v3-turbo-q5_0");
        ModelAdvisor.Recommend(hw, "de").ModelId.Should().Be("large-v3-turbo-german-q5_0");
    }

    [Fact]
    public void Recommends_small_or_api_without_gpu()
    {
        ModelAdvisor.Recommend(new HardwareSummary(8, 16, 16L << 30, []), "en").ModelId.Should().Be("small-q5_1");
        ModelAdvisor.Recommend(new HardwareSummary(2, 4, 8L << 30, []), "en").SuggestApi.Should().BeTrue();
    }

    [Fact]
    public void Hardware_query_does_not_throw()
    {
        var hw = HardwareInfo.Query();
        hw.PhysicalCores.Should().BeGreaterThan(0);
        hw.TotalMemoryBytes.Should().BeGreaterThan(0);
    }
}
