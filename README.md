# Cutroom

Keyboard-first Windows video editor for narration-led Overwatch YouTube videos. See [SCOPE.md](SCOPE.md) for what it does and [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) for how it is built.

The narration is the spine of every video. Cleanup compares the recording with the script, keeps the last good take, and lists every cut for review with the least certain first. AI only proposes: it drafts text and plans, and never places anything on the timeline.

## Install and run

Requirements: Windows 11, the .NET 10 SDK and Visual Studio 2022 C++ Build Tools (only to build). The AI helpers need the Claude Code or Codex CLI installed and signed in. They run on your subscription and never use API keys.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install.ps1
```

This fetches the pinned dependencies (FFmpeg, whisper.cpp, DeepFilterNet, fonts) into `.deps\`. It then builds the native engine and the GPU version of whisper.cpp (once, a few minutes; it makes narration cleanup much faster, and Cutroom uses the CPU if it's unavailable), and publishes Cutroom self-contained to `%LOCALAPPDATA%\Programs\Cutroom`, adds **Cutroom** to the Start menu, and registers the on-demand `Cutroom Hub Operator` task for Project Hub. That task starts Cutroom in the signed-in desktop session; the editor exposes only a loopback health/stop endpoint for Hub, with no Funnel route. Run the installer again to update; your projects and settings are kept.

The first cleanup downloads the speech models (about 1.8 GB, verified by sha256) to `%LOCALAPPDATA%\Cutroom\models`.

Project Hub's **Restart** installs a newer commit from this PC's Cutroom `main` before starting the app. With the same commit already installed, it skips the build. Hub writes the update output to `logs/cutroom-build.log` in the Project Hub folder.

## Where things live

- **Projects:** Create, open, and rename projects from the launch screen or the editor’s **Projects…** button. **Browse…** opens projects from other folders and adds them to the list. New projects default to `Documents\Cutroom\<name>\<name>.cutroom.json`. Recordings go in `narration\` and exports in `exports\`. Autosave keeps `.bak` copies.
- **Settings, shortcuts, library index, caches:** `%LOCALAPPDATA%\Cutroom`.
- **Footage and asset folders:** indexed in place. Cutroom never moves or renames your files.

## Workflow

1. **Record** (`Ctrl+1`): paste your script from Google Docs (headings become sections and chapters), then record. You can also import an OBS recording.
2. **Clean** (`Ctrl+2`). Run **Clean narration**, then use **Play cleaned narration** (`Space`) to hear how it flows. **Review cuts** lists retakes and false starts in chronological order. Open **All decisions** for the full list, or **Show script** to read along.
   - `Enter` plays the selected passage with the original audio, then with the cut applied.
   - `X` toggles whether the selected audio is cut.
   - `N` / `P` move to the next or previous decision.
   - Choose **Continue to Edit** when the narration sounds right.
3. **Edit** (`Ctrl+3`): fill the script slots from your footage library (`F`), add blur or notebook cards (`C` / `Shift+C`), zooms (`Z`), freeze + outline (`Shift+O`) and transitions (`Ctrl+T`). Change properties in the Inspector. All shortcuts are listed and rebindable under **Settings → Shortcuts**.
4. **Export** (`Ctrl+4`): exports YouTube 1080p60 H.264 at −14 LUFS, with captions (.srt), chapters, and AI-drafted titles, description and tags.

## Development

```powershell
scripts\build.ps1          # bootstrap + native engine + managed build (Release)
scripts\check.ps1          # Debug build, fast tests, golden images, UI smoke at 100% and 150% scaling (the project's check command)
scripts\build-whisper-vulkan.ps1  # GPU whisper.cpp only (build.ps1 runs it; -Force rebuilds)
scripts\test-speech.ps1    # model-based speech tests and the end-to-end export test
scripts\eval-cleanup.ps1   # score narration cleanup on your own recordings in tests\corpus (see below)
scripts\bench.ps1          # performance budgets: project open, scrub, cleanup, export (slow)
```

### Cleanup corpus

`scripts\eval-cleanup.ps1` measures how well cleanup does on real narrations. Put each recording in its own folder under `tests\corpus\` (gitignored):

- `narration.wav`, or the OBS/Streamlabs video file named `narration.*`
- `script.txt` or `script.docx`
- `expected.txt`: what the finished narration should say, in order. Keep ad-libs; leave out retakes and false starts.
- `marks.txt` (optional): retake-mark times in seconds, one per line

The report (`artifacts\eval-cleanup.md`) lists, per recording, substantive words wrongly removed (must be 0), retake recall (target 95%) and word accuracy (target 98%). It uses your Settings for the whisper model, noise removal and GPU.

Golden images for the engine tests live in `tests\Cutroom.Tests\golden`. After an intended visual change, rerun the tests with `CUTROOM_UPDATE_GOLDEN=1`, check the new images and commit them.

## Rights

The source is published for viewing. All rights reserved; no license is granted to copy, modify, or redistribute it.
