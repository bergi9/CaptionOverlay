# Task: German test fixtures (FLEURS) + tests

> For a **new Claude Code session**. The app from `PLAN.md` is already implemented.
> This task adds German audio fixtures with known transcripts and timings, and tests that use them.
> No Python. Everything runs through the existing .NET solution.

---

## 0. Before writing any code

1. Read `PLAN.md` and skim the current solution: `CaptionOverlay.Core`, `CaptionOverlay.Cli`, `tests/`.
2. Find what already exists and **reuse it**; don't duplicate:
   - a WAV/file-based audio source (anything that feeds a WAV into the pipeline)
   - the CLI command structure
   - existing test fixtures, traits, helpers
   - the SRT writer, segmenter, VAD, transcriber classes and their real names
3. Where this document names a class or command that already exists under another name, use the existing one. Note any gaps in `docs/decisions.md`.

---

## 1. Source data

**Google FLEURS, German test split**, repackaged as plain files in the Hugging Face dataset `FluidInference/fleurs-full`.

- License: **CC-BY-4.0**. Files may be committed or redistributed with attribution (see section 6).
- Folder `de_de/` contains:
  - `de_de.trans.txt`: one line per clip, format `file_id transcription` (first whitespace-separated token = id, rest of line = text)
  - `de_de_XXXX.wav`: 16 kHz mono WAV (862 clips, see below)
- URL pattern (no auth):
  `https://huggingface.co/datasets/FluidInference/fleurs-full/resolve/{revision}/de_de/{file}`
- **Pin the revision** to commit `66b5a017a55ac0dbdc38b313f10f4e141399550f` (not `main`), so fixtures never change underneath the tests.

### 1.1 Verified facts (checked 2026-09-25, don't re-derive, but fail loudly if they stop holding)
- The pinned revision still resolves (HTTP 200, redirects to a CDN). `main` has since moved to `1cca811b…`; keep the pin.
- Dataset is public, not gated, license tag `cc-by-4.0`.
- `de_de/` has **862** WAV files plus `de_de.trans.txt` (862 lines). The folder is not necessarily just the FLEURS *test* split; don't claim that in the attribution, just say "German (de_de) subset".
- `file_id` has **no** `.wav` extension (`de_de_0000 für die besten aussichten …`). File name = `{file_id}.wav`.
- WAV: RIFF, PCM 16-bit, mono, 16 000 Hz, plain 44-byte header (e.g. `de_de_0000.wav` = 574 124 bytes ≈ 17.9 s).
- Transcripts are UTF-8, **already normalized**: lowercase, almost no punctuation (occasional `;`), hyphens replaced by spaces (`t rex`). Umlauts/ß are present in 734 of 862 lines. Numbers are written as **digits** (164 lines, e.g. `1963`, `70`), not spelled out, so Whisper's digit output mostly matches.
- File sizes are available without downloading: `GET https://huggingface.co/api/datasets/FluidInference/fleurs-full/tree/{revision}/de_de` returns every file with `size` (the listing contains all 862 files in one response). For 16-bit mono 16 kHz, duration = `(size − 44) / 32000` s. Use this for clip selection (2.1) instead of downloading candidates, then confirm the real duration from the WAV header after downloading the 8 chosen clips.

---

## 2. What gets built

All generated files go to `tests/fixtures/de/`.

**Only the 8 original clips are stored as audio (≈ 2 MB total).** Everything else is either small text or is *derived deterministically in memory at test time* from those clips, so nothing is stored twice.

| File (committed) | Contents | Purpose |
|---|---|---|
| `clips/de_de_XXXX.wav` | 8 selected FLEURS clips, **byte-for-byte unchanged** (16 kHz mono PCM16) | per-clip WER; source for everything derived |
| `clips/transcripts.json` | `{ id, file, text, durationSec, speechStartSec, speechEndSec, bucket }[]` | reference text + effective speech span (2.1) |
| `manifest.json` | dataset revision, selected ids, per-file SHA-256 + size, generator version | reproducibility + up-to-date check |
| `ATTRIBUTION.md` | FLEURS credit + license | CC-BY requirement |

| Derived at test time (never stored) | Built by | Purpose |
|---|---|---|
| 16 kHz composite + reference cues (`{ index, sourceId, startSec, endSec, speechStartSec, speechEndSec, text, gapBeforeSec, expectMerged }`) | `GermanComposite.Build()` in the test project, from `transcripts.json` + the layout in 2.2 | segmenter, SRT timing, end-to-end |
| 48 kHz float stereo composite | same helper: upsample the 16 kHz composite (`WdlResampler`), duplicate to both channels | exercises the resampler like real loopback audio (would be ~27 MB on disk) |
| Reference SRT text | same helper, via `SrtWriter(TextWriter)` into a `StringWriter` | SRT comparison |
| 20 s silence (10 s digital zero + 10 s white noise ≈ −60 dBFS, fixed RNG seed) | same helper | hallucination filter, VAD false positives |

Building these takes milliseconds, and it removes any risk of derived files drifting out of sync with the clips. For listening by hand, add an optional CLI switch `fixtures fetch-de --write-composite <path>` that writes the 16 kHz composite to a path outside the repo. For that, the composer lives in a place both CLI and tests can use: e.g. `CaptionOverlay.Cli/Fixtures/GermanComposite.cs`, with the test project linking that file (`<Compile Include="..\..\src\CaptionOverlay.Cli\Fixtures\GermanComposite.cs" Link=… />`). Do not put it in Core; it's test tooling.

**Why not downsample further?** The clips are already 16 kHz mono 16-bit, which is exactly what Whisper and Silero consume. Going to 8 kHz would only halve ~2 MB, but it removes the 4–8 kHz band (s, f, sch, z) and makes the resampler upsample. The WER numbers would then measure the damaged fixture, not the app. 8-bit samples or lossy codecs (Opus/MP3) have the same problem, and lossless FLAC would need an extra NuGet dependency to save ~1 MB. Keep the originals unchanged.

### 2.1 Clip selection
Pick clips **deterministically** and record the chosen ids in `manifest.json` so later runs reuse exactly the same ones:
- 6 clips of **4–10 s** (normal sentences)
- 1 clip of **> 14 s** (must trigger the segmenter's max-length force cut; default `maxUtteranceSec` is 12 s, so read the actual configured value)
- 1 **shortest-available** clip (short utterance handling). **The dataset has no clip under 3 s**: the shortest file is `de_de_0739` at 3.60 s (then `de_de_0353` at 3.96 s; only 12 clips are < 4.5 s). Bucket = "file duration ≤ 4.5 s, the shortest one that passes the VAD check". Very short utterances (< 400 ms, discarded by the segmenter) are already covered by the synthetic `UtteranceSegmenterTests`.
- Prefer clips whose transcripts contain umlauts/ß and at least one number, to catch text-normalization issues.
- Selection rule: iterate ids in ascending order, take the first clips matching each duration bucket (short bucket: ascending by duration). Duration comes from the tree API sizes (1.1); only the chosen clips are downloaded.
- Expected size: the first matches in id order (`de_de_0000` 17.9 s long; `0004`, `0005`, `0010`, `0015`, `0016`, `0017` normal, 5–10 s) plus `de_de_0739` total ≈ **2.1 MB**. Bucket availability: 313 clips of 4–10 s, 296 clips > 14 s.
- **As implemented (ADR-014):** clips whose transcript has FLEURS artefacts (trailing `.x`, quotes) are skipped, and the VAD check rejected `0000` (only 9.6 s of speech), `0007` and `0009` (internal pauses). Result: long `de_de_0011` (16.9 s), normal `0004 0005 0015 0016 0017 0019`, short `de_de_0739`; 2.08 MiB.
- **Reject clips the VAD would split on its own:** after download, run the app's `SileroVad` + `UtteranceSegmenter` (default options) on each candidate. A normal (4–10 s) or short clip must yield exactly **one** final; the long clip must yield no split *before* the forced cut (no internal pause ≥ `EndSilenceMs`). If a clip fails, take the next id in the bucket. This keeps the "one cue = one utterance" assumptions in section 4 true.
- For every clip, compute and store the **effective speech span** (`speechStartSec`/`speechEndSec`, from the segmenter/VAD run above, or an energy threshold). FLEURS clips carry roughly 0.3–1 s of their own leading/trailing silence.

### 2.2 Composite layout
```
[1.5 s silence] clip1 [2.0 s] clip2 [1.0 s] clip3 [3.0 s] clip4(long, >14 s) [1.5 s]
clip5 [0.3 s] clip6 [2.0 s] clip7(short) [1.5 s] clip8 [2.0 s silence]
```
- **Trim each clip to its effective speech span (2.1) plus 50 ms on both sides before inserting it.** Otherwise a clip's own trailing silence + the gap + the next clip's leading silence decide the real pause, not the gap in this layout. Without trimming, the 0.3 s "merge" gap would really be ~1–2 s and would not merge.
- Gaps ≥ 1.0 s must produce **separate** utterances (`SegmenterOptions.EndSilenceMs` = 600 ms by default).
- The **0.3 s gap** (clip5 → clip6) is shorter than `EndSilenceMs`, so those two are **expected to merge** into one utterance. The reference JSON marks this pair with `"expectMerged": true`.
- The long clip is expected to be **force-cut** into ≥ 2 utterances (`SegmenterOptions.MaxUtteranceSec` = 12 s; note the limit counts the whole utterance buffer including the 300 ms pre-roll).
- `GermanComposite.Build()` records exact sample-accurate start/end of each (trimmed) clip in the reference cues it returns. The originals in `clips/` stay untrimmed; trimming happens only in memory.

---

## 3. Implementation

### 3.1 CLI command
Add to `CaptionOverlay.Cli` (adapt to the existing command framework):

```
fixtures fetch-de   [--out tests/fixtures/de] [--revision <hash>] [--force] [--write-composite <path.wav>]
```

Steps:
1. If `manifest.json` exists and every listed file exists with the right SHA-256 → print "fixtures up to date" and exit 0 (unless `--force`).
2. Download `de_de.trans.txt` (pinned revision), parse it. Get file sizes from the tree API (1.1).
3. Pick candidates per bucket (2.1) from the sizes, download them to `clips/`.
4. Validate every WAV: 16 kHz, mono, PCM16, readable, duration > 0. Fail loudly otherwise.
5. Run the VAD check and compute effective speech spans (2.1); reject and replace failing clips (delete the rejected file).
6. Write `clips/transcripts.json`, `manifest.json` (revision, selected ids, per-file SHA-256 + size, generator version) and `ATTRIBUTION.md`.
7. Optional `--write-composite`: build the 16 kHz composite with `GermanComposite.Build()` and write it (`WavIO.WritePcm16`) plus a `.srt` next to it, **outside** `tests/fixtures`, for listening.

Networking: `HttpClient`, follow redirects (HF `resolve` URLs redirect to a CDN), timeout 60 s, 3 retries with backoff, clear error message on failure. `ModelDownloader` is **not** general-purpose (it is tied to `ModelCatalogEntry` + `ModelStore`), so use a small `HttpClient` helper; the clips are tiny, so resume support isn't needed. Verify each clip's SHA-256 against the value recorded in `manifest.json` on later runs.

### 3.0 Existing code to reuse (as implemented)
| This document says | Actual name in the solution |
|---|---|
| WAV file source | `Core/Audio/WavFileAudioSource(path, realtime: false)`, plus `WavIO.ReadMono16k` / `WavIO.WritePcm16` |
| CLI framework | hand-written: `Cli/CliArgs.cs` (command + positionals + `--flags`) and `Cli/Commands.cs` (`switch` on the command). `fixtures fetch-de` parses as command `fixtures`, positional `fetch-de` |
| VAD / segmenter | `SileroVad(SileroVad.DefaultModelPath)` (tests: `Fixtures.SileroModel`), `UtteranceSegmenter(SegmenterOptions)`, `AudioFrontEnd` (512-sample framing) |
| Pipeline | `CaptionPipeline.StartAsync(PipelineConfig, TranscriberFactory)`, `WaitForSourceCompletionAsync()`, `StopAsync(drain: true)`, `LineCommitted` event |
| Fake transcriber | `tests/.../TestUtil.cs` → `FakeTranscriber(delay, text: (samples, opts) => …)`; its text callback gets only the samples, so add an offset-aware `ReferenceTranscriber` (section 5) instead of stretching it |
| Test fixture helper | `tests/.../TestUtil.cs` → `Fixtures.Path(name)`, `Fixtures.Labels(...)`; fixtures are copied to the test output via `<None Include="..\fixtures\**\*" …>` (subfolders are included automatically) |
| Model trait | `[Trait("Category", "RequiresModel")]` already exists; `LocalWhisperIntegrationTests.FindTinyModel()` resolves via env var **or** the installed `ModelStore` |
| Hallucination filter | `HallucinationFilter.Apply(FilterInput)`, `HallucinationFilter.Rms` |

### 3.2 Commit or download?
**Commit the fixtures** (≈ 2.1 MB of WAV + a few KB of JSON/Markdown) so tests and CI run offline and identically. `.gitattributes` already marks `*.wav` as binary.
- If the total ever exceeds 5 MB (e.g. more clips are added later), gitignore `tests/fixtures/de/clips/*.wav`, commit only `manifest.json` + `transcripts.json`, and have CI run `fixtures fetch-de` with a cache keyed on the manifest hash.
- Record the choice (and the "derive in memory, don't downsample" reasoning from section 2) in `docs/decisions.md`.

---

## 4. Tests to add

Put them in `CaptionOverlay.Core.Tests` (or a new `CaptionOverlay.Integration.Tests` project if model-dependent tests are better isolated). Use a shared fixture helper that locates `tests/fixtures/de/` and fails with a message pointing to `fixtures fetch-de` if files are missing or hashes mismatch.

### 4.1 No model required (run in CI)

| Test | Input | Assert |
|---|---|---|
| `Fixtures_ManifestHashesMatch` | manifest | every file present, SHA-256 matches |
| `Segmenter_FindsUtteranceBoundaries` | `GermanComposite.Build()` 16 kHz samples → VAD → segmenter | each reference cue (merged pair treated as one) matches exactly one final utterance; `final.StartOffset + PreRollMs` within **±200 ms** of the cue's speech start, `final.End − PostRollMs` within **±200 ms** of its speech end. (The segmenter trims trailing silence to `PostRollMs` = 200 ms, so `endSilenceMs` is *not* part of the end tolerance.) |
| `Segmenter_MergesShortGap` | same | clip5+clip6 produce one utterance |
| `Segmenter_ForceCutsLongUtterance` | same | long clip produces ≥ 2 finals, none longer than `maxUtteranceSec` + 0.1 s, no audio lost (sum of durations ≈ clip duration ± pre-roll) |
| `Segmenter_SilenceProducesNothing` | generated 20 s silence | zero utterances |
| `Resampler_48kStereoMatches16kMono` | 16 kHz composite, plus the same audio upsampled in memory to 48 kHz float stereo and fed through `AudioFrontEnd` (i.e. the app's `Resampler`) | the segmenter yields the same utterance count and boundaries within ±50 ms of the 16 kHz run |
| `Pipeline_FakeTranscriber_SrtTiming` | 16 kHz composite through the full pipeline (write it to a temp WAV and use `WavFileAudioSource(path, realtime: false)`, or add a small in-memory `IAudioSource`) with `ReferenceTranscriber` (section 5) | exported SRT has the same cue count (after merge/split rules) and cue starts within **±500 ms** of the reference SRT from `GermanComposite` |

Note on tolerances: all boundary comparisons use the stored effective speech spans (`speechStartSec`/`speechEndSec`, section 2.1), not the raw clip edges. Don't loosen tolerances silently; document any change.

### 4.2 Model required (skipped unless the model is available)
Trait `[Trait("Category", "RequiresModel")]`. The German tests run as an **`[Theory]` over models**, so every available model is measured in one run and each gets its own threshold:

| Model id (catalog) | Aggregate WER threshold (start value) | Notes |
|---|---|---|
| `large-v3-turbo-german-q5_0` | ≤ 12 % | the model the app recommends for German |
| `large-v3-turbo-q5_0` | ≤ 20 % | multilingual comparison |
| `small-q5_1` | ≤ 40 % | **required**: the weak-PC option must be measured on German too. Already installed on the dev machine |
| `base-q5_1`, `tiny-q5_1` | log only | too weak for German; record WER but don't assert |

Model resolution per id: `CAPTIONOVERLAY_TEST_MODEL_<ID>` env var (path) if set, otherwise the installed file from `ModelStore` + the bundled `ModelCatalog` (same approach as `FindTinyModel()`). If neither exists → `Assert.Skip` for that theory row (skip, not fail). Keep `CAPTIONOVERLAY_TEST_MODEL` working as a single-model override for a path outside the store.

| Test (theory over model ids) | Assert |
|---|---|
| `LocalWhisper_German_PerClipWer` | transcribe each `clips/*.wav` with language forced to `de`; compute WER vs reference; log per-clip WER and inference time (`TestContext.Current.SendDiagnosticMessage`) |
| `LocalWhisper_German_AggregateWer` | aggregate WER ≤ the threshold above. Tune after the first real run and record per-model baselines (per-clip + aggregate WER, RTF, runtime) in `tests/fixtures/de/wer-baseline.json` |
| `LocalWhisper_SilenceNoHallucination` | generated 20 s silence through transcriber **+ `HallucinationFilter`** → no committed text |
| `Pipeline_Local_EndToEnd_Composite` | real pipeline on the in-memory 48 kHz stereo composite (non-realtime file source) → committed lines exist for every reference cue; aggregate WER within that model's baseline + 5 points; SRT cue starts within ±500 ms |

For the German fine-tune, `ForceLanguage = "de"` comes from the catalog; pass it through `LocalWhisperOptions.ForcedLanguage` like the app does.

**WER normalization** (implement once, unit-test it): Unicode NFC; lowercase; **hyphens and slashes → space** (reference has `t rex`, Whisper writes `T-Rex`); strip all other punctuation (incl. `;`, quotes, `„“`); collapse whitespace; keep umlauts/ß as-is. FLEURS writes numbers as **digits** (1.1), so Whisper's usual digit output matches. Don't build a number normalizer; just note any remaining mismatches (e.g. `70` vs `siebzig`, `3,5` vs `3.5`) in the summary.

API-mode tests: **not part of this task** (costs money, needs keys). Optionally add one manual-only test behind `CAPTIONOVERLAY_TEST_API_KEY` that transcribes a single clip.

---

## 5. Fake transcriber for timing tests
Add `ReferenceTranscriber : ITranscriber` in the test project (next to the existing `FakeTranscriber`):
- constructed with the reference cues **and the 16 kHz composite samples** from `GermanComposite.Build()`
- **Caveat:** `ITranscriber.TranscribeAsync(float[] samples, …)` receives only the audio, not the utterance's start offset. On the 16 kHz mono path the pipeline passes samples through bit-exactly (`Resampler` short-circuits at 16 kHz; `AudioFrontEnd` only re-frames), so locate the utterance by **exact sample match**: find the first 256 consecutive non-zero samples of the job inside the composite. Offset = match index / 16000. Then return the concatenated reference text of all cues overlapping `[offset, offset + samples.Length / 16000]`. Fail the test loudly if no match is found.
- Use it only with the 16 kHz composite (the 48 kHz path is not bit-exact).
- Configurable artificial delay (to also exercise the backpressure path: e.g. 0 ms and 2× real time).
- Partial jobs (`opts.IsPartial`) may return the same text; the SRT only contains finals.

---

## 6. Attribution
`tests/fixtures/de/ATTRIBUTION.md`:
- Audio and transcripts from **FLEURS** (Conneau et al., 2022, arXiv:2205.12446), Google, licensed **CC-BY-4.0**, obtained from the German (`de_de`) subset of the Hugging Face dataset `FluidInference/fleurs-full` at revision `<hash>`.
- Composite files are derived from these clips (trimmed and concatenated); state that they were modified. The silence file is generated by this project.
- List the selected clip ids.

Do **not** add this to `packaging/THIRD_PARTY_NOTICES.txt`: that file ships inside the release zip, and the fixtures don't. Mention the FLEURS attribution in the repo `README.md` (layout table, `tests/` row) instead.

---

## 7. CI
- CI runs the no-model tests on every push.
- If fixtures are committed: nothing extra.
- If fixtures are downloaded: add a step `dotnet run --project src/CaptionOverlay.Cli -- fixtures fetch-de` with `actions/cache` keyed on `hashFiles('tests/fixtures/de/manifest.json')`.
- `RequiresModel` tests are excluded in CI. The solution uses xUnit v3 on **Microsoft.Testing.Platform** (see `global.json`), so the VSTest syntax `--filter "Category!=RequiresModel"` does **not** apply. Use `dotnet test --solution CaptionOverlay.slnx -- --filter-not-trait "Category=RequiresModel"` (confirm the exact option with `dotnet test --project tests/CaptionOverlay.Core.Tests -- --help`). They also skip themselves when no model is installed, so CI is safe either way.
- The Silero VAD model ships in the repo (`src/CaptionOverlay.Core/Assets`), so segmenter tests need no download.

---

## 8. Acceptance criteria
1. `fixtures fetch-de` on a clean checkout produces all files in section 2; running it again reports "up to date" without downloading.
2. The no-model tests (section 7 filter) pass locally and in CI; the existing 91 tests still pass.
3. With `small-q5_1` and `large-v3-turbo-german-q5_0` installed (download the German one with `captionoverlay-cli download large-v3-turbo-german-q5_0`, 574 MB), all `RequiresModel` rows for both pass, and `wer-baseline.json` has per-clip and aggregate WER for each measured model.
4. Deliberately breaking the segmenter (e.g. setting `endSilenceMs` to 5000 in a local experiment) makes the boundary tests fail with a readable message showing expected vs. actual boundaries.
5. `ATTRIBUTION.md` exists, and the chosen commit-vs-download approach is recorded in `docs/decisions.md`.
6. Summary written at the end of the session: selected clip ids, fixture sizes, measured WER per model tested, any tolerance changes and why.
