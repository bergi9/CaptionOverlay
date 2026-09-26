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
- API mode can stream to OpenAI's realtime transcription models (e.g. `gpt-realtime-whisper`): text appears word by word.
- English and German UI (follows the Windows display language; can be changed in Settings), light and dark mode.

## Why this exists
There are many good live-caption and Whisper projects, most of them in Python. They work well, but getting
one running usually means installing Python, creating a virtual environment, `pip install`-ing PyTorch or
CTranslate2, and sometimes matching CUDA and cuDNN versions. That is fine for developers but a real hurdle for
everyone else. No offense meant to those projects; this one simply aims at the other end: **one zip, extract,
double-click**. It is a self-contained .NET app with the Whisper runtime (whisper.cpp) and the voice detector
(Silero VAD via ONNX Runtime) bundled. The GPU is used through Vulkan, which ships with normal graphics drivers,
so there is no CUDA toolkit, no Python and nothing to install.

## How it was made
The entire code base (app, core library, CLI, tests, build and release scripts, documentation) was written by
**Claude** (Anthropic's AI model, working in Claude Code), following the plan in [`PLAN.md`](PLAN.md) and
reviewed and tested by the repository owner. Design decisions and the problems found along the way are recorded
in [`docs/decisions.md`](docs/decisions.md); what was verified, and how, is in [`docs/testing.md`](docs/testing.md).

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

Tests that need a Whisper model skip themselves if it isn't installed; CI excludes them with
`-- --filter-not-trait "Category=RequiresModel"`. See [`docs/testing.md`](docs/testing.md).

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

Other commands: `devices`, `record`, `segment`, `bench`, `models`, `download`, `hardware`, `fixtures fetch-de` (`--help`).

## Layout
| Path | Contents |
|---|---|
| `src/CaptionOverlay.Core` | Pipeline (audio, VAD, segmenter, transcription, captions, models, settings). No WPF. |
| `src/CaptionOverlay.App` | WPF app: overlay, tray, hotkeys, settings, model manager, first-run wizard |
| `src/CaptionOverlay.Cli` | Developer harness |
| `tests/` | xUnit v3 tests and WAV fixtures. `tests/fixtures/de` holds German clips from [FLEURS](https://arxiv.org/abs/2205.12446) (Google, CC-BY-4.0, see its `ATTRIBUTION.md`) |
| `catalog/models.json` | Model catalog (bundled and fetched from this repo at startup) |
| `docs/` | `decisions.md` (ADRs), `testing.md` (test status per milestone) |

Design and roadmap: [`PLAN.md`](PLAN.md).
