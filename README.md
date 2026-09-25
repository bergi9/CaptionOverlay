# CaptionOverlay (whisper-live-caption)

Live captions for **everything your Windows PC plays**: system audio is captured via WASAPI loopback,
transcribed with **OpenAI Whisper** (locally through whisper.cpp / Whisper.net, or via an
OpenAI-compatible API such as OpenAI or Groq) and shown in a transparent, always-on-top,
click-through overlay.

- Portable: download the zip from Releases, extract, run. No .NET or other installs, no admin rights.
- Models are not bundled: pick and download them in the app, including language-specific fine-tunes
  such as *whisper-large-v3-turbo-german*.
- GPU acceleration through Vulkan (NVIDIA / AMD / Intel with the normal driver), CPU fallback.
- Silero VAD, partial (in-progress) captions, hallucination filter, SRT/TXT transcripts.

## Using it
See [`packaging/README.txt`](packaging/README.txt) (shipped in the zip). Hotkeys: `Ctrl+Alt+C` move/resize,
`Ctrl+Alt+P` pause, `Ctrl+Alt+X` clear.

## Building
Requires the .NET 10 SDK on Windows.

```bash
dotnet build CaptionOverlay.slnx
```

```bash
dotnet test --project tests/CaptionOverlay.Core.Tests
```

```bash
dotnet run --project src/CaptionOverlay.App
```

Portable zip (writes to `artifacts/`):

```bash
powershell -NoProfile -File scripts/package.ps1 -Version 0.1.0
```

Pushing a `v*` tag runs `.github/workflows/release.yml`, which attaches the zip and `SHA256SUMS.txt` to a draft release.

### Developer CLI
`src/CaptionOverlay.Cli` exercises the pipeline without UI:

```bash
dotnet run --project src/CaptionOverlay.Cli -- live --model tiny-q5_1 --lang en
```

Other commands: `devices`, `record`, `segment`, `bench`, `models`, `download`, `hardware` (`--help`).

## Layout
| Path | Contents |
|---|---|
| `src/CaptionOverlay.Core` | Pipeline (audio, VAD, segmenter, transcription, captions, models, settings). No WPF. |
| `src/CaptionOverlay.App` | WPF app: overlay, tray, hotkeys, settings, model manager, first-run wizard |
| `src/CaptionOverlay.Cli` | Developer harness |
| `tests/` | xUnit v3 tests and WAV fixtures |
| `catalog/models.json` | Model catalog (bundled and fetched from this repo at startup) |
| `docs/` | `decisions.md` (ADRs), `testing.md` (test status per milestone) |

Design and roadmap: [`PLAN.md`](PLAN.md).
