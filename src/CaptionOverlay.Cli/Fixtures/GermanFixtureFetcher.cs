using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CaptionOverlay.Core.Audio;
using CaptionOverlay.Core.Segmentation;
using CaptionOverlay.Core.Vad;
using NAudio.Wave;

namespace CaptionOverlay.Cli.Fixtures;

/// <summary>
/// Downloads a small, deterministic selection of German FLEURS clips (pinned dataset revision),
/// checks them with the app's VAD + segmenter and writes <c>tests/fixtures/de</c>. See GERMAN_TEST_FIXTURES_PLAN.md.
/// </summary>
public sealed partial class GermanFixtureFetcher(HttpClient http, TextWriter log)
{
    public const string Dataset = "FluidInference/fleurs-full";
    public const string DefaultRevision = "66b5a017a55ac0dbdc38b313f10f4e141399550f";
    public const string Subset = "de_de";
    public const int GeneratorVersion = 1;

    private const int NormalCount = 6;
    private const double NormalMinSec = 4, NormalMaxSec = 10, LongMinSec = 14, ShortMaxSec = 4.5;

    public async Task<int> RunAsync(string folder, string revision, bool force, string? compositeOut, CancellationToken ct)
    {
        if (!force && FixtureManifest.Verify(folder).Count == 0)
        {
            log.WriteLine($"fixtures up to date ({folder})");
            if (compositeOut is not null)
            {
                WriteComposite(folder, compositeOut);
            }
            return 0;
        }

        string clipsDir = Path.Combine(folder, "clips");
        Directory.CreateDirectory(clipsDir);
        string vadModel = SileroVad.DefaultModelPath;
        if (!File.Exists(vadModel))
        {
            throw new CliException($"Silero VAD model not found at {vadModel}");
        }

        log.WriteLine($"Dataset {Dataset}@{revision[..12]}, subset {Subset}");
        var transcripts = ParseTranscripts(await GetStringAsync(ResolveUrl(revision, $"{Subset}.trans.txt"), ct));
        var tree = await GetTreeAsync(revision, ct);
        if (tree.Count != transcripts.Count)
        {
            throw new CliException($"Dataset changed: {tree.Count} WAV files but {transcripts.Count} transcript lines (expected equal counts).");
        }
        log.WriteLine($"{tree.Count} clips listed; selecting…");

        var candidates = tree.Values
            .Where(e => transcripts.ContainsKey(e.Id))
            .Select(e => new Candidate(e.Id, $"{e.Id}.wav", transcripts[e.Id], (e.Size - 44) / 32000.0, e.Sha256))
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .ToList();
        var usable = candidates.Where(c => IsCleanTranscript(c.Text)).ToList();

        var selected = new List<GermanClip>();
        var used = new HashSet<string>();
        // Long first, then short (both single picks), then the normal bucket; buckets overlap only at 4–4.5 s.
        await PickAsync(GermanClip.Long, 1, usable.Where(c => c.DurationSec > LongMinSec));
        await PickAsync(GermanClip.Short, 1, usable.Where(c => c.DurationSec <= ShortMaxSec).OrderBy(c => c.DurationSec));
        await PickAsync(GermanClip.Normal, NormalCount, usable.Where(c => c.DurationSec is >= NormalMinSec and <= NormalMaxSec));

        async Task PickAsync(string bucket, int count, IEnumerable<Candidate> pool)
        {
            int picked = 0;
            foreach (var c in pool)
            {
                if (picked == count)
                {
                    break;
                }
                if (!used.Add(c.Id))
                {
                    continue;
                }
                var clip = await TryAcceptAsync(c, bucket, clipsDir, revision, vadModel, ct);
                if (clip is not null)
                {
                    selected.Add(clip);
                    picked++;
                }
            }
            if (picked < count)
            {
                throw new CliException($"Only {picked} of {count} clips found for bucket '{bucket}'.");
            }
        }

        selected = [.. selected.OrderBy(c => c.Id, StringComparer.Ordinal)];
        foreach (var stale in Directory.GetFiles(clipsDir, "*.wav").Where(p => selected.All(c => c.File != Path.GetFileName(p))))
        {
            File.Delete(stale);
        }

        WriteText(GermanComposite.TranscriptsPath(folder), JsonSerializer.Serialize(selected, GermanComposite.Json));
        var files = selected.Select(c => $"clips/{c.File}").Append("clips/transcripts.json")
            .Select(rel => new FixtureFile(rel, FixtureManifest.Sha256(Path.Combine(folder, rel)), new FileInfo(Path.Combine(folder, rel)).Length))
            .ToList();
        var manifest = new FixtureManifest("captionoverlay-cli fixtures fetch-de", GeneratorVersion, Dataset, revision, Subset, "CC-BY-4.0",
            [.. selected.Select(c => c.Id)], files);
        WriteText(FixtureManifest.PathIn(folder), JsonSerializer.Serialize(manifest, GermanComposite.Json));
        WriteText(Path.Combine(folder, "ATTRIBUTION.md"), Attribution(revision, selected));

        // Validates the layout rules (merge pair short enough) right away instead of in the tests.
        var composite = GermanComposite.Build(folder);
        log.WriteLine($"Selected {string.Join(", ", selected.Select(c => $"{c.Id} ({c.Bucket}, {c.DurationSec:F2} s)"))}");
        log.WriteLine($"Composite {composite.DurationSec:F1} s, {composite.Cues.Count} cues; clips total {files.Where(f => f.Path.EndsWith(".wav", StringComparison.Ordinal)).Sum(f => f.Size) / 1024.0 / 1024.0:F2} MiB");
        if (compositeOut is not null)
        {
            WriteComposite(folder, compositeOut);
        }
        return 0;
    }

    /// <summary>Downloads a candidate, validates the WAV and checks that the VAD keeps it in one piece.</summary>
    private async Task<GermanClip?> TryAcceptAsync(Candidate c, string bucket, string clipsDir, string revision, string vadModel, CancellationToken ct)
    {
        string path = Path.Combine(clipsDir, c.File);
        if (!File.Exists(path) || FixtureManifest.Sha256(path) != c.Sha256)
        {
            byte[] data = await GetBytesAsync(ResolveUrl(revision, c.File), ct);
            string hash = Convert.ToHexStringLower(SHA256.HashData(data));
            if (hash != c.Sha256)
            {
                throw new CliException($"{c.File}: SHA-256 {hash} does not match the dataset listing ({c.Sha256}).");
            }
            await File.WriteAllBytesAsync(path, data, ct);
        }

        double duration = ValidateWav(path);
        float[] audio = WavIO.ReadMono16k(path);
        var (ok, reason, start, end) = CheckSpeech(audio, bucket, vadModel);
        if (!ok)
        {
            log.WriteLine($"  reject {c.Id} ({bucket}, {duration:F2} s): {reason}");
            File.Delete(path);
            return null;
        }
        log.WriteLine($"  accept {c.Id} ({bucket}, {duration:F2} s, speech {start:F2}–{end:F2} s)");
        return new GermanClip(c.Id, c.File, c.Text, Math.Round(duration, 3), start, end, bucket);
    }

    /// <summary>
    /// With force cuts disabled, a clip must be exactly one utterance (no internal pause ≥ EndSilenceMs).
    /// The speech span is the segmenter's view: utterance start + pre-roll to end − post-roll. The clip is padded
    /// with 1 s of silence on both sides so pre-/post-roll are never clipped by the file edges.
    /// </summary>
    private static (bool Ok, string Reason, double Start, double End) CheckSpeech(float[] audio, string bucket, string vadModel)
    {
        const double pad = 1.0;
        var defaults = new SegmenterOptions();
        int padSamples = (int)(pad * GermanComposite.SampleRate);
        var padded = new float[audio.Length + 2 * padSamples];
        audio.CopyTo(padded, padSamples);

        var run = FixtureSegmentation.Run(padded, GermanComposite.SampleRate, 1, defaults with { MaxUtteranceSec = 600 }, vadModel);
        if (run.Finals.Count != 1)
        {
            return (false, $"{run.Finals.Count} utterances without force cuts ({FixtureSegmentation.Describe(run.Finals)})", 0, 0);
        }
        var f = run.Finals[0];
        double duration = audio.Length / (double)GermanComposite.SampleRate;
        double start = Math.Clamp(f.StartOffset.TotalSeconds + defaults.PreRollMs / 1000.0 - pad, 0, duration);
        double end = Math.Clamp(f.End.TotalSeconds - defaults.PostRollMs / 1000.0 - pad, 0, duration);

        if (bucket == GermanClip.Long)
        {
            var cut = FixtureSegmentation.Run(padded, GermanComposite.SampleRate, 1, defaults, vadModel);
            if (cut.Finals.Count < 2)
            {
                return (false, $"not force-cut with MaxUtteranceSec {defaults.MaxUtteranceSec} s (speech {end - start:F2} s)", 0, 0);
            }
        }
        return (true, "", Math.Round(start, 3), Math.Round(end, 3));
    }

    /// <summary>Fails loudly unless the file is a readable 16 kHz mono PCM16 WAV. Returns its duration.</summary>
    private static double ValidateWav(string path)
    {
        using var reader = new WaveFileReader(path);
        var f = reader.WaveFormat;
        if (f.Encoding != WaveFormatEncoding.Pcm || f.SampleRate != 16000 || f.Channels != 1 || f.BitsPerSample != 16)
        {
            throw new CliException($"{Path.GetFileName(path)}: expected 16 kHz mono PCM16, got {f}");
        }
        if (reader.TotalTime <= TimeSpan.Zero)
        {
            throw new CliException($"{Path.GetFileName(path)}: empty audio");
        }
        return reader.TotalTime.TotalSeconds;
    }

    /// <summary>Skips transcripts with FLEURS artefacts (quotes, stray ".x" endings) that would count as errors for every model.</summary>
    internal static bool IsCleanTranscript(string text) => CleanTranscript().IsMatch(text) && !text.EndsWith(".x", StringComparison.Ordinal);

    internal static Dictionary<string, string> ParseTranscripts(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string raw in content.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            int space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space > 0)
            {
                result[line[..space]] = line[(space + 1)..].Trim();
            }
        }
        return result;
    }

    private static Uri ResolveUrl(string revision, string file) =>
        new($"https://huggingface.co/datasets/{Dataset}/resolve/{revision}/{Subset}/{file}");

    private async Task<Dictionary<string, TreeEntry>> GetTreeAsync(string revision, CancellationToken ct)
    {
        var uri = new Uri($"https://huggingface.co/api/datasets/{Dataset}/tree/{revision}/{Subset}");
        var items = JsonSerializer.Deserialize<List<TreeItem>>(await GetStringAsync(uri, ct), GermanComposite.Json) ?? [];
        var result = new Dictionary<string, TreeEntry>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            string name = Path.GetFileName(item.Path);
            if (item.Type == "file" && name.EndsWith(".wav", StringComparison.Ordinal))
            {
                string sha = item.Lfs?.Oid ?? throw new CliException($"{name}: no SHA-256 (LFS oid) in the dataset listing");
                string id = Path.GetFileNameWithoutExtension(name);
                result[id] = new TreeEntry(id, item.Size, sha);
            }
        }
        return result;
    }

    private async Task<string> GetStringAsync(Uri uri, CancellationToken ct) => Encoding.UTF8.GetString(await GetBytesAsync(uri, ct));

    /// <summary>GET with redirects (Hugging Face → CDN), 60 s timeout per attempt and 3 retries with back-off.</summary>
    private async Task<byte[]> GetBytesAsync(Uri uri, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                using var response = await http.GetAsync(uri, timeout.Token);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new CliException($"GET {uri} → {(int)response.StatusCode} {response.ReasonPhrase} (revision gone or dataset gated?)");
                }
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync(timeout.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested && attempt <= 3)
            {
                var delay = TimeSpan.FromSeconds(2 * attempt);
                log.WriteLine($"  GET {uri} failed ({ex.Message}); retry {attempt}/3 in {delay.TotalSeconds:F0} s");
                await Task.Delay(delay, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new CliException($"GET {uri} failed after 3 retries: {ex.Message}");
            }
        }
    }

    private void WriteComposite(string folder, string output)
    {
        string full = Path.GetFullPath(output);
        if (full.StartsWith(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase))
        {
            throw new CliException("--write-composite must point outside the fixtures folder (derived files are never stored).");
        }
        var composite = GermanComposite.Build(folder);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        WavIO.WritePcm16(full, composite.Samples);
        File.WriteAllText(Path.ChangeExtension(full, ".srt"), composite.ReferenceSrt(), new UTF8Encoding(false));
        log.WriteLine($"Wrote {full} ({composite.DurationSec:F1} s) and {Path.ChangeExtension(full, ".srt")}");
    }

    private static string Attribution(string revision, IReadOnlyList<GermanClip> clips) => string.Create(CultureInfo.InvariantCulture, $"""
        # German test fixtures: attribution

        The audio clips in `clips/` and their transcripts are from **FLEURS**
        (Conneau et al., 2022, "FLEURS: Few-shot Learning Evaluation of Universal Representations of Speech",
        arXiv:2205.12446), by Google, licensed under **CC-BY-4.0** (https://creativecommons.org/licenses/by/4.0/).

        Obtained from the German (`{Subset}`) subset of the Hugging Face dataset
        [`{Dataset}`](https://huggingface.co/datasets/{Dataset}) at revision `{revision}`.

        The clip files are unchanged. The tests derive modified audio from them in memory (clips trimmed to their
        speech, concatenated with silence, resampled to 48 kHz stereo); the silence/noise signal is generated by this
        project. None of the derived audio is stored in the repository.

        Selected clips: {string.Join(", ", clips.Select(c => $"`{c.Id}`"))}.

        Regenerate with `dotnet run --project src/CaptionOverlay.Cli -- fixtures fetch-de`.

        """);

    private static void WriteText(string path, string text) =>
        File.WriteAllText(path, text.ReplaceLineEndings("\n"), new UTF8Encoding(false));

    [GeneratedRegex(@"^[a-zäöüß0-9 .;-]+$")]
    private static partial Regex CleanTranscript();

    private sealed record Candidate(string Id, string File, string Text, double DurationSec, string Sha256);

    private sealed record TreeEntry(string Id, long Size, string Sha256);

    private sealed record TreeItem(string Type, string Path, long Size, TreeLfs? Lfs);

    private sealed record TreeLfs([property: JsonPropertyName("oid")] string Oid);
}
