# Cutroom implementation plan

This plan says how to build what [SCOPE.md](SCOPE.md) defines. Phases are a build order, not gates: the whole of v1 is built straight through without stopping for Joshua's input or approval. Automated tests the builder runs itself are part of building. Everything that needs Joshua (his recordings, his ears, his judgment of how things look and feel) happens once, after the build, in section 5.

## 1. Architecture

```
┌────────────────────── Cutroom.App (.NET 10, WPF, modern minimal dark theme) ─────────────────┐
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

- **AI through subscription command-line tools (`Cutroom.AI`).**
  - `IAiProvider` has two implementations: `ClaudeCodeProvider` runs `claude -p` in non-interactive print mode with JSON output, and `CodexProvider` runs `codex exec` non-interactively. Both are detected on PATH, and their sign-in status is probed at startup.
  - Each request runs as a child process in an empty temp working directory with tools/file access disabled or read-only. The prompt goes over stdin, and the answer must be JSON matching a per-feature schema; invalid JSON is retried once with the validation error.
  - **Subscription-only guard.** The child environment always has `ANTHROPIC_API_KEY`, `ANTHROPIC_AUTH_TOKEN`, `OPENAI_API_KEY` and related variables removed, so a request can never fall back to per-use API billing.
  - Requests run in the background with a 120 s timeout and a cancel button. Results are cached by prompt hash.
  - A usage-limit or sign-in error is shown plainly and never retried in a loop.
  - Only text crosses the boundary: script, transcript excerpts, card ranges and channel profile.

- **Implementation notes (as built).** These refine the decisions above:
  - **Text and shapes.** Rasterized with WPF (`FormattedText`, geometry) into textures that the native compositor draws, instead of DirectWrite inside the engine. Preview and export still share the same pixels.
  - **Video decode.** FFmpeg software decode with `sws_scale` to BGRA, plus a one-frame lookahead. Preview decodes proxies, and export decodes the originals.
  - **Export.** Composed frames are read back from the GPU and piped to `ffmpeg.exe` (`h264_amf`, with `h264_mf` as the fallback). The mixed audio is muxed in the same `ffmpeg.exe` call.
  - **Transcription.** whisper.cpp `b5454` with `ggml-large-v3-turbo`, on the GPU through Vulkan.
    - `scripts/build-whisper-vulkan.ps1` compiles the GPU build without the Vulkan SDK. Its inputs are pinned downloads: the whisper.cpp source, Vulkan-Headers and SPIRV-Headers 1.4.363, and conda-forge's shaderc/glslang/spirv-tools for `glslc`. `vulkan-1.lib` is generated from the driver's `vulkan-1.dll`.
    - The build directory is a short temp path, because ggml's shader generator nests deep enough to exceed Windows' 260-character limit.
    - On the RX 6900 XT, 8.7 minutes of speech transcribes in about 70 s, against 216 s on 6 CPU cores. Accuracy is equivalent; the differences are ordinary decoding variance.
    - The prebuilt CPU binary stays bundled. Cutroom switches to it for the rest of the session if the GPU build fails to start, fails, or writes unreadable output. A Settings → Narration toggle turns the GPU off.
    - It runs on the 6 performance cores: on the 13600K, using all 20 threads was about 4× slower.
    - Whisper gets 2 s of padded silence. Its timestamps drift on the last words when a recording stops right after speech.
  - **ONNX models** (Silero VAD and the wav2vec2 aligner) run on the CPU ONNX Runtime. DirectML crashed natively (0xC0000005), in a way that can't be caught from managed code. Confident forced alignments may move whisper's word edges by up to 1.5 s. Words the aligner can't place are fitted into the gap between their aligned neighbors, so a rough whisper time can't overlap the next word and pull a retake decision onto the wrong copy of a phrase.
  - **Denoise.** Uses the DeepFilterNet command-line tool (`deep-filter`, WAV to WAV) instead of an in-process ONNX model. RNNoise via FFmpeg `arnndn` remains the option and fallback.
  - **Undo.** Each edit stores a JSON snapshot of the project, instead of separate `Do`/`Undo` command classes. Autosave checks every 5 s.
  - **UI.** The WPF views are built in code (`Theme.cs` holds the `Ui` helpers), with no XAML per view. One keyboard dispatcher in `MainWindow` maps the Settings shortcut table to commands and scopes each command to its stage.
  - **Tests.** One test project, `tests/Cutroom.Tests`, with categories:
    - `Models`: needs the speech models; run with `scripts/test-speech.ps1`. It includes the full end-to-end test.
    - `Live`: makes real Claude and Codex calls.
    - Everything else runs in `scripts/check.ps1`. That script also runs `Cutroom.exe --smoke`, which renders all four stages at 1440p and 1080p window sizes, using throwaway data in `CUTROOM_DATA`.

## 2. Repository layout

```
src/Cutroom.App/        WPF shell, views, keymap, theme
src/Cutroom.Core/       Project, Timeline, Track, Clip, Slot, SubtitleRegion, Card, Command (undo)
src/Cutroom.Speech/     Transcriber, Aligner, ScriptMatcher, CleanupEngine, Vad, Denoise, Loudness
src/Cutroom.Library/    IndexDb, FolderWatcher, ProxyQueue, Search, Tags
src/Cutroom.Audio/      Mixer, Ducking, WasapiOutput, AudioCache, Recorder
src/Cutroom.AI/         Provider detection, CLI runner, prompts, response schemas, ChannelProfile
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

### Decisions made up front (no spikes)
These were open questions. They are now fixed defaults, and anything that turns out wrong is adjusted during post-build testing.
- **Preview:** a native D3D11 compositor hosted in WPF via `HwndHost`, with FFmpeg + D3D11VA decode.
- **Word alignment:** CTC forced alignment (wav2vec2-family ONNX model on DirectML) refines whisper's words. Whisper's own DTW timestamps are the automatic fallback if the aligner fails on a file.
- **Denoise:** DeepFilterNet3 (ONNX) by default. FFmpeg `arnndn` (RNNoise) is a Settings option and the automatic fallback.
- **AI command-line flags:** the builder confirms the current non-interactive flags for `claude` and `codex` (JSON output, tools disabled, no repository, detectable sign-in/limit errors) while writing the providers. No input from Joshua is needed.

### Phase 1: Foundation editor
- Solution skeleton, bootstrap script and check script.
- **Theme and shell.**
  - A WPF resource dictionary implements the chosen mock B tokens from `design/mocks/edit-stage.html`: background `#0b0c0f`, panels `#14161b`/`#1b1e25`, text `#e8e9ee`, muted `#7d8291`, accent `#7c8cff`, 12 px panel radius, 8 px gaps, 13 px Segoe UI Variable, and track colors (footage `#4a7dff`, card `#a46cff`, subtitles `#f2c94c`, narration `#3ddc97`, game `#2cc5c5`, music `#6c8cff`, SFX `#ff7a59`).
  - Theming is window-scoped, as in Game Clipper.
- **Stage tabs** Record / Clean / Edit / Export (`Ctrl+1`..`Ctrl+4`). Each stage is its own view with its own panel layout, sharing one project and undo stack. Only Edit is built fully in this phase; the others are placeholders.
- **Edit layout.** Slots on the left (resizable, collapsible), preview in the center, and Footage / Assets / Inspector tabs on the right, with the timeline below. Panel sizes persist.
  - Minimum supported window is 1920×1080 at 100%, and 2560×1440 is the design target.
  - The preview pops out to a second monitor (`Ctrl+Shift+P`).
- **Inspector** is the single place for editing properties of the selection. It contains only sliders, toggles, dropdowns and preset strips: no node graphs, curve editors or nested dialogs. It shows only the controls relevant to the selection.
- Media import with ffprobe metadata and background jobs for proxies (540p H.264 on AMF with a short GOP for scrubbing), waveforms (peak files), thumbnails, and the decoded audio cache. A job panel shows progress, and nothing blocks the UI.
- Timeline UI: tracks, clips, zoom, snapping, playhead, waveform drawing, and virtualization so a 90-minute timeline stays smooth.
- Playback: the native compositor with one video layer plus the C# audio mixer, A/V sync on the audio clock, and J/K/L shuttle.
- Editing commands with Resolve-default hotkeys: `I`/`O` mark, `Ctrl+B`/`B` blade, `Shift+Delete` ripple delete, `Up`/`Down` previous/next edit, `Left`/`Right` frame step, `Shift+Left`/`Shift+Right` 1-second step, `Ctrl+Z`/`Ctrl+Shift+Z`, `Space` play. The keymap is a JSON file, edited on a **Settings → Shortcuts** page: a searchable list grouped by stage, with click-to-rebind and conflict warnings. Every toolbar button also shows its shortcut in its tooltip.
- Minimal export through the same compositor, encoded by `h264_amf` at 1080p60. 30/24 fps sources are conformed by frame repetition, and 1440p sources are downscaled with a Lanczos shader.

### Phase 2: Script, recorder and narration cleanup (success criteria 1–2)
**Script import** (needed here, because cleanup matches against the script):
- Paste text, or open a .docx (parsed with DocumentFormat.OpenXml) or a .txt file.
- Paragraphs and Docs headings are preserved. Headings define sections, which drive section header cards, chapters and the "Intro" boundary (everything before the first heading).

**Recorder** (`Cutroom.Audio.Recorder`):
- WASAPI shared-mode capture from the chosen mic at 48 kHz, 24-bit, written to `ProjectFolder\narration\take-YYYYMMDD-HHMMSS.wav`.
- The header is flushed every second, so a crash leaves a playable file.
- The recorder view shows the script in a large, readable, manually scrolled panel, plus a level meter with a clip warning, elapsed time, and Record/Pause/Stop (`Ctrl+R`, `Space`).
- Pressing `R` drops a `RetakeMark` at the current time. Each mark is a strong prior for step 6 below: the speech just before it is presumed to be an abandoned attempt of the next line said. Without marks, the pipeline works the same.
- Importing an OBS/Streamlabs video file extracts its audio track automatically; the picture is never used.

Pipeline (`CleanupEngine`), each stage cached by media fingerprint plus stage settings:
1. **Decode** to 48 kHz mono float.
2. **Denoise** with DeepFilterNet3 (or RNNoise if chosen in Settings). The output is a cached derivative, so the source file stays untouched.
3. **Transcribe** with whisper.cpp large-v3. The initial prompt is seeded with a custom vocabulary drawn from the script (hero, ability and map names), so these get spelled right.
4. **Align** words with the CTC aligner (falling back to whisper DTW timestamps), producing `Word { text, start, end, confidence }`.
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

### Phase 3: Subtitles (success criteria 3–5)
- **Source text.** Uses the kept transcript words with their timings. Where the script match is confident, each word's spelling and capitalization comes from the script (e.g. "kiriko" becomes "Kiriko", "1v1" stays "1v1"). Joshua can also import an SRT, which skips auto-generation.
- **Chunker.**
  - Greedy, word by word, aiming for 3–8 words per caption.
  - Hard limits: one line within a configurable maximum width (measured with the preset's font through DirectWrite, default about 42 characters), minimum 0.8 s on screen, and maximum 6 s.
  - Preferred breaks: sentence end, then clause punctuation, then before conjunctions/prepositions. It never breaks between a number and its unit, or inside a hero/ability name.
- **Editing.** Inline text editing, split/merge with hotkeys, and nudging edges. Captions follow narration edits automatically because they are anchored to word IDs, not times.
- **Regions.** `SubtitleRegion { start, end, presetId }`.
  - **New projects get an Intro region automatically**, from timeline start to the first script heading (or a chosen marker).
  - Toolbar presets: Intro, Whole video, and Custom (mark I/O, then press `Alt+S`).
  - Captions are suppressed wherever a text card is visible, unless the region's "show over cards" flag is set.
- **Presets.** Font, size, fill, outline width/color, shadow, position (safe-area anchored) and an in/out animation (none, fade or pop). Stored in the app's settings so every project shares them. Rendered by the native text layer, so preview and export match.
  - The built-in default preset is a heavy sans-serif (Montserrat ExtraBold, bundled under the OFL licence), white fill, about 8 px black outline, soft 40% shadow, at 78% of frame height, centered.
- **Export.** SRT for YouTube closed captions, covering the whole video regardless of the visual regions.

### Phase 4: Script slots, footage library, asset library (success criterion 6)
- **Slots.** Generated from the script match: one per sentence by default, or merged per paragraph with a toggle. **In the intro section, slots split per phrase**: at clause punctuation, targeting 2–4 s each. The slot panel lists text, duration and status. `Ctrl+Down` jumps to the next empty slot.
- **Source viewer.** A second viewer for library footage, with its own J/K/L and I/O.
- **Fast fill.** `F` fills the current slot from the source in-point and trims to the slot length; `Shift+F` uses the source I/O range and ripples the slot to match. `Alt+Left`/`Alt+Right` slips the source by frames inside the slot.
- **Library index (SQLite).**
  - `media` (fingerprint, path, kind, duration, fps, resolution)
  - `moments` (media, in, out, note)
  - `tags`, `asset_tags`, `favorites`, `recent_uses`, and `fts5` virtual tables over filenames, notes and tags.
- **Indexing and tagging.** Joshua adds "watched folders" (footage roots and asset roots). A `FileSystemWatcher` plus a startup rescan keep the index current, and files are never moved. While scrubbing he presses `T` to tag the current moment (quick picker with autocomplete over existing tags) and `Ctrl+M` to save I/O as a named moment.
- **AI visual plan.**
  - A "Plan visuals" button (also available right after script import, before recording) sends the script with its slot boundaries plus the list of Cutroom's visual recipes. It gets back `{ slotId, kind, clipDescription?, cardText?, reason }` per slot.
  - Each suggestion appears as a muted chip on its slot. `Ctrl+Enter` applies it: a clip suggestion fills the library search box with `clipDescription`, and a card suggestion creates the card with the draft text. `Ctrl+Backspace` dismisses it.
  - Suggestions never place footage.
- **Search panel.** Search-as-you-type (FTS5 plus tag facets), type filters (footage, music, SFX, image, background), and Favorites and Recently-used tabs. Hovering shows a filmstrip or waveform preview with audio. Drag or press `Enter` to place.
- Proxy generation for newly indexed footage runs at low priority in the background.

### Phase 5: Cards, branded templates, emphasis kit, transitions (success criteria 7–8)
Guiding rule: every recipe must look good with zero adjustment. The builder ships polished defaults based on the decisions in SCOPE.md, and Joshua fine-tunes them after the build.

- **Compositor effects (HLSL)**: separable Gaussian blur (radius parameter, rendered on a downsampled target for speed), dim, transform (position/scale/rotation/opacity, driven by internal keyframes), crop, rounded mask, drop shadow, freeze-frame (holds a decoded frame), and vector shapes (ellipse, rectangle, arrow with stroke and draw-on animation, via Direct2D).
- **Blurred-footage text card.** `C` on a slot or a timeline range adds a blur+dim adjustment over the range plus a text layer, with the text typed or pasted in the inspector. `Shift+Tab` swaps the content to an image or clip.
  - **Auto-layout.** Text box at most 70% of frame width, line length 30–45 characters, font size stepped down from 64 px to 40 px until it fits within 60% of frame height, left-aligned for multi-line and centered for one line, generous line spacing. If it still won't fit, an "overflow: split into two cards" warning appears.
  - **Line-by-line reveal (default).** Each card line is matched to the narration words (fuzzy token match against the kept transcript in the card's range), and appears at its matched word's start. Unmatched lines are spaced evenly. `Tab` toggles to all-at-once fade. Retiming: play the range and press `.` at each line's intended start; Cutroom rewrites the reveal times.
- **Branded-background card.** `Shift+C` replaces the range with a looping background asset (default: the notebook page) plus a content layer, using the same auto-layout.
- **Branded templates.**
  - **Section header:** auto-placed at each script heading, on the branded background, showing the heading text.
  - **Title card:** placed after the intro.
  - **End screen:** the last 5–20 s, with safe zones matching YouTube's end-screen element grid.
  - Each ships with a clean default design and is restyled in the Inspector after the build. Each then needs only the text.
- **Emphasis kit.**
  - `Z` zoom-to-box: drag a rectangle, and it animates to fill the frame over 250 ms, holds, and returns.
  - `Shift+O` freeze-frame + outline: freezes at the playhead, Joshua drags an ellipse, box or arrow (`1`/`2`/`3`), the shape draws on, holds until the next narration sentence boundary (adjustable), then resumes.
  - `K` shake, and a quick white flash.
  - Every effect is a preset with Inspector controls: duration slider, easing (Smooth / Snappy / Bouncy / Linear) and effect-specific values such as zoom amount. Keyframes exist only internally. **There is no keyframe or curve editor in the UI.**
- **Animation presets:** pop-in, slide (4 directions), zoom/punch-in. The preset is chosen from a single strip in the inspector.
- **Transitions:** cross-dissolve, dip to black, and directional sweep/wipe (with feather). `Ctrl+T` (the Resolve default) adds the default transition at the nearest edit point. Clicking a transition shows a strip of alternatives, and one click swaps it.
- **Auto-paired SFX.** Library assets tagged `whoosh` or `impact` are attached automatically: a whoosh to sweeps, an impact to zooms and freeze-frames, with a rotating pick to avoid repetition. These are toggled globally and per instance.
- **AI card text.**
  - `Ctrl+G` on a text card (or a selected range) sends the kept transcript for that range plus the surrounding script paragraph. It gets back three drafts: condensed explanation (1–4 lines), key points (2–5 short lines), and exact words with filler trimmed.
  - A popover shows them side by side. `1`/`2`/`3` picks one into the card, which stays fully editable. Hero, ability and map names are kept exactly as spelled in the script.
- **Card templates** save blur strength, dim level, background asset, layout, text preset and reveal mode. Joshua picks one from a palette with number keys.
- **Chapters.** Section header start times become a YouTube chapters text block (`0:00 Intro` first), copied to the clipboard and written next to the export.
- **Facecam:** picture-in-picture with crop, rounded mask, and saved corner/size layouts.

### Phase 6: Audio mix (success criterion 9)
- Tracks: A1 narration, A2 game audio (linked to its video clips by default, unlink with `Ctrl+L`), A3 music, A4 SFX.
- **Auto-duck.** Driven by the kept narration word spans, which are already known exactly, so no detector is needed. Music and game audio drop by a default −12 dB (adjustable per track) with 150 ms attack and 400 ms release. Gaps shorter than 600 ms stay ducked to avoid pumping. Ducking is drawn on each clip's volume line. It is adjusted with per-track depth/speed controls and "don't duck here" ranges, not hand-edited keyframes, and it regenerates whenever narration changes.
- Clip gain, fades (drag handles or `Alt+F`), and per-track volume and mute/solo.
- **Final loudness.** Two-pass EBU R128 on the export mix to −14 LUFS integrated with a −1 dBTP true-peak limiter.

### Phase 7: Export polish (success criteria 10, 12)
- Export dialog: one YouTube preset (1080p60, H.264 High, AMF VBR at a 40 Mbps target, AAC 320 kbps, `+faststart`) plus a filename template. The SRT and the chapters text are written alongside.
- Background export with progress, an ETA and a cancel button. Editing stays responsive during export. When done, Joshua can reveal the file in Explorer.
- **AI YouTube packaging** (a "Package" tab in the export dialog).
  - **Channel profile setup (once).** Joshua pastes a few past titles and descriptions, or Cutroom reads his channel's public RSS feed (`youtube.com/feeds/videos.xml?channel_id=…`, which is free and needs no key) for recent titles and descriptions.
  - AI extracts the style notes and the fixed blocks (links, socials, sign-off). The result is stored in `%LOCALAPPDATA%\Cutroom\channel-profile.json`, and Joshua can edit it.
  - **Per video:** sends the script plus the profile and gets back 5 title options, a description and tags. Fixed blocks are inserted word for word by Cutroom, not by AI, and chapters are appended from the section headers.
  - Each part has a copy button, and a `package.txt` is written next to the export.
- **Pre-flight checks:** missing media, empty slots, and subtitle regions without a preset.

## 5. Testing
### During the build (automated, no Joshua involvement)
- **Unit tests (`scripts/check.ps1`):**
  - timeline commands and undo symmetry (property tests: random command sequences, then undo all, must return the original)
  - the script matcher on synthetic retake/ad-lib/retake-mark cases
  - the subtitle chunker, card auto-layout and line-reveal matching
  - ducking generation
  - AI response schema validation and API-key variable stripping (with a fake provider)
  - project JSON round-trips including old-version migration
- **Engine tests:** render fixed timelines to frames and compare them against golden images with a perceptual tolerance. Export a 5 s test project and verify duration, fps, audio/video sync and loudness using ffprobe/ebur128.
- **UI smoke:** render key views at 100/150/200% DPI, using the same approach as Game Clipper.
- **Synthetic audio fixtures:** generated narration-like test audio with inserted silences, clicks and repeated phrases, so the cleanup pipeline is exercised without Joshua's recordings.

### After the build (with Joshua)
Done once v1 is fully built. Each issue found becomes a fix:
1. **Real recordings.** Joshua provides 3–5 narrations with scripts, kept in `tests/corpus/` (gitignored, never committed). `scripts/eval-cleanup.ps1` measures removed substantive words (must be 0), retake recall (target 95%+), ad-libs kept, and subtitle accuracy (target 98%+). Thresholds, aligner and denoiser are tuned against these.
2. **Listening checks.** Joshua judges cleanup pacing, the denoiser (DeepFilterNet vs RNNoise) and ducking by ear.
3. **Look and feel.** Joshua tunes the subtitle preset, card templates, title card, end screen, section headers and animation timing on his own footage. The results become the built-in defaults.
4. **Workflow timing checks:**
   - reviewing a narration in under 10 minutes
   - correcting subtitles for a 12-minute video in under 5 minutes
   - filling a 10-minute narration's slots from 5 hours of footage
   - building a 20-second intro in under 10 minutes
   - mixing without manual volume work
5. **v1 acceptance (success criterion 11):** Joshua edits a full real video in Cutroom without opening Resolve, then uploads it.

## 6. Performance budgets
- Opening a 90-minute project takes under 2 s. Timeline interactions stay at 60 fps.
- Scrubbing shows a frame within 100 ms, playback runs at 1080p60 with no dropped frames on a card + subtitle range, and A/V drift stays under one frame.
- Full cleanup of a 15-minute narration takes under 4 minutes end to end on the RX 6900 XT, with cached reruns under 5 s.
- Export of a 12-minute video takes 3 minutes or less.

## 7. Risks and mitigations
| Risk | Mitigation |
|---|---|
| Aligner boundaries clip word edges | 30 ms padding, VAD cross-check, review list, post-build corpus metric that must be 0 |
| Retake logic picks the wrong take | Keep-last-complete rule, low confidence when takes differ in wording, one-key toggle with audition |
| Native compositor complexity | Build the preview path first in Phase 1; keep the effect set small (blur, dim, transform, text, wipe) |
| Huge source files | Proxies, a frame cache, and decoding only visible layers |
| AI command-line flags or subscription terms change | Thin provider adapters, flags verified while building, schema-validated output; AI features degrade to greyed out, never blocking editing |
| AI drafts are bland or off-style | Always 2–3 options plus fully editable text; channel profile is editable; prompts tuned on Joshua's real scripts after the build |
| whisper.cpp/Vulkan driver issues on AMD | Fall back to a CPU build (13600K) at reduced speed; pin a known-good release |
| Dependency URLs rot (as with Game Clipper's FFmpeg) | Pin month-end builds plus sha256, and reuse cached archives |
