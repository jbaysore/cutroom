# Cutroom scope

## Purpose
A fast, keyboard-first Windows video editor for one person (Joshua) who makes Overwatch YouTube videos and dislikes editing. It is not an all-in-one suite. It includes only the necessities, plus as many time-saving automations as possible. Personal tool: not a multi-user product.

## Success criteria (v1 is done when)
1. Upload a long voice recording and Cutroom cleans it automatically: removes mouth noises (breathing, swallowing) and dead space without removing any substantive speech.
2. Cutroom subtitles the video accurately on its own.
3. Joshua chooses where subtitles appear (only the intro, the whole video, or any chosen ranges).
4. Subtitles have saved style presets.

## Inputs
- Gameplay video file(s)
- Separate voice recording (must be synced to the gameplay and cleaned)
- Facecam or other overlay sources

## Features
### 1. Voice cleanup (top priority)
- Auto-cut silences/dead space and mouth noises, then Joshua reviews. Every cut is visible on the timeline and undoable.
- Adjustable thresholds. Nothing substantive may be removed; the review step is the safety net.
- Runs locally.

### 2. Subtitles (top priority)
- Auto-transcribe locally (no paid service or inference API) and also import a script/SRT.
- Edit text after generation.
- Place subtitles on any chosen time ranges (intro only, whole video, custom).
- Saved style presets (font, color, outline, position, animation).

### 3. Fast cutting
- Keyboard-first editing: mark in/out, split, ripple delete, jump between cuts. Mouse optional.
- Joshua finds highlights himself. No auto-highlight detection; hotkeys make marking fast.

### 4. Overlays and effects
- Transitions: fades and sweeps.
- Blur background with text/image overlays on top (common pattern, should be one-step).
- Motion graphics, two layers: a template library (lower thirds, callouts, title cards with swappable text/image) and one-click animation presets (pop-in, slide, zoom, shake) on any layer.
- SFX placement on the timeline.

### 5. Export
- YouTube 1080p60 MP4 with AMD hardware encoding.
- Vertical 9:16 Shorts reframed from the same project.

## Platform and stack
- Windows desktop, native. Likely .NET/WPF with FFmpeg and AMD hardware encoding, as in Game Clipper. Confirm in a spike before locking in.
- Non-destructive: source media is never modified; projects are JSON files that reference media.

## Non-goals
- Color grading
- Multi-cam and advanced audio mixing (basic volume only)
- macOS/Linux support
- Auto-highlight detection
- 4K export and direct YouTube upload (not requested; revisit later)
- Paid services or inference API keys

## Open questions
- Which local speech-to-text engine, and which mouth-noise detection approach, meets the "never remove substantive audio" bar?
- Should this connect to Game Clipper output later?
- Phasing and order of build (proposed: voice cleanup, then subtitles, then cutting/timeline, then overlays, then export).
