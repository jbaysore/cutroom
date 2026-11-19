# Cutroom

Keyboard-first Windows video editor for narration-led Overwatch YouTube videos. See [SCOPE.md](SCOPE.md) for what it does and [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) for how it is built.

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
2. **Clean** (`Ctrl+2`): one button removes dead air, breaths, mouth noises and retakes. Review the list, least-certain decisions first:
   - `Enter` plays before and after
   - `X` flips a decision
   - `N` / `P` move through the list
   - `A` accepts the rest
3. **Edit** (`Ctrl+3`): fill the script slots from your footage library (`F`), add blur or notebook cards (`C` / `Shift+C`), zooms (`Z`), freeze + outline (`Shift+O`) and transitions (`Ctrl+T`). Change properties in the Inspector. All shortcuts are listed and rebindable under **Settings → Shortcuts**.
4. **Export** (`Ctrl+4`): exports YouTube 1080p60 H.264 at −14 LUFS, with captions (.srt), chapters, and AI-drafted titles, description and tags.

## Development

```powershell
scripts\build.ps1          # bootstrap + native engine + managed build (Release)
scripts\check.ps1          # Debug build, fast tests, UI smoke (the project's check command)
scriptsuild-whisper-vulkan.ps1  # GPU whisper.cpp only (build.ps1 runs it; -Force rebuilds)
scripts\test-speech.ps1    # model-based speech tests and the end-to-end export test
```
