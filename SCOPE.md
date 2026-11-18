# Cutroom scope

## Purpose
A fast, keyboard-first Windows video editor for one person (Joshua), who makes educational Overwatch YouTube videos and dislikes editing. It is not an all-in-one suite: it includes only what Joshua's workflow needs, plus as many time-saving automations as possible. It is a personal tool, not a multi-user product, and it replaces DaVinci Resolve for his videos.

## Joshua's workflow (what Cutroom is built around)
1. **Script.** Writes a full script in Google Docs.
2. **Narration.** Records the script on its own, separate from gameplay. Follows the script about 95% of the time; occasionally rewords a line or adds a sentence. When he flubs a line he re-says it and keeps going, so retakes stay in the file.
3. **Clean the narration.** Today this is manual and slow.
4. **Find and place footage.** This is the most time-consuming step. Once the narration is final, he finds gameplay that fits each point: cut-up OBS full matches, practice range clips recorded to demonstrate a point, and other people's gameplay (with permission). Then he splices the clips in against the commentary.
5. **Make it engaging.** Subtitles, cards, transitions, cuts, music.
6. **Export** and upload.

The narration is the spine of every video. Everything else is placed against it.

## Scale
- Finished videos run from about 4 minutes to over an hour; most are 8–15 minutes.
- Raw footage per project runs from about 10 minutes to 3+ hours. Design for up to 90 minutes finished and 5 hours raw.
- Sources are mostly 1080p60, sometimes 1440p60, with an occasional 30 or 24 fps clip.
- Machine: i5-13600K, 32 GB RAM, Radeon RX 6900 XT (16 GB), 2560×1440 display.

## Success criteria (v1 is done when)
1. Joshua imports a narration recording plus its script, and Cutroom automatically removes dead space, mouth noises (breaths, swallows, clicks) and retakes, keeping the last good take of each line. It never removes substantive speech, including ad-libs that aren't in the script.
2. Every cleanup decision appears in a review list with low-confidence decisions first. Each can be auditioned and toggled with one key.
3. Cutroom subtitles the video accurately on its own, using the script to spell names and Overwatch terms correctly.
4. Joshua chooses where subtitles appear: only the intro, the whole video, or any chosen ranges.
5. Subtitles have saved style presets.
6. The narration splits into script slots that Joshua fills with footage from a searchable, tagged library. Clips auto-trim to fit their slot.
7. A blurred-footage card and a branded-background card can each be made in one step.
8. Narration always takes focus: music and game audio duck automatically under speech.
9. A finished video exports as YouTube-ready 1080p60 MP4 with AMD hardware encoding.
10. Joshua edits a real video end to end in Cutroom without opening Resolve.

## Inputs
- Narration recording (one long file per video)
- Script (pasted text or a .docx/.txt exported from Google Docs; no Google sign-in)
- Gameplay footage from many sources: OBS full matches, practice range clips, other creators' footage
- Background music, SFX, images and animated backgrounds from Joshua's existing asset folders
- Facecam (occasionally)

## Features
### 1. Narration cleanup (top priority)
- **Script-aware retake removal.** Matches what Joshua said against the script. When a line was said more than once, it keeps the last complete take and cuts the earlier attempts and false starts.
- **Ad-libs are kept.** Speech that isn't in the script is kept and shown in the review list as "ad-lib kept".
- **Dead space.** Pacing is tight but natural: about 150–250 ms between sentences and a little longer at script paragraph breaks. Both are adjustable.
- **Mouth noises.** Breaths, swallows, lip smacks and clicks are removed only in the gaps between words, never inside a word.
- **Sound.** Local background-noise removal plus loudness normalization.
- **Review.** A list of every cut, sorted so low-confidence decisions come first. One key plays before/after, one key toggles. Every cut also shows on the timeline and can be undone.
- **Safety.** Speech-detection cross-checks flag possibly-untranscribed speech and keep it instead of cutting. Thresholds are adjustable. Everything runs locally.

### 2. Subtitles (top priority)
- Transcribed locally (no paid service or inference API), with the script used for spelling, especially hero names, abilities and maps. SRT import is also supported.
- Educational style: one line maximum, usually about 3–8 words per caption, broken at natural phrase boundaries. No word-by-word TikTok highlighting.
- Text is editable after generation.
- Shown only on chosen time ranges: intro only, the whole video, or custom ranges.
- Saved style presets: font, size, color, outline/shadow, position, and in/out animation.
- SRT export for YouTube closed captions.

### 3. Footage matching (the biggest time sink)
- **Script slots.** The cleaned narration is split into slots, one per script sentence or paragraph. A slot panel shows the script text, each slot's status (empty, footage, or card), and its duration. Joshua jumps between empty slots by hotkey.
- **Fast fill.** Joshua marks a moment in the source viewer and presses one key. The clip drops into the current slot and trims to fit. He can then nudge the source in-point or adjust the slot length.
- **Footage library.** Indexes the footage folders. Proxies are generated in the background so even a 3-hour recording scrubs instantly. Joshua tags moments by hotkey while scrubbing (hero, map, topic, free notes) and finds them later with fast search.
- Joshua always chooses the footage. Cutroom does not auto-place or auto-recommend clips in v1.

### 4. Asset library
- Indexes Joshua's existing asset folders in place and never moves or renames files.
- Instant search, tags, favorites, "recently used" and per-type filters (music, SFX, image, background, video).
- Hover previews: audio waveform with playback, image and video thumbnails.

### 5. Cards, overlays and effects
- **Blurred-footage card (most-used visual).** Blurs and dims the footage underneath, then puts text, an image, or another clip on top. One step.
- **Branded-background card.** Cuts to a looping animated background (e.g. the crinkled notebook page) with text, an image, or a clip on top. One step.
- Card templates are saved and reused.
- Transitions: fades and sweeps.
- Occasional animation presets on any layer: pop-in, slide, zoom/punch-in, shake.
- Facecam (occasional): basic picture-in-picture with saved position, size and crop.

### 6. Audio mix
- Three main sources: narration, background music and gameplay audio. Usually two are active at a time.
- **Narration takes focus.** Music and gameplay audio duck automatically under speech. The ducking appears as editable volume keyframes.
- SFX placement on the timeline.
- Per-clip and per-track volume and fades. No advanced mixing.
- The final mix is loudness-normalized for YouTube.

### 7. Fast cutting
- Keyboard-first, with DaVinci Resolve-style default hotkeys (I/O, J/K/L, blade, ripple delete, jump between edits). Every hotkey can be remapped. Mouse use is optional.
- Joshua finds highlights himself. There is no automatic highlight detection.

### 8. Export
- YouTube 1080p60 H.264 MP4 using AMD hardware encoding (AMF).
- 1440p sources are downscaled, so zooms get extra sharpness for free. 30 and 24 fps sources are conformed to 60 fps.

## Platform and stack
- Windows desktop, native. .NET 10/WPF UI with a native C++/Direct3D 11 engine and FFmpeg, following Game Clipper's proven stack. Confirmed in spikes before locking in (see IMPLEMENTATION_PLAN.md).
- Non-destructive: source media is never modified. Projects are JSON files that reference media.
- Standalone: there is no Resolve hand-off phase.

## Non-goals
- Vertical Shorts / 9:16 reframing (Joshua rarely makes Shorts)
- Exports above 1080p
- Color grading
- Multi-cam and advanced audio mixing
- macOS/Linux support
- Auto-highlight detection
- Word-by-word / TikTok-style captions and automatic keyword emphasis
- Game Clipper integration (its clips are imported like any other file)
- Resolve export or hand-off
- Direct YouTube upload
- Paid services or inference API keys

## Later (after v1)
- **Overwatch HUD scanning.** Deterministic reads of the kill feed, hero, ultimate use and map, used to suggest footage for a slot, with the reason shown.
- **Local visual search** (image-text model) to rank footage for fuzzy queries.
- Template library for lower thirds, callouts and title cards.

## Open questions (resolved by the Phase 0 spikes)
- Which word-alignment approach gives cut boundaries precise enough never to clip a word?
- Which local denoiser sounds best on Joshua's mic: DeepFilterNet or RNNoise?
- Is a native D3D11 compositor hosted in WPF smooth enough for 1080p60 multi-layer preview with blur?
