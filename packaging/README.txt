CaptionOverlay — live captions for everything your PC plays
============================================================

CaptionOverlay listens to your PC's audio output (whatever you hear: videos, streams, calls, games)
and shows live captions in a transparent overlay on top of your other windows. Speech recognition
runs locally with OpenAI Whisper (whisper.cpp), or optionally through an OpenAI-compatible API.

Requirements
------------
- Windows 10 (21H2 or newer) or Windows 11, 64-bit.
- Nothing else: .NET and all libraries are included. No administrator rights needed.
- For fast local captions a graphics card is recommended (NVIDIA, AMD or Intel; the normal
  graphics driver is enough). Without one, use a small model or the API mode.

Getting started
---------------
1. Extract this zip to any folder (e.g. Documents\CaptionOverlay). Do not run it from inside the zip.
2. Start CaptionOverlay.exe. A setup wizard asks for your language and downloads a Whisper model
   (models are NOT included in this zip; they are downloaded from huggingface.co on first use).
3. Drag the overlay to where you want it and click "Done".

"Windows protected your PC"
---------------------------
The app is not code-signed, so Windows SmartScreen may warn you the first time.
Click "More info", then "Run anyway". You can verify the download with SHA256SUMS.txt
from the GitHub release page (PowerShell: Get-FileHash .\CaptionOverlay-*.zip).

Everyday use
------------
- The app lives in the notification area (tray). Right-click the icon for the menu.
- Ctrl+Alt+C  move / resize the overlay (edit mode)
- Ctrl+Alt+P  pause / resume captions
- Ctrl+Alt+X  clear the overlay
- Transcripts (.srt and .txt) are saved to Documents\CaptionOverlay (can be turned off).
- Language (English / German, follows Windows) and light/dark mode: Settings -> General.

Known limitation: the overlay cannot appear above games running in *exclusive* fullscreen.
Use borderless or windowed fullscreen instead.

Updating
--------
Download the new zip, extract it, and delete the old folder. Settings, models and transcripts
are stored in your user profile and carry over:
- Settings:  %APPDATA%\CaptionOverlay
- Models and logs:  %LOCALAPPDATA%\CaptionOverlay
If "Start with Windows" is enabled, the app notices the new location and offers to update the entry.

Uninstalling
------------
Turn off "Start with Windows" in Settings, then delete the app folder and the two folders above.

Privacy
-------
In Local mode, audio never leaves your computer. In API mode, detected speech segments are sent to
the provider you configured. API keys are encrypted for your Windows user and never logged.
