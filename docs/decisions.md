# Decisions (ADR log)

Short records of decisions taken while implementing `PLAN.md`. Newest last.

## ADR-001 — Target .NET 10 LTS, `net10.0-windows`, `.slnx`, central package management
- **Context:** Plan asks for the latest LTS .NET. .NET 10 (SDK 10.0.1xx) is installed and LTS.
- **Decision:** All projects target `net10.0-windows` (Core too, because NAudio WASAPI and DPAPI are Windows-only; Core still has no WPF reference). `CaptionOverlay.slnx` (the .NET 10 default solution format) instead of `.sln`. Package versions live in `Directory.Packages.props`.
- **Consequence:** `dotnet build CaptionOverlay.slnx`. Visual Studio 2022 17.13+ / Rider open `.slnx`.

## ADR-002 — Test stack: xUnit v3 on Microsoft.Testing.Platform, AwesomeAssertions
- **Context:** xUnit v3 + the .NET 10 SDK no longer supports the VSTest bridge for `dotnet test`. FluentAssertions 8 changed to a commercial license (Xceed).
- **Decision:** `global.json` opts into Microsoft.Testing.Platform (`"test": { "runner": ... }`); run tests with `dotnet test --project tests/CaptionOverlay.Core.Tests` or `--solution CaptionOverlay.slnx`. Use **AwesomeAssertions** (Apache-2.0 fork, same API as FluentAssertions 7) instead of FluentAssertions.

## ADR-003 — Ship a folder, not a single-file exe; size budget exceeded
- **Context:** Plan: verify single-file (`PublishSingleFile` + `IncludeNativeLibrariesForSelfExtract`), fall back to a folder if native loading is problematic.
- **Finding:** With single-file, Whisper.net's `NativeLibraryLoader` fails ("Native Library not found in default paths"): it looks for `runtimes\...\whisper.dll` next to the app, which is inside the bundle. ONNX Runtime was fine.
- **Decision:** Folder deploy (`scripts/package.ps1`): self-contained publish, zipped. Non-win-x64 native runtimes (Linux Vulkan ~56 MB, win-x86, win-arm64) are removed by a target in `CaptionOverlay.Core.csproj`.
- **Size:** 212.7 MB extracted / 84.6 MB zipped (v0.1.0). The plan's "< 150 MB" aim is **not met**: the self-contained .NET + WPF runtime is ~140 MB (WPF cannot be trimmed) and the Vulkan whisper runtime is 55 MB. The alternatives (framework-dependent, dropping Vulkan) contradict "user installs nothing" / "GPU with only the driver". Accepted.

## ADR-004 — GPU runtimes: CPU + Vulkan, no CUDA in v1
- **Decision:** Ship `Whisper.net.Runtime` (CPU) and `Whisper.net.Runtime.Vulkan`. Runtime order: CUDA → Vulkan → CPU → CPU-no-AVX (CUDA is only used if someone drops the CUDA runtime in; it is not shipped). "CPU only" setting restricts the order to CPU.
- **Why:** Vulkan covers NVIDIA, AMD and Intel with the normal driver (verified: RTX 4090, tiny model RTF 0.008 on Vulkan vs 0.022 on CPU). `Whisper.net.Runtime.Cuda` needs the CUDA runtime/cuBLAS DLLs, which are several hundred MB; per the plan it is deferred (possible optional add-on later). *The CUDA package size was not downloaded/measured in this session — re-check before revisiting.*
- **Note:** The native library is chosen once per process, so changing the GPU preference takes effect after an app restart (stated in the UI).

## ADR-005 — NAudio 3 API differences
- `WasapiLoopbackCapture` is obsolete in NAudio 3.1 → `new WasapiRecorderBuilder().WithDevice(d).WithLoopbackCapture().WithSharedMode().Build()`. Its `DataAvailable` delivers `ReadOnlySpan<byte>` plus `AudioClientBufferFlags` (silent packets are converted to zeros).
- Device notifications: `MMDeviceEnumerator.CreateNotificationClient()` returns an event-based `MMDeviceNotificationClient` (`DefaultDeviceChanged`, `DeviceStateChanged`, …) instead of implementing `IMMNotificationClient`.
- `WdlResampler.ResamplePrepare` returns the input buffer as `out Span<float>`.

## ADR-006 — Silero VAD version and location
- Bundled `silero_vad.onnx` from snakers4/silero-vad master (release v6.2.3 at download time, SHA-256 `1a153a22…88e3`, 2.3 MB). Its I/O is the v5 layout: inputs `input` [1, 576], `state` [2,1,128], `sr` (int64); outputs `output`, `stateN`. Like the reference implementation, each 512-sample frame is prefixed with the previous frame's last 64 samples.
- The model lives in `src/CaptionOverlay.Core/Assets` (not `App/Assets` as sketched in the plan) so the CLI and tests get it too.

## ADR-007 — Overlay positioning in physical pixels, bottom-anchored
- The overlay is positioned with `SetWindowPos` in physical pixels relative to the monitor work area and stored per monitor device name as fractions `{left, bottom, width}` (plan said "relative rect"). The window grows upwards with more text, so the bottom edge is the anchor. Missing monitor → primary monitor, bottom-centre.
- Height is computed from the content (`UpdateLayout()` then `Measure`), not with `SizeToContent`, to avoid WPF switching SizeToContent off when the window is resized from code.

## ADR-008 — Tray icon without Windows "efficiency mode"
- `H.NotifyIcon`'s `ForceCreate()` enables EcoQoS efficiency mode by default, which throttles the whole process. Real-time audio needs full scheduling, so it is created with `enablesEfficiencyMode: false`.

## ADR-009 — Hallucination filtering checks all languages
- The per-language phrase list (`Core/Captions/hallucinations.json`) is embedded; all languages are checked for every result because language detection is least reliable on the near-silent audio that produces these phrases. Long phrases (≥ 15 chars) match as substrings (e.g. "Untertitel im Auftrag des ZDF, 2021"), short ones only exactly.

## ADR-010 — Live timeline starts at session start
- WASAPI loopback delivers no packets while nothing plays. The session timeline (SRT timestamps) therefore starts when the pipeline starts, and silence is injected while no packets arrive (100 ms tolerance). Starting at the first packet shifted all timestamps by the initial silence (found in manual testing; regression test `LiveTimelineTests`).

## ADR-011 — Settings are sanitized on load
- Out-of-range or empty values (hand-edited / damaged files) are replaced with defaults when `settings.json` is loaded (`AppSettings.Sanitize`), so a bad file cannot yield an invisible overlay or a VAD that never fires.

## ADR-012 — A Whisper processor is built per inference
- The prompt (recent committed text) changes after every caption, and Whisper.net sets prompts at build time, so a processor is built per call from the single loaded `WhisperFactory`. Measured overhead is negligible compared to inference (tiny model: 12.8 s audio in ~100 ms on Vulkan including build).

## ADR-013 — Remote catalog source
- Remote catalog URL: `https://raw.githubusercontent.com/bergi9/whisper-live-caption/main/catalog/models.json` (4 s timeout). Used only if valid and `version` ≥ bundled; entries are validated (id/repo/file patterns, 64-hex SHA-256, no `.en` models, `https://huggingface.co` only). Until the repository is public/pushed the fetch returns 404 and the bundled catalog is used.

## ADR-014 — German test fixtures: commit 8 unchanged FLEURS clips, derive everything else in memory
- **Context:** `GERMAN_TEST_FIXTURES_PLAN.md`: German audio with known transcripts/timings for segmenter, SRT and WER tests. Source: FLEURS `de_de` via the Hugging Face dataset `FluidInference/fleurs-full`, pinned to revision `66b5a017…`, CC-BY-4.0.
- **Decision:** `captionoverlay-cli fixtures fetch-de` selects 8 clips deterministically and writes `tests/fixtures/de` (clips byte-for-byte unchanged, `clips/transcripts.json`, `manifest.json` with SHA-256, `ATTRIBUTION.md`). **Committed** (2.08 MiB of WAV), so CI runs offline. The composite, its 48 kHz stereo version, the reference SRT/cues and the 20 s silence are built in memory by `GermanComposite` (`src/CaptionOverlay.Cli/Fixtures`, linked into the test project, not part of Core).
- **Not downsampled:** the clips are already 16 kHz mono PCM16, Whisper's and Silero's native format. 8 kHz / 8-bit / lossy codecs would save ~1 MB but cut the 4–8 kHz band (s, f, sch, z), so WER would measure the damaged fixture. FLAC would need a new dependency. If the fixtures ever exceed 5 MB: gitignore the WAVs, keep manifest + transcripts, and let CI run `fixtures fetch-de` with a cache keyed on the manifest hash.
- **Selection details found while implementing:**
  - Transcripts are not fully clean: 11 lines end with a stray `.x`, 15 contain quotes. Clips with such transcripts are skipped (they would count as errors for every model). Hyphens are allowed; the WER normalization turns them into spaces.
  - The SHA-256 of every clip is in the tree API listing (`lfs.oid`); downloads are verified against it.
  - The plan's expected long clip `de_de_0000` has only 9.6 s of speech (not force-cut); `0007` and `0009` contain pauses ≥ `EndSilenceMs`. The first clip passing the VAD checks is `de_de_0011` (16.9 s). Normal: `0004 0005 0015 0016 0017 0019` (`0010` has the `.x` artefact). Short: `de_de_0739` (3.6 s).
  - VAD check: with force cuts disabled (`MaxUtteranceSec` = 600) a clip must be exactly one utterance; the long clip must also be force-cut with the default options. Speech spans come from the same run (clip padded with 1 s of silence, span = utterance ± pre-/post-roll).
  - Merge pair (0.3 s gap) = the two normal clips with the shortest speech, so the merged utterance stays below `MaxUtteranceSec` (`GermanComposite.Arrange` throws otherwise). Other normal clips fill slots 1–3 and 8 in id order.
- **Test adjustments vs the plan:** the backpressure variant of the SRT test uses a fixed 250 ms delay per job instead of "2× real time" (which would take > 2 min for the 69 s composite). The long clip is one cue in the reference SRT; the test expects it split into ≥ 2 lines. Tolerances as planned (±200 ms boundaries, ±50 ms 48 kHz vs 16 kHz, ±500 ms SRT starts).
- **WER baseline:** `tests/fixtures/de/wer-baseline.json`, refreshed by running the model tests with `CAPTIONOVERLAY_UPDATE_WER_BASELINE=1`. Measured on Vulkan; the CPU runtime gives slightly different text (e.g. base-q5_1 end-to-end 32 % on CPU vs 21 % on Vulkan), so the end-to-end limit (baseline + 5 points) is only asserted for models with a threshold.

## ADR-015 — Hallucination filter drops any text on inaudible audio
- **Finding:** `large-v3-turbo-german-q5_0` returns "Vielen Dank." for 10 s of digital silence. Adding it to the phrase list would also drop real "Vielen Dank." captions.
- **Decision:** `HallucinationFilter` drops any text when the utterance RMS is below `SilentRms` = 0.001 (≈ −60 dBFS; nothing audible, so any text is invented). In normal operation the VAD rarely lets such audio through, so this is a safety net; the fixed-window (no-VAD) path already skipped audio below the same level.

## ADR-016 — Custom-model validation must not pin the native runtime to CPU
- **Bug (found by the German model tests running after `Invalid_model_file_gives_clear_error`):** `LocalWhisperTranscriber.ValidateModelAsync` loaded with `GpuPreference.CpuOnly`, which also set the process-wide runtime order (chosen once per process). Importing a custom model before any model had loaded (e.g. in API mode) kept all later models on the CPU until restart.
- **Fix:** validation runs with `UseGpu = false` but configures the runtime order from the user's GPU preference (`ValidateModelAsync(path, settings.Engine.Gpu)`). Regression assertion in `Invalid_model_file_gives_clear_error`.
