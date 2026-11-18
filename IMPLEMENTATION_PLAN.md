# Cutroom implementation plan

This plan says how to build what [SCOPE.md](SCOPE.md) defines. Work proceeds phase by phase. Each phase ends with exit checks that must pass before the next one starts.

## 1. Architecture

```
┌──────────────────────── Cutroom.App (.NET 10, WPF, dark Fluent theme) ───────────────────────┐
│ Timeline · Slot panel · Review list · Source viewer · Libraries · Inspector · Keymap          │
└──────────────┬───────────────────────────────┬───────────────────────────────┬───────────────┘
               │                               │                               │
     Cutroom.Core (pure C#)          Cutroom.Speech (C#)                Cutroom.Library (C#)
     project model, timeline ops,    whisper.cpp + aligner + VAD,       SQLite index, folder watch,
     undo stack, script/slot model,  script matching, cleanup engine,   tags, search, proxies queue
     subtitle chunker, ducking       denoise, loudness analysis
               │
     Cutroom.Engine (interop) ──P/Invoke──► native/engine.dll (C++20, D3D11)
                                            decode (FFmpeg + D3D11VA) · compositor · text (DirectWrite)
                                            preview swapchain · export encode (FFmpeg h264_amf)
     Audio: Cutroom.Audio (C#, NAudio/WASAPI) mixes float buffers for playback and export
```

**Key decisions**
- **One renderer for preview and export.** The same native compositor draws preview frames and export frames, so what Joshua sees is exactly what he gets. Preview renders into a D3D11 swapchain hosted in WPF through `HwndHost`.
- **Timeline and project logic stays in pure C# (`Cutroom.Core`)** with no UI or native dependencies, so it is fully unit-testable.
- **Audio is mixed in C#.** Sources are decoded once to a 48 kHz float cache. Playback goes through WASAPI, and export feeds the same mixer into FFmpeg.
- **Dependencies are fetched by `scripts/bootstrap.ps1` into `.deps/`** (gitignored), following Game Clipper's pattern: FFmpeg pinned to a BtbN month-end LGPL shared build with a sha256 marker, whisper.cpp built with Vulkan, and ONNX Runtime with DirectML. Models download on first run to `%LOCALAPPDATA%\Cutroom\models` with pinned hashes.
- **Storage.** Projects are `*.cutroom.json`. The app's index and caches live under `%LOCALAPPDATA%\Cutroom\`: `index.db` (SQLite), `cache\proxies`, `cache\audio`, `cache\waveforms`, `cache\thumbs` and `cache\speech`. Cache entries are keyed by a media fingerprint (path, size, mtime, and a hash of the first and last 1 MB).

## 2. Repository layout

```
src/Cutroom.App/        WPF shell, views, keymap, theme
src/Cutroom.Core/       Project, Timeline, Track, Clip, Slot, SubtitleRegion, Card, Command (undo)
src/Cutroom.Speech/     Transcriber, Aligner, ScriptMatcher, CleanupEngine, Vad, Denoise, Loudness
src/Cutroom.Library/    IndexDb, FolderWatcher, ProxyQueue, Search, Tags
src/Cutroom.Audio/      Mixer, Ducking, WasapiOutput, AudioCache
src/Cutroom.Engine/     P/Invoke wrappers over native/engine.dll
native/engine/          C++20: decoder, frame cache, compositor, effects (HLSL), text, encoder
tests/Cutroom.Core.Tests/, tests/Cutroom.Speech.Tests/, tests/Cutroom.Engine.Tests/
tests/corpus/           (gitignored) Joshua's real narrations + scripts + labeled decisions
scripts/bootstrap.ps1, scripts/check.ps1
```

`scripts/check.ps1` runs the build plus all unit tests and is the project's check command. It should be fast and must not require the gitignored corpus. Corpus evaluations run separately through `scripts/eval-cleanup.ps1`.

## 3. Data model (Cutroom.Core)

- `Project { Version, Settings(1920×1080, 60 fps, 48 kHz), Media[], Script, Narration, Timeline, SubtitleRegions[], SubtitlePresets[], CardTemplates[], Keymap overrides }`
- `Timeline` is made of `Track`s, each typed V (video), A (audio) or S (subtitle), with clips placed at frame positions. Time is stored as integer **ticks at 1/48000 s** so audio cuts are sample-accurate. Video snaps to 60 fps frames.
- **Narration track (A1)** is the spine. It holds the cleaned narration as many sub-clips of the original file, one per kept span.
- `Slot { ScriptRange, TimelineRange, Status: Empty|Footage|Card }`. Slot ranges are derived from the narration's word timings, so they stay correct when narration edits shift time.
- `CleanupDecision { Kind: Silence|Breath|MouthNoise|Retake|FalseStart|AdLibKept|PossibleSpeechKept, SourceRange, Confidence, Applied, Reason }`
- Every mutation is a `Command` with `Do`/`Undo`. The undo stack is unlimited for the session. Autosave runs every 30 s and on each significant command, writing `name.cutroom.json` plus rotating `name.cutroom.json.bak1..5`.

## 4. Phases

### Phase 0: Spikes (de-risk before committing)
Each spike lives in a scratch folder and produces a short findings note that becomes a decision in this plan.

1. **Render/preview.** Host a D3D11 swapchain in WPF via `HwndHost`. Decode with FFmpeg + D3D11VA, composite 3 layers (footage + Gaussian blur + text) at 1080p60, and scrub a 3-hour OBS file through a proxy. Pass: steady 60 fps playback, and a scrub frame appears in under 100 ms.
2. **Transcription + alignment.** Run whisper.cpp large-v3 on Vulkan over a real 15-minute narration. Measure speed and word error rate against a hand transcript. Compare word-boundary accuracy of whisper DTW timestamps against CTC forced alignment (wav2vec2 ONNX on DirectML). Pass: transcribes in under 3 minutes, and the chosen aligner's word boundaries fall within 30 ms of hand labels for 95% of words.
3. **Denoise.** Run DeepFilterNet3 (ONNX) and FFmpeg `arnndn` on Joshua's mic recordings and let him pick by ear.
4. **Owner input needed:** 3–5 real narration recordings with their scripts. They go into `tests/corpus/` (gitignored, never committed) and become the regression corpus for cleanup and subtitles.

### Phase 1: Foundation editor
- Solution skeleton, bootstrap script, dark theme (reusing Game Clipper's window-scoped Fluent theme approach), and the check script.
- Media import with ffprobe metadata and background jobs for proxies (540p H.264 on AMF with a short GOP for scrubbing), waveforms (peak files), thumbnails, and the decoded audio cache. A job panel shows progress, and nothing blocks the UI.
- Timeline UI: tracks, clips, zoom, snapping, playhead, waveform drawing, and virtualization so a 90-minute timeline stays smooth.
- Playback: the native compositor with one video layer plus the C# audio mixer, A/V sync on the audio clock, and J/K/L shuttle.
- Editing commands with Resolve-default hotkeys: `I`/`O` mark, `Ctrl+B`/`B` blade, `Shift+Delete` ripple delete, `Up`/`Down` previous/next edit, `Left`/`Right` frame step, `Shift+Left`/`Shift+Right` 1-second step, `Ctrl+Z`/`Ctrl+Shift+Z`, `Space` play. The keymap is a JSON file with an in-app editor.
- Minimal export through the same compositor, encoded by `h264_amf` at 1080p60. 30/24 fps sources are conformed by frame repetition, and 1440p sources are downscaled with a Lanczos shader.
- **Exit:** cut a real 3-hour OBS recording into a 10-minute edit by keyboard only, export it, and the MP4 plays correctly with sound in sync.

### Phase 2: Narration cleanup (success criteria 1–2)
Pipeline (`CleanupEngine`), each stage cached by media fingerprint plus stage settings:
1. **Decode** to 48 kHz mono float.
2. **Denoise** with the engine chosen in spike 3. The output is a cached derivative, so the source file stays untouched.
3. **Transcribe** with whisper.cpp large-v3. The initial prompt is seeded with a custom vocabulary drawn from the script (hero, ability and map names), so these get spelled right.
4. **Align** words with the aligner chosen in spike 2, producing `Word { text, start, end, confidence }`.
5. **VAD** with Silero VAD (ONNX) to get a per-10 ms speech probability. This is the independent safety net.
6. **Script match.** Normalize both texts (lowercase, strip punctuation, spell out numbers). Then run a word-level alignment (Smith–Waterman with affine gaps) from the transcript into the script. A *take* is a contiguous transcript run that maps onto the same script sentence. For each script sentence:
   - If one complete take exists, keep it.
   - If several takes exist, keep the **last take that covers at least 90%** of the sentence's words. Cut the earlier ones as `Retake`.
   - A short run that restarts the same script position within a few seconds (e.g. "the best way, the best way to…") is cut as `FalseStart`.
   - Transcript runs with no script match are kept as `AdLibKept`. They are never cut automatically.
7. **Gaps.** Every region between kept words is a gap. Gaps are shortened to the target pause: 200 ms between sentences (range 150–250), 400 ms at script paragraph breaks, and commas keep up to 120 ms of their natural pause. If VAD speech probability inside a gap exceeds 0.5 for more than 120 ms, the gap is kept and flagged `PossibleSpeechKept`.
8. **Mouth noises.** Within gaps only, classify short events (energy burst, spectral centroid, zero-crossing rate, duration) as breath, click/smack or swallow. Removing them is free because gaps are being shortened anyway. Events overlapping word boundaries are left alone.
9. **Cut rendering.** Cuts snap to the nearest zero crossing within ±5 ms, use 8 ms equal-power crossfades, and keep 30 ms of padding outside each word. The kept spans become sub-clips on A1.
10. **Loudness.** Measure narration with EBU R128 and gain it to −16 LUFS pre-mix. The final mix target is set in Phase 6.

**Confidence and review list.** Each decision gets a confidence built from aligner word confidence, script-match score and VAD agreement. The review list sorts ascending by confidence and shows the decision kind, transcript context and reason. Keys: `Enter` plays the before/after audition (2 s of context around the decision), `X` toggles the decision, `N`/`P` move to the next/previous decision, and `A` accepts all decisions remaining above the current one. Every toggle is a normal undoable command and updates the timeline immediately.

**Exit (run on the corpus with `eval-cleanup.ps1`):**
- Zero substantive words removed: every word in the hand transcript's final takes survives.
- At least 95% of hand-labeled retakes are removed.
- All ad-lib sentences are kept.
- Joshua reviews one real narration in under 10 minutes and the result sounds right to him.

### Phase 3: Subtitles (success criteria 3–5)
- **Source text.** Uses the kept transcript words with their timings. Where the script match is confident, each word's spelling and capitalization comes from the script (e.g. "kiriko" becomes "Kiriko", "1v1" stays "1v1"). Joshua can also import an SRT, which skips auto-generation.
- **Chunker.**
  - Greedy, word by word, aiming for 3–8 words per caption.
  - Hard limits: one line within a configurable maximum width (measured with the preset's font through DirectWrite, default about 42 characters), minimum 0.8 s on screen, and maximum 6 s.
  - Preferred breaks: sentence end, then clause punctuation, then before conjunctions/prepositions. It never breaks between a number and its unit, or inside a hero/ability name.
- **Editing.** Inline text editing, split/merge with hotkeys, and nudging edges. Captions follow narration edits automatically because they are anchored to word IDs, not times.
- **Regions.** `SubtitleRegion { start, end, presetId }`. Toolbar presets: Intro (from timeline start to the first script heading or a chosen marker), Whole video, and Custom (mark I/O, then press `Alt+S`).
- **Presets.** Font, size, fill, outline width/color, shadow, position (safe-area anchored) and an in/out animation (none, fade or pop). Stored in the app's settings so every project shares them. Rendered by the native text layer, so preview and export match.
- **Export.** SRT for YouTube closed captions, covering the whole video regardless of the visual regions.
- **Exit:** on the corpus, subtitle words match the hand transcript with at least 98% accuracy after script correction. Correcting one 12-minute video takes Joshua under 5 minutes.

### Phase 4: Script slots, footage library, asset library (success criterion 6)
- **Script import.** Paste text, or open a .docx (parsed with DocumentFormat.OpenXml) or a .txt file. Paragraphs and headings are preserved, since headings help define the "Intro" region.
- **Slots.** Generated from the script match: one per sentence by default, or merged per paragraph with a toggle. The slot panel lists text, duration and status. `Ctrl+Down` jumps to the next empty slot.
- **Source viewer.** A second viewer for library footage, with its own J/K/L and I/O.
- **Fast fill.** `F` fills the current slot from the source in-point and trims to the slot length; `Shift+F` uses the source I/O range and ripples the slot to match. `Alt+Left`/`Alt+Right` slips the source by frames inside the slot.
- **Library index (SQLite).**
  - `media` (fingerprint, path, kind, duration, fps, resolution)
  - `moments` (media, in, out, note)
  - `tags`, `asset_tags`, `favorites`, `recent_uses`, and `fts5` virtual tables over filenames, notes and tags.
- **Indexing and tagging.** Joshua adds "watched folders" (footage roots and asset roots). A `FileSystemWatcher` plus a startup rescan keep the index current, and files are never moved. While scrubbing he presses `T` to tag the current moment (quick picker with autocomplete over existing tags) and `Ctrl+M` to save I/O as a named moment.
- **Search panel.** Search-as-you-type (FTS5 plus tag facets), type filters (footage, music, SFX, image, background), and Favorites and Recently-used tabs. Hovering shows a filmstrip or waveform preview with audio. Drag or press `Enter` to place.
- Proxy generation for newly indexed footage runs at low priority in the background.
- **Exit:** with 5 hours of indexed footage, Joshua fills every slot of a real 10-minute narration using only keyboard plus search, and finds any tagged moment in under 5 seconds.

### Phase 5: Cards, overlays, transitions, animations (success criterion 7)
- **Compositor effects (HLSL)**: separable Gaussian blur (radius parameter, rendered on a downsampled target for speed), dim, transform (position/scale/rotation/opacity with keyframes), crop, rounded mask, and drop shadow.
- **Blurred-footage card.** `C` on a slot or a timeline range adds a blur+dim adjustment over the range plus a content layer (text, image or clip). The content source comes from the inspector or by dropping in from the library.
- **Branded-background card.** `Shift+C` replaces the range with a looping background asset plus a content layer.
- **Card templates** save blur strength, dim level, background asset, content layout, text preset and in/out animation. Joshua picks one from a palette with number keys.
- **Transitions:** cross-dissolve, dip to black, and directional sweep/wipe (with feather). `Ctrl+T` (the Resolve default) adds the default transition at the nearest edit point.
- **Animation presets:** pop-in, slide (4 directions), zoom/punch-in to a selected rectangle, and shake. Each is a keyframe generator applied to a layer's transform, so it stays editable afterwards.
- **Facecam:** picture-in-picture with crop, rounded mask, and saved corner/size layouts.
- **Exit:** recreate the cards from two recent published videos in under 1 minute each, and preview stays at 60 fps on a range with blur + card + subtitles.

### Phase 6: Audio mix (success criterion 8)
- Tracks: A1 narration, A2 game audio (linked to its video clips by default, unlink with `Ctrl+L`), A3 music, A4 SFX.
- **Auto-duck.** Driven by the kept narration word spans, which are already known exactly, so no detector is needed. Music and game audio drop by a default −12 dB (adjustable per track) with 150 ms attack and 400 ms release. Gaps shorter than 600 ms stay ducked to avoid pumping. Ducking is generated as volume keyframes, visible and editable, and regenerates when narration changes unless a keyframe was hand-edited.
- Clip gain, fades (drag handles or `Alt+F`), and per-track volume and mute/solo.
- **Final loudness.** Two-pass EBU R128 on the export mix to −14 LUFS integrated with a −1 dBTP true-peak limiter.
- **Exit:** a real video's mix needs no manual volume work beyond optional music level tweaks.

### Phase 7: Export polish and v1 acceptance (success criteria 9–10)
- Export dialog: one YouTube preset (1080p60, H.264 High, AMF VBR at a 40 Mbps target, AAC 320 kbps, `+faststart`) plus a filename template. Optional SRT is written alongside.
- Background export with progress, an ETA and a cancel button. Editing stays responsive during export. When done, Joshua can reveal the file in Explorer.
- **Pre-flight checks:** missing media, empty slots, and subtitle regions without a preset.
- **v1 acceptance:** Joshua edits a full real video in Cutroom without opening Resolve, then uploads it. Any friction found gets fixed before v1 is declared done.

## 5. Testing strategy
- **Unit tests (`scripts/check.ps1`):** timeline commands and undo symmetry (property tests: random command sequences, then undo all, must return the original), the script matcher on synthetic retake/ad-lib cases, the subtitle chunker, ducking keyframe generation, and project JSON round-trips including old-version migration.
- **Engine tests:** render fixed timelines to frames and compare them against golden images with a perceptual tolerance. Export a 5 s test project and verify duration, fps, audio/video sync and loudness using ffprobe/ebur128.
- **Corpus evaluation (`scripts/eval-cleanup.ps1`):** Joshua's labeled recordings. It reports removed-substantive-word count (must be 0), retake recall and subtitle accuracy. It is run before changing any threshold or model.
- **UI smoke:** render key views at 100/150/200% DPI, using the same approach as Game Clipper.

## 6. Performance budgets
- Opening a 90-minute project takes under 2 s. Timeline interactions stay at 60 fps.
- Scrubbing shows a frame within 100 ms, playback runs at 1080p60 with no dropped frames on a card + subtitle range, and A/V drift stays under one frame.
- Full cleanup of a 15-minute narration takes under 4 minutes end to end on the RX 6900 XT, with cached reruns under 5 s.
- Export of a 12-minute video takes 3 minutes or less.

## 7. Risks and mitigations
| Risk | Mitigation |
|---|---|
| Aligner boundaries clip word edges | 30 ms padding, VAD cross-check, corpus metric that must be 0, review list |
| Retake logic picks the wrong take | Keep-last-complete rule, low confidence when takes differ in wording, one-key toggle with audition |
| Native compositor complexity | Spike first; keep the effect set small (blur, dim, transform, text, wipe) |
| Huge source files | Proxies, a frame cache, and decoding only visible layers |
| whisper.cpp/Vulkan driver issues on AMD | Fall back to a CPU build (13600K) at reduced speed; pin a known-good release |
| Dependency URLs rot (as with Game Clipper's FFmpeg) | Pin month-end builds plus sha256, and reuse cached archives |
