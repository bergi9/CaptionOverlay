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
