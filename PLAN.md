# CaptionOverlay — Implementation Plan

> Working name: **CaptionOverlay** (rename freely).
> Audience: this plan is written to be handed to Claude Code. Work milestone by milestone (section 12), keep each milestone shippable, and verify acceptance criteria before moving on.

---

## 1. Goal

A Windows desktop app that:

1. Captures **system audio** (whatever the PC is playing) via WASAPI loopback.
2. Transcribes it **live** with **Whisper**, either
   - **locally** via whisper.cpp (through Whisper.net), or
   - **remotely** via an **OpenAI-compatible transcription API** (OpenAI, Groq, self-hosted) when local hardware is too weak.
3. Shows the text in a **transparent, always-on-top, click-through caption overlay**.
4. Ships as a **self-contained package**: the user installs nothing else (no .NET, no Python, no CUDA toolkit).
5. **Does not bundle Whisper models.** The user picks and downloads models from inside the app (including language-specific fine-tunes such as `whisper-large-v3-turbo-german`).

### Non-goals (v1)
- No translation (design so it can be added later as a post-processing stage).
- No speaker diarization.
- No macOS/Linux.
- No per-application audio capture (whole system output only; see section 14 for a later option).
- No microphone mixing (keep the audio-source abstraction so it can be added).

---

## 2. Tech stack

Verify the **current stable versions** of every package when scaffolding; APIs noted here may have shifted. Prefer the latest LTS .NET.

| Concern | Choice | Notes |
|---|---|---|
| Runtime | .NET (latest LTS), `net*-windows` | Self-contained publish |
| UI | WPF | Transparent overlay windows are straightforward in WPF |
| MVVM | `CommunityToolkit.Mvvm` | Source-generated observable properties/commands |
| Audio | `NAudio` (`NAudio.Wasapi`) | `WasapiLoopbackCapture`, `MMDeviceEnumerator` |
| Whisper local | `Whisper.net` + runtime packages | `Whisper.net.Runtime` (CPU), `Whisper.net.Runtime.Vulkan`, optionally `Whisper.net.Runtime.Cuda` |
| VAD | Silero VAD (ONNX) via `Microsoft.ML.OnnxRuntime` | Ship the ~2 MB `silero_vad.onnx` with the app |
| HTTP | `HttpClient` (built-in) | Model downloads + API transcription |
| Tray icon | `H.NotifyIcon.Wpf` | |
| Logging | `Serilog` (+ File sink) | Rolling logs in `%LOCALAPPDATA%` |
| Settings | `System.Text.Json` | JSON file in `%APPDATA%` |
| Secrets | `System.Security.Cryptography.ProtectedData` (DPAPI) | API keys encrypted per Windows user |
| Tests | xUnit + FluentAssertions | WAV fixtures for pipeline tests |
| Distribution | Portable `.zip` on GitHub Releases | No installer, no auto-updater |

---

## 3. Solution layout

```
CaptionOverlay.sln
├─ src/
│  ├─ CaptionOverlay.Core/          # No WPF references. Pure pipeline logic.
│  │  ├─ Audio/                     # IAudioSource, WasapiLoopbackSource, Resampler, AudioChunk
│  │  ├─ Vad/                       # IVoiceActivityDetector, SileroVad
│  │  ├─ Segmentation/              # UtteranceSegmenter (state machine)
│  │  ├─ Transcription/             # ITranscriber, LocalWhisperTranscriber, OpenAiCompatibleTranscriber
│  │  ├─ Captions/                  # CaptionBuffer, CaptionLine, HallucinationFilter
│  │  ├─ Models/                    # ModelCatalog, ModelDownloader, ModelStore
│  │  ├─ Pipeline/                  # CaptionPipeline (orchestrates everything)
│  │  ├─ Export/                    # SrtWriter, TxtWriter
│  │  └─ Settings/                  # AppSettings, SettingsStore, SecretStore
│  ├─ CaptionOverlay.App/           # WPF
│  │  ├─ Overlay/                   # OverlayWindow, OverlayViewModel, Win32 interop
│  │  ├─ Settings/                  # SettingsWindow + tabs
│  │  ├─ Models/                    # Model manager UI
│  │  ├─ Tray/                      # Tray icon + menu
│  │  ├─ Hotkeys/                   # Global hotkey service (RegisterHotKey)
│  │  └─ Assets/silero_vad.onnx
│  └─ CaptionOverlay.Cli/           # Optional dev harness: WAV/loopback → console text
├─ tests/
│  ├─ CaptionOverlay.Core.Tests/
│  └─ fixtures/                     # short WAVs: speech EN/DE, silence, music, speech+music
├─ catalog/models.json              # Model catalog (also hosted remotely, see section 7)
└─ PLAN.md
```

Rule: **Core must not reference WPF.** The App project only wires Core into UI. This keeps the pipeline testable and lets the CLI harness exist.

---

## 4. Architecture

```
┌───────────────┐   float32 48k stereo   ┌────────────┐  float32 16k mono  ┌─────────┐
│ WasapiLoopback│ ─────────────────────► │ Resampler  │ ─────────────────► │ SileroVad│
│    Source     │   (capture thread)     └────────────┘  (32 ms frames)    └────┬────┘
└───────────────┘                                                              │ speech prob / frame
                                                                               ▼
                                                                    ┌──────────────────────┐
                                                                    │ UtteranceSegmenter   │
                                                                    │ Idle→Speaking→Trailing│
                                                                    └───┬──────────────┬───┘
                                                   partial snapshot     │              │ final utterance
                                                   (every ~500 ms)      ▼              ▼
                                                             ┌────────────────────────────────┐
                                                             │ TranscriptionScheduler         │
                                                             │  finals: never dropped (queue) │
                                                             │  partials: latest-wins, droppable│
                                                             └──────────────┬─────────────────┘
                                                                            ▼
                                                             ┌────────────────────────────────┐
                                                             │ ITranscriber                   │
                                                             │  LocalWhisper | OpenAiCompatible│
                                                             └──────────────┬─────────────────┘
                                                                            ▼
                                                  ┌──────────────────────────────────────────┐
                                                  │ HallucinationFilter → CaptionBuffer      │
                                                  │ (committed lines + one tentative line)   │
                                                  └──────────────┬───────────────────────────┘
                                                                 ▼ events (marshalled to UI thread)
                                                  ┌──────────────────────────────────────────┐
                                                  │ OverlayWindow  +  Transcript/SRT export  │
                                                  └──────────────────────────────────────────┘
```

### Threading model
- **Capture thread** (NAudio callback): copy samples only, push to a `Channel<AudioChunk>`. Never block here.
- **Processing task**: reads the channel → resample → VAD → segmenter. Single consumer, cheap work.
- **Transcription worker**: one dedicated long-running task consuming the scheduler. Whisper inference is serialized (one model instance, one inference at a time).
- **UI thread**: receives `CaptionBuffer` change events via `Dispatcher.InvokeAsync`.
- Everything is cancellable with a single `CancellationTokenSource` owned by `CaptionPipeline` (Start/Stop).

### Backpressure
- If inference is slower than real time: **drop partial jobs** (keep only the newest), **never drop final jobs**.
- Expose a "lag" metric (queued final audio seconds) to the UI; if it exceeds ~10 s, show a hint suggesting a smaller model or API mode.

---

## 5. Component specs

### 5.1 Audio capture — `IAudioSource`

```csharp
public interface IAudioSource : IAsyncDisposable
{
    WaveFormat SourceFormat { get; }
    event Action<ReadOnlyMemory<float>>? SamplesAvailable; // interleaved, source format
    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}
```

`WasapiLoopbackSource`:
- Uses `WasapiLoopbackCapture` on the **default render device** (or a user-selected device).
- Typical format is 48 kHz, 32-bit float, stereo; **do not assume**, read `WaveFormat`, and handle 16/24-bit PCM too.
- **Silence gap handling:** loopback delivers *no callbacks* while nothing is playing. The segmenter must be driven by a clock as well: the processing task injects synthetic silence when no data arrives for > 100 ms, so trailing-silence detection still ends utterances.
- **Device changes:** register `IMMNotificationClient` (via `MMDeviceEnumerator.RegisterEndpointNotificationCallback`). On default-device change (e.g., headphones plugged in) → restart capture on the new device transparently. Log it.
- Device list for the settings UI: `MMDeviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)`.

### 5.2 Resampler
- Downmix to mono (average channels), resample to **16 kHz float32**.
- Use a streaming resampler (NAudio `WdlResampler` works on float buffers and keeps state between chunks). Keep state across calls; no clicks at chunk boundaries.
- Unit test: 1 kHz sine at 48 kHz stereo → 16 kHz mono; verify frequency via zero crossings and amplitude within tolerance.

### 5.3 VAD — `SileroVad`
- Load `silero_vad.onnx` (ship in Assets). Verify the exact input/output tensor names and state shape for the version you download (they changed between v4 and v5; v5 takes `input`, `state`, `sr` and returns `output`, `stateN`).
- Frame size at 16 kHz: **512 samples (32 ms)**. Carry the recurrent state between frames; reset on Stop.
- Output: speech probability per frame.
- Thresholds (configurable, advanced settings): `speechThreshold = 0.5`, `silenceThreshold = 0.35` (hysteresis).
- Note: heavy music can suppress VAD. Provide an advanced toggle **"Disable VAD (fixed 5 s windows)"** for music-heavy content.

### 5.4 Utterance segmenter
State machine:

| State | Enter when | Behavior |
|---|---|---|
| `Idle` | start / after emit | Keep a ring buffer of the last **300 ms pre-roll** |
| `Speaking` | ≥ `minSpeechMs` (default 250 ms) of speech frames | Append audio; every `partialIntervalMs` (default 500 ms) emit a **PartialSnapshot** of the whole current utterance |
| `Trailing` | speech prob < silenceThreshold | Keep appending; if speech resumes → back to `Speaking`; if silence ≥ `endSilenceMs` (default 600 ms) → emit **FinalUtterance**, go `Idle` |

Hard limits:
- `maxUtteranceSec` (default 12 s): force a cut. Prefer to cut at the lowest-energy 200 ms window within the last 2 s to avoid splitting words. Emit Final, start a new utterance immediately (no pre-roll loss).
- Discard utterances shorter than `minUtteranceMs` (default 400 ms), since they're mostly clicks and breaths.

Emits:
```csharp
record PartialSnapshot(Guid UtteranceId, float[] Samples, TimeSpan StartOffset);
record FinalUtterance(Guid UtteranceId, float[] Samples, TimeSpan StartOffset, TimeSpan Duration);
```
`StartOffset` is relative to session start (needed for SRT export).

### 5.5 Transcription — `ITranscriber`

```csharp
public interface ITranscriber : IAsyncDisposable
{
    string DisplayName { get; }
    bool SupportsPartials { get; }           // API mode: false by default (cost)
    Task<TranscriptionResult> TranscribeAsync(
        float[] samples16kMono, TranscriptionOptions opts, CancellationToken ct);
}

public record TranscriptionOptions(
    string? Language,        // "de", "en", or null = auto-detect
    string? Prompt,          // previous committed text, for context
    bool IsPartial);

public record TranscriptionResult(
    string Text, string? DetectedLanguage,
    IReadOnlyList<TranscribedSegment> Segments, // with timestamps + avg prob if available
    TimeSpan InferenceTime);
```

#### 5.5.1 `LocalWhisperTranscriber` (Whisper.net)
- Create **one** `WhisperFactory.FromPath(modelPath)` per loaded model; build processors from it. Dispose on model switch.
- Runtime selection: set Whisper.net's runtime library order (e.g., `RuntimeOptions.RuntimeLibraryOrder`) to `Cuda → Vulkan → Cpu`, restricted to what's installed. Log which runtime actually loaded and show it in the UI ("GPU (Vulkan)", "CPU").
- Processor settings: language (forced if the user chose one; **German fine-tunes must be forced to `de`**), thread count = `min(physical cores, 8)`, prompt = last ~200 chars of committed text (improves continuity and casing), disable timestamps for partials if that speeds things up.
- **Partial passes** use the same model by default. Advanced option: a second small model (e.g., `tiny`/`base`) dedicated to partials. If enabled, it holds its own factory; memory impact shown in the UI.
- Pad audio shorter than 1 s with silence to 1 s (Whisper behaves badly on very short clips).
- Verify Whisper.net's current API surface (`WhisperFactory`, `WhisperProcessorBuilder`, `ProcessAsync`) against its README at scaffold time.

#### 5.5.2 `OpenAiCompatibleTranscriber`
- `POST {baseUrl}/audio/transcriptions`, multipart form:
  - `file`: WAV (16 kHz, mono, PCM16), encoded in memory
  - `model`: user-configured (defaults per provider preset, see below)
  - `language`: if set
  - `prompt`: previous committed text (truncate)
  - `response_format`: `json` (or `verbose_json` where supported, for segments)
- Provider presets (user-editable, check current model names at implementation time):
  - OpenAI: base `https://api.openai.com/v1`, model `whisper-1` (newer transcription models may exist)
  - Groq: base `https://api.groq.com/openai/v1`, model `whisper-large-v3-turbo`
  - Custom: any base URL (e.g., a self-hosted whisper.cpp server)
- **Finals only** by default (partials would multiply cost ~5-10×). Advanced toggle to enable partials with a minimum interval of 1.5 s.
- Resilience: timeout 30 s; retry once on 5xx/timeout with jitter; on 401/403 stop the pipeline and show "API key rejected"; on 429 back off and surface a status message.
- Key stored with DPAPI (`SecretStore`), never in plain JSON, never logged.
- "Test connection" button: send 1 s of silence, report latency or error.

### 5.6 Caption buffer and hallucination filter
`CaptionBuffer` holds:
- `Committed`: list of `CaptionLine(Text, Start, End, UtteranceId)`
- `Tentative`: the latest partial text for the in-progress utterance (or null)

Rules:
- A partial result updates `Tentative` **only if** it belongs to the current utterance and is newer than the last applied partial (results can arrive out of order).
- A final result replaces `Tentative` with a committed line.
- Raise a single `Changed` event per update.

`HallucinationFilter` (applied before committing):
- Drop results that are empty, only punctuation, or only "[Music]"-style tags.
- Drop known silence hallucinations. Keep the list in a JSON resource, per language. Seed with common ones, e.g.:
  - EN: "Thank you for watching.", "Thanks for watching!", "Subtitles by the Amara.org community"
  - DE: "Untertitel im Auftrag des ZDF", "Untertitelung des ZDF, 2020", "Vielen Dank fürs Zuschauen.", "Untertitel von Stephanie Geiges"
- Drop a result identical to the previous committed line when the utterance was mostly low-energy (repetition loop).
- If segment probabilities are available: drop if average token prob is very low **and** audio RMS is low.

### 5.7 Pipeline orchestration — `CaptionPipeline`
- `StartAsync(PipelineConfig)` / `StopAsync()` / `ReloadTranscriberAsync(...)` (switch model or mode without restarting capture).
- Exposes: `CaptionBuffer`, `Status` (Idle, LoadingModel, Listening, Transcribing, Error(message)), `Metrics` (real-time factor, lag seconds, runtime in use).
- Session timing: `Stopwatch` from Start; offsets feed SRT export.

### 5.8 Export
- Auto-save transcript per session (toggle): `%USERPROFILE%\Documents\CaptionOverlay\yyyy-MM-dd_HH-mm-ss.srt` + `.txt`.
- Writes happen on commit (append), so a crash loses at most one line.
- "Copy transcript" and "Open transcripts folder" in the tray menu.

---

## 6. Overlay window

WPF `OverlayWindow`:
- `WindowStyle="None"`, `AllowsTransparency="True"`, `Background="Transparent"`, `Topmost="True"`, `ShowInTaskbar="False"`, `ResizeMode="NoResize"` in locked mode.
- Extended styles via `SetWindowLongPtr(GWL_EXSTYLE)`: `WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`, plus **`WS_EX_TRANSPARENT` in locked mode** (click-through).
- **Two modes**, toggled by a global hotkey (default `Ctrl+Alt+C`) and the tray menu:
  - **Locked** (default): click-through, no border, never steals focus.
  - **Edit**: visible border + grip, draggable and resizable; shows a small toolbar (font size −/+, opacity, lines count, "Done").
- Rendering: rounded semi-transparent background panel (configurable color/opacity), text with outline/shadow for readability on any background. Committed lines normal, tentative line slightly dimmed/italic.
- Layout: show last N lines (default 2), newest at bottom; long lines wrap; old lines scroll away with a short fade.
- Persist position/size **per monitor** (store monitor device name + relative rect). Handle monitor removal by falling back to the primary monitor, bottom-center.
- DPI: declare `PerMonitorV2` in `app.manifest`; test at 100%/150% scaling across two monitors.
- Stay on top: re-assert `SetWindowPos(HWND_TOPMOST, ... SWP_NOACTIVATE)` every ~1 s and on foreground changes (some apps grab topmost).
- Known limitation to document: overlays cannot appear over **exclusive fullscreen** games; borderless/windowed fullscreen works.
- Global hotkeys via `RegisterHotKey` on a hidden message window; handle "hotkey already in use" gracefully.
- Additional hotkeys: pause/resume captions (`Ctrl+Alt+P`), clear overlay (`Ctrl+Alt+X`). All rebindable.

---

## 7. Model management

### 7.1 Catalog format (`models.json`)
```json
{
  "version": 1,
  "models": [
    {
      "id": "large-v3-turbo-q5_0",
      "displayName": "Large v3 Turbo (Q5)",
      "languages": ["multi"],
      "repo": "ggerganov/whisper.cpp",
      "file": "ggml-large-v3-turbo-q5_0.bin",
      "sizeBytes": 574000000,
      "sha256": "<fill after verifying download>",
      "tier": "recommended-gpu",
      "notes": "Best quality/speed balance on a GPU."
    },
    {
      "id": "large-v3-turbo-german-q5_0",
      "displayName": "Large v3 Turbo — German (Q5)",
      "languages": ["de"],
      "forceLanguage": "de",
      "repo": "MolyProduction/whisper-large-v3-turbo-german-ggml-q5_0",
      "file": "ggml-large-v3-turbo-german-q5_0.bin",
      "sizeBytes": 574000000,
      "sha256": "<fill after verifying download>",
      "license": "apache-2.0 (upstream: primeline/whisper-large-v3-turbo-german)",
      "tier": "recommended-gpu"
    }
  ]
}
```

Seed entries (fill exact sizes and SHA-256 by downloading once; file names from `huggingface.co/ggerganov/whisper.cpp`):

| id | repo / file | approx size | use |
|---|---|---|---|
| tiny-q5_1 | ggerganov/whisper.cpp / `ggml-tiny-q5_1.bin` | 32 MB | weakest PCs, partial model |
| base-q5_1 | ggerganov/whisper.cpp / `ggml-base-q5_1.bin` | 60 MB | CPU-only laptops |
| small-q5_1 | ggerganov/whisper.cpp / `ggml-small-q5_1.bin` | 190 MB | decent CPU |
| large-v3-turbo-q5_0 | ggerganov/whisper.cpp / `ggml-large-v3-turbo-q5_0.bin` | 574 MB | GPU default |
| large-v3-turbo | ggerganov/whisper.cpp / `ggml-large-v3-turbo.bin` | 1.62 GB | GPU, max quality |
| large-v3-turbo-german-q5_0 | MolyProduction/whisper-large-v3-turbo-german-ggml-q5_0 / `ggml-large-v3-turbo-german-q5_0.bin` | 574 MB | German |
| large-v3-turbo-german | cstr/whisper-large-v3-turbo-german-ggml / `ggml-model.bin` | 1.62 GB | German, max quality |

Download URL pattern: `https://huggingface.co/{repo}/resolve/main/{file}` (follow redirects; public, no auth).

Do **not** list `.en` models as options for non-English users, and skip `*-encoder.mlmodelc.zip` (Apple only).

### 7.2 Catalog sourcing
- Bundle `models.json` in the app as fallback.
- On startup, fetch a remote copy (e.g., raw GitHub URL of the repo's `catalog/models.json`) with a short timeout; use it if its `version` ≥ bundled. This lets you add models without releasing a new build.
- Remote catalog is data only; validate schema, reject entries with non-`https` URLs or hosts outside an allowlist (`huggingface.co`).

### 7.3 Downloader
- Storage: `%LOCALAPPDATA%\CaptionOverlay\models\{id}\{file}`.
- Download to `{file}.part`; **resume** with `Range: bytes={existing}-` if the server returns 206; restart if 200.
- Progress (bytes, speed, ETA), cancel, pause/resume in the UI.
- After download: verify SHA-256 (streaming hash, off UI thread); on mismatch delete and show error. Then atomic rename `.part` → final.
- Check free disk space before starting (size + 10% margin).
- Concurrency: one download at a time (queue others).

### 7.4 Custom models
- "Add model file…" → pick any local `.bin`; user enters a display name and optional forced language. Validate by attempting `WhisperFactory.FromPath` and show a clear error if it isn't a valid GGML Whisper model (e.g., someone picked a Transformers `.safetensors`).

### 7.5 Hardware hint (first run and on the model page)
- Detect GPUs (DXGI adapter enumeration or WMI `Win32_VideoController`), VRAM, RAM, CPU cores.
- Suggest: dedicated GPU with ≥ 4 GB VRAM → turbo-q5; no GPU but ≥ 8 cores → small/base; otherwise → base or API mode.
- Offer a **"Benchmark"** button: run the selected model on a bundled 10 s fixture WAV, report real-time factor (RTF). RTF < 0.3 → "good for live"; 0.3–0.7 → "usable, some lag"; > 0.7 → "too slow, use a smaller model or API".

---

## 8. Settings

`AppSettings` (JSON in `%APPDATA%\CaptionOverlay\settings.json`, versioned with a `schemaVersion` + migration hook):

- **Audio:** device (default = follow system default), VAD on/off, thresholds (advanced).
- **Engine:** mode (`Local` | `Api`), selected model id, language (`auto` | ISO code), partials on/off, partial interval, separate partial model (advanced), GPU preference (`Auto` | `CPU only`).
- **API:** provider preset, base URL, model name, API key (stored via DPAPI in a separate file), partials on/off.
- **Overlay:** font family/size/weight, text color, outline, background color/opacity, lines shown, width, position (per monitor), fade timeout (hide overlay after N s of no speech).
- **Hotkeys:** toggle edit mode, pause, clear.
- **Transcripts:** auto-save on/off, folder, formats (SRT/TXT).
- **General:** start with Windows (per-user `HKCU\...\Run` entry pointing at the exe's current location), start listening on launch, notify about new versions (optional, see section 10).

Settings window tabs: General · Audio · Engine · Models · API · Overlay · Hotkeys · About (versions, loaded runtime, log folder link).

Apply changes live where possible (overlay styling instantly; engine/model changes trigger `ReloadTranscriberAsync`).

---

## 9. App shell and UX

- Tray-first app: launching shows the tray icon + overlay; the settings window is opened from the tray.
- Tray menu: Start/Stop listening · Pause captions · Edit overlay position · Engine: *Local – model name* / *API – provider* (submenu to switch) · Copy transcript · Open transcripts folder · Settings… · Quit.
- **First-run wizard** (3 steps): pick language → pick engine (hardware hint + benchmark, or API key) → download model (with progress) → show overlay in edit mode so the user places it.
- Status feedback in the overlay's edit mode and the tray tooltip: "Loading model…", "Listening (GPU/Vulkan)", "API error: …", "Lagging 8 s, consider a smaller model".
- Single instance (named mutex); a second launch brings the settings window forward.

---

## 10. Packaging and distribution

- Publish: `dotnet publish src/CaptionOverlay.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`.
  - Verify that Whisper.net and ONNX Runtime native DLLs load correctly from a single-file bundle. If self-extraction is problematic, ship a **folder** (installer handles it) instead of single-file.
- **GPU runtimes:**
  - Ship **CPU + Vulkan** runtimes by default. Vulkan covers NVIDIA, AMD and Intel with just the normal graphics driver.
  - Check what `Whisper.net.Runtime.Cuda` requires on the user's machine (CUDA runtime/cuBLAS DLLs are large). If it needs the CUDA toolkit or adds hundreds of MB, make CUDA an **optional downloadable add-on** or skip it for v1.
- **No installer, no auto-updater.** Releases are published manually on **GitHub Releases** as a portable zip; users download and extract it themselves.
  - Artifact: `CaptionOverlay-vX.Y.Z-win-x64.zip` containing the exe (plus native DLLs if not single-file), `silero_vad.onnx` if not embedded, `README.txt`, `LICENSE`, `THIRD_PARTY_NOTICES.txt`.
  - Publish a `SHA256SUMS.txt` next to the zip.
  - Optional GitHub Actions workflow: on pushing a `v*` tag, build, zip, compute checksum and attach to a **draft** release. You review and publish manually.
- **Portable behavior:**
  - The app runs from any folder the user extracts to (Downloads, Desktop, USB stick). No admin rights, no writes to `Program Files` or the exe folder.
  - Settings, models, logs and transcripts live in `%APPDATA%` / `%LOCALAPPDATA%` / Documents (as specified elsewhere), so **updating = extract the new zip and delete the old folder**; settings and downloaded models carry over.
  - If "Start with Windows" is on and the exe path changes after an update, detect the stale `Run` entry on launch and offer to fix it.
- **New-version notice (optional, off by default or on with a clear toggle):** on startup, call `https://api.github.com/repos/{owner}/{repo}/releases/latest`, compare `tag_name` to the app version, and show a small tray notification with a link to the release page. No downloading, no replacing files.
- **Code signing:** optional. Unsigned exes from a zip trigger SmartScreen ("Windows protected your PC" → More info → Run anyway). Document this in the README and release notes; consider Azure Trusted Signing later.
- Target: Windows 10 21H2+ and Windows 11, x64. (ARM64 later: Whisper.net and ONNX Runtime have ARM64 builds; verify.)
- Size budget (excluding models): aim < 150 MB installed.
- Include `THIRD_PARTY_NOTICES.txt` (NAudio, Whisper.net / whisper.cpp MIT, ONNX Runtime MIT, Silero VAD MIT, etc.). Model licenses are shown in the model manager per model.

---

## 11. Logging, diagnostics, privacy

- Serilog rolling file logs in `%LOCALAPPDATA%\CaptionOverlay\logs`, 7-day retention.
- Log: device changes, model load (path, runtime, load time), per-utterance inference time and RTF, API status codes and latency. **Never log transcript text or API keys** by default (debug toggle for text).
- "Export diagnostics" button: zip logs + settings (key redacted) + hardware summary.
- Privacy statement in About: in Local mode audio never leaves the machine; in API mode audio segments are sent to the configured provider.

---

## 12. Milestones

Each milestone ends with its acceptance criteria met and a short manual test note in `docs/testing.md`.

### M0 — Scaffold
- Solution, projects, CI build (GitHub Actions: build + test on `windows-latest`), `.editorconfig`, nullable enabled, warnings as errors in Core.
- **Accept:** `dotnet build` and `dotnet test` pass in CI.

### M1 — Loopback capture
- `WasapiLoopbackSource`, resampler, CLI command `record --seconds 10 out.wav` writing 16 kHz mono WAV.
- Synthetic-silence injection; default-device change handling.
- **Accept:** recording a YouTube clip produces a clean 16 kHz mono WAV; switching output device mid-recording continues capture; resampler unit tests pass.

### M2 — VAD + segmenter
- `SileroVad`, `UtteranceSegmenter` with partial/final events.
- CLI command `segment in.wav` prints utterance boundaries.
- **Accept:** on fixtures, speech segments are detected within ±200 ms of hand-labelled boundaries; silence-only fixture yields zero utterances; 30 s monologue is split at ≤ 12 s.

### M3 — Local transcription (console)
- `LocalWhisperTranscriber`, `TranscriptionScheduler`, `CaptionBuffer`, `HallucinationFilter`.
- CLI `live --model <path> --lang de` prints tentative/committed lines from loopback.
- Log loaded runtime (CPU/Vulkan/CUDA) and RTF.
- **Accept:** German news clip transcribes live with the German turbo model; finals appear ≤ 1.5 s after end of sentence on a mid-range GPU; no "Untertitel im Auftrag des ZDF" on silence fixture.

### M4 — Overlay
- WPF app, `OverlayWindow` with locked/edit modes, hotkeys, tray icon, basic styling, position persistence.
- **Accept:** overlay stays on top of a browser and a borderless game, is click-through when locked, doesn't steal focus, is correct on a 150% DPI second monitor, and position survives restart.

### M5 — Partials and polish of live feel
- Partial scheduling (latest-wins), tentative line rendering, optional separate partial model, fade-out after silence.
- **Accept:** text visibly updates while someone is still speaking; under artificial slowdown partials are dropped but no final is lost.

### M6 — Model manager
- Catalog (bundled + remote), downloader with resume + SHA-256, custom model import, hardware hint, benchmark.
- **Accept:** killing the app mid-download and relaunching resumes; tampered file is rejected; benchmark reports RTF; switching models live works without restarting capture.

### M7 — API mode
- `OpenAiCompatibleTranscriber`, provider presets, DPAPI key storage, test-connection, error handling.
- **Accept:** works against OpenAI and Groq; invalid key shows a clear message and stops cleanly; network drop recovers without crashing; key not present in settings.json or logs.

### M8 — Settings UI, first-run wizard, export
- All settings tabs, live-apply, first-run wizard, SRT/TXT auto-save, copy transcript.
- **Accept:** fresh user profile goes from install to captions in < 3 minutes (excluding download time); SRT timestamps line up with the source video within ~0.5 s.

### M9 — Packaging (portable zip)
- Self-contained publish, zip layout, `SHA256SUMS.txt`, third-party notices, README (incl. SmartScreen note), optional tag-triggered GitHub Actions workflow creating a draft release, optional new-version notice.
- **Accept:** zip extracted to an arbitrary folder runs on a clean Windows 10 and Windows 11 VM with no .NET installed and without admin rights; Vulkan path works on a machine with only the GPU driver; replacing the folder with a newer version keeps settings and models; a stale "Start with Windows" path is detected.

### M10 — Hardening
- 2-hour soak test (memory stable, no handle leaks), sleep/resume, device unplug, model file deleted while running, disk full during download, very loud/quiet audio, music-only content.
- **Accept:** no crashes; memory growth < 50 MB over 2 h; every error path shows a readable message.

---

## 13. Testing strategy

- **Unit (Core):** resampler, segmenter state machine (feed synthetic VAD probabilities), caption buffer ordering, hallucination filter, SRT writer, catalog validation, downloader resume logic (against a local `HttpListener` test server).
- **Integration:** fixture WAVs through the full pipeline with a small model (`tiny`), asserting non-empty text and timing; skip in CI if the model isn't cached (mark with a trait), run locally.
- **Fake transcriber** (`FakeTranscriber` with configurable delay) to test scheduling/backpressure deterministically.
- **Manual matrix:** NVIDIA / AMD / Intel iGPU / CPU-only; Windows 10 / 11; 100% / 150% / 200% DPI; single / dual monitor.

---

## 14. Risks and open decisions

| Risk / decision | Mitigation / default |
|---|---|
| Whisper hallucinates on silence/music | VAD gate + hallucination filter + min utterance length |
| Whisper is not a streaming model; latency | Short utterances (≤ 12 s), partial passes, turbo model, GPU |
| CUDA runtime size / install requirements | Default to Vulkan; CUDA optional or later |
| Single-file publish vs native DLLs | Fall back to folder deploy inside installer |
| Third-party GGML conversions could change or disappear | Pin by SHA-256; remote catalog can repoint; custom import always available |
| Exclusive-fullscreen games hide the overlay | Document; recommend borderless mode |
| VAD misses sung/heavily mixed speech | "Disable VAD (fixed windows)" option |
| API cost with partials | Finals-only default for API |
| SmartScreen on unsigned builds | Document "More info → Run anyway" in README; code signing later |
| Users running old versions (no auto-update) | Optional new-version tray notice linking to GitHub Releases |

Later options (post-v1): per-application capture (Windows process loopback API, `AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`, Windows 10 2004+), microphone mixing, translation stage, OBS text-source output (WebSocket/local HTTP), ARM64 build.

---

## 15. Instructions for Claude Code

1. Work strictly milestone by milestone; don't start M(n+1) until M(n)'s acceptance criteria pass.
2. Before using any NuGet package, check its current version and README; update this plan if an API differs from what's described here.
3. Keep `CaptionOverlay.Core` free of WPF and Windows-UI dependencies (NAudio and ONNX Runtime are fine).
4. Never commit model files or API keys. Add `*.bin`, `*.part`, and `models/` to `.gitignore`.
5. Prefer small, reviewable commits with a one-line summary of what changed and how it was tested.
6. When a decision in section 14 needs to be made, write a short note in `docs/decisions.md` (ADR style) and continue with the stated default.
