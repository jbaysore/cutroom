# Cutroom scope

## Purpose
A fast, keyboard-first Windows video editor for one person (Joshua), who makes educational Overwatch YouTube videos and dislikes editing. It is not an all-in-one suite: it includes only what Joshua's workflow needs, plus as many time-saving automations as possible. It is a personal tool, not a multi-user product, and it replaces DaVinci Resolve for his videos.

Joshua burned out on YouTube because of editing. Cleaning narration was tedious, and making visuals engaging meant learning unfamiliar tools in Resolve. Cutroom exists to make editing light enough that he keeps making videos.

## Design principle
**Joshua brings the idea; Cutroom makes it real fast and good-looking by default.**
- Every common visual is a ready-made recipe: pick it, type or drop in the content, done. Getting a good result never requires design skill or learning the software.
- Changing something (a transition, a card style, a subtitle look) takes one click or one key.
- AI only proposes, and Joshua accepts, edits or ignores. It drafts text and suggests plans. It never places anything or changes the edit on its own.

## Joshua's workflow (what Cutroom is built around)
1. **Script.** Writes a full script in Google Docs. He will start marking sections with Docs headings so Cutroom can automate section headers and chapters.
2. **Narration.** Records the script on its own, separate from gameplay. Today he uses Streamlabs OBS, which produces a video file whose picture he has to strip; Cutroom replaces this with a built-in recorder. He follows the script about 95% of the time, occasionally rewording a line or adding a sentence. When he flubs a line he re-says it and keeps going, so retakes stay in the file.
3. **Clean the narration.** Today this is manual and slow.
4. **Find and place footage.** This is the most time-consuming step. Once the narration is final, he finds gameplay that fits each point: cut-up OBS full matches, practice range clips recorded to demonstrate a point, and other people's gameplay (with permission). Then he splices the clips in against the commentary.
5. **Make it engaging.**
   - The intro (first 15–30 s) is a spoken hook/summary over rapid clips (a few seconds each), always subtitled, with as much attention-holding editing as he can manage: zooms, outlines, transitions.
   - The rest of the video is mostly gameplay plus blurred-footage text cards explaining abilities and concepts, with section headers, music and transitions.
   - Every video has a title card and an end screen.
6. **Export** and upload.

The narration is the spine of every video. Everything else is placed against it.

## Scale
- Finished videos run from about 4 minutes to over an hour; most are 8–15 minutes.
- Raw footage per project runs from about 10 minutes to 3+ hours. Design for up to 90 minutes finished and 5 hours raw.
- Sources are mostly 1080p60, sometimes 1440p60, with an occasional 30 or 24 fps clip.
- Machine: i5-13600K, 32 GB RAM, Radeon RX 6900 XT (16 GB), 2560×1440 display.

## Success criteria (v1 is done when)
1. Joshua records narration inside Cutroom (or imports a recording, including OBS video files) along with its script, and Cutroom automatically removes dead space, mouth noises (breaths, swallows, clicks) and retakes, keeping the last good take of each line. It never removes substantive speech, including ad-libs that aren't in the script.
2. Every cleanup decision appears in a review list with low-confidence decisions first. Each can be auditioned and toggled with one key.
3. Cutroom subtitles the video accurately on its own, using the script to spell names and Overwatch terms correctly.
4. Joshua chooses where subtitles appear: only the intro, the whole video, or any chosen ranges.
5. Subtitles have saved style presets.
6. The narration splits into script slots that Joshua fills with footage from a searchable, tagged library. Clips auto-trim to fit their slot.
7. A blurred-footage text card (with line-by-line reveal synced to the narration), a branded-background card, and a freeze-frame outline can each be made in one step. They look good with default settings.
8. Section headers, the title card and the end screen come from saved templates. Section headers and YouTube chapter timestamps are generated from the script's headings.
9. Narration always takes focus: music and game audio duck automatically under speech.
10. A finished video exports as YouTube-ready 1080p60 MP4 with AMD hardware encoding.
11. Joshua edits a real video end to end in Cutroom without opening Resolve.
12. AI drafts blur-card text (2–3 versions to pick from), suggests a visual plan for the script, and drafts YouTube packaging in his channel's style. It runs on his Claude or Codex subscription, never a paid API.

## Inputs
- Narration recording (one long file per video)
- Script (pasted text or a .docx/.txt exported from Google Docs; no Google sign-in)
- Gameplay footage from many sources: OBS full matches, practice range clips, other creators' footage
- Background music, SFX, images and animated backgrounds from Joshua's existing asset folders
- Facecam (occasionally)

## Features
### 0. Narration recorder
- Records the mic straight to a clean WAV inside Cutroom, replacing the OBS-then-strip-video routine.
- The uploaded script sits in a readable panel that Joshua scrolls himself. No auto-scrolling teleprompter.
- An optional retake key (`R`) marks "I'm redoing this line". Cleanup treats the mark as strong evidence, but works the same without it.
- Shows a level meter and a clipping warning, and saves continuously, so a crash never loses a take.
- External recordings are still imported, including OBS video files; Cutroom takes the audio and ignores the picture.

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
- Shown only on chosen time ranges: intro only, the whole video, or custom ranges. **The default is the intro only**, which is Joshua's normal use.
- Saved style presets: font, size, color, outline/shadow, position, and in/out animation. The default preset is a bold white sans-serif with a thick black outline and soft shadow, placed lower-middle.
- Subtitles hide automatically while a text card is on screen. Joshua can override this per region.
- SRT export for YouTube closed captions.

### 3. Footage matching (the biggest time sink)
- **Script slots.** The cleaned narration is split into slots, one per script sentence or paragraph. A slot panel shows the script text, each slot's status (empty, footage, or card), and its duration. Joshua jumps between empty slots by hotkey.
- **Intro mode.** In the intro, slots split per phrase (about 2–4 s) instead of per sentence, so rapid cuts are just "next slot, fill, next slot, fill".
- **Fast fill.** Joshua marks a moment in the source viewer and presses one key. The clip drops into the current slot and trims to fit. He can then nudge the source in-point or adjust the slot length.
- **Footage library.** Indexes the footage folders. Proxies are generated in the background so even a 3-hour recording scrubs instantly. Joshua tags moments by hotkey while scrubbing (hero, map, topic, free notes) and finds them later with fast search.
- Joshua always chooses the footage. Cutroom does not auto-place or auto-recommend clips in v1.

### 4. Asset library
- Indexes Joshua's existing asset folders in place and never moves or renames files.
- Instant search, tags, favorites, "recently used" and per-type filters (music, SFX, image, background, video).
- Hover previews: audio waveform with playback, image and video thumbnails.

### 5. Cards, overlays and effects
- **Blurred-footage text card (most-used visual).** Blurs and dims the footage underneath, then puts text on top. The text is usually a short or long explanation of a hero ability or concept, less often a short phrase, and occasionally an image or clip instead. One step.
  - **Automatic text layout.** Wrapping, size and line length are chosen automatically, so a full paragraph looks clean without manual placement.
  - **Reveal.** The default is line by line, each line appearing as Joshua says it. Cutroom matches each line to the narration automatically, and Joshua can retime by tapping a key during playback. All-at-once fade is one click away.
- **Branded-background card.** Cuts to a looping animated background (e.g. the crinkled notebook page) with text, an image, or a clip on top. One step.
- **Branded templates.** A title card, an end screen (laid out for YouTube end-screen elements), and section headers. Section headers are placed automatically at each heading in the script.
- **Emphasis kit (no editing skill needed).**
  - Zoom-to-box: drag a rectangle and Cutroom animates in and out.
  - Freeze-frame + outline: one key freezes the frame, Joshua drags a circle, box or arrow, it holds while he talks about it, then resumes.
  - Shake and flash.
  - Animation presets on any layer: pop-in, slide, zoom/punch-in.
- **Transitions.** Fades and sweeps. Changing one is one click, and the default is applied with one key.
- **Auto-paired SFX.** Transitions and zooms can automatically bring a whoosh or impact sound from Joshua's library. Each pairing is a toggle.
- Card templates are saved and reused. Every recipe looks good with its default settings.
- Facecam (occasional): basic picture-in-picture with saved position, size and crop.

### 6. Audio mix
- Three main sources: narration, background music and gameplay audio. Usually two are active at a time.
- **Narration takes focus.** Music and gameplay audio duck automatically under speech. The ducking is drawn on the clips' volume lines, and it is adjusted with simple per-track controls (how far, how fast) plus "don't duck here" ranges, not hand-edited keyframes.
- SFX placement on the timeline.
- Per-clip and per-track volume and fades. No advanced mixing.
- The final mix is loudness-normalized for YouTube.

### 7. Fast cutting
- Keyboard-first, with DaVinci Resolve-style default hotkeys (I/O, J/K/L, blade, ripple delete, jump between edits). Every hotkey can be remapped. Mouse use is optional.
- Joshua finds highlights himself. There is no automatic highlight detection.

### 8. Export
- YouTube 1080p60 H.264 MP4 using AMD hardware encoding (AMF).
- 1440p sources are downscaled, so zooms get extra sharpness for free. 30 and 24 fps sources are conformed to 60 fps.
- YouTube chapter timestamps are generated from the section headers as text to paste into the description.

### 9. AI assistance (on Joshua's subscription)
- **Engine.** Uses the Claude Code or Codex command-line tool Joshua is already signed into, the same way Harness does. He picks a preferred one in settings, and the other is the fallback.
  - No API keys, and no per-use billing beyond his subscription's normal limits.
  - If neither is available, the AI buttons grey out and everything else works.
  - Only text is sent (script, transcript, past titles). Audio and video never leave the PC.
- **Draft card text.** On a blur card or a selected range, AI drafts 2–3 versions of on-screen text from what Joshua says there: condensed explanation, key points as lines, and his exact words trimmed. He picks one with a key and edits freely.
- **Visual plan from script.**
  - On demand (before or after recording), AI reads the script and suggests a visual for each slot: gameplay clip (with a description of what to look for), text card (with draft text), background card, zoom, freeze-frame outline, or section header.
  - Suggestions appear as chips in the slot panel. Applying one creates the card or fills the library search with the clip description. Ignoring costs nothing.
- **YouTube packaging.**
  - Drafts several title options, a description and tags from the script.
  - Learns Joshua's style from his past videos, and keeps his fixed parts (links, socials, sign-off) word for word.
  - Chapters come from the script headings, as before.

## Interface and feel
- **The opposite of Resolve's complexity.** Only what Joshua uses is on screen. There are no node graphs, no curve editors and no buried menus. Blur is a slider. Animation smoothness is a choice of Smooth, Snappy, Bouncy or Linear.
- **Four stages** as tabs across the top, following his workflow: **Record → Clean → Edit → Export**. Each stage shows only what that step needs. Assembling and polishing share one Edit stage, because he polishes while he assembles. The script is loaded at the start of Record.
- **Visual style: modern minimal dark** (mock B in `design/mocks/edit-stage.html`).
  - Near-black background with rounded floating panels separated by gaps instead of borders.
  - One blue-violet accent and bright, distinct clip colors per track type.
  - Roomy spacing.
- **Screens.** Designed for one 2560×1440 monitor and fully usable on his 1080p monitor. The preview can pop out to the other monitor.
- **Edit stage layout:** script slots on the left, preview in the center, and Footage / Assets / Inspector tabs on the right, with the timeline below.
- **Input.** Keyboard-first, but every action is also one click away for when his hand is on the mouse.
- **Editing properties.** Card text, styles, blur and effect settings are all edited in the Inspector panel, not directly on the preview.
- **Motion comes from presets only.** Each preset has a duration slider and an easing choice. There is no keyframe editor.
- **Shortcuts.** A Settings page lists every shortcut, searchable and rebindable. Buttons show their shortcut on hover. There is no command palette and no always-on hint bar.

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
- Paid services or inference API keys (AI runs only through Joshua's existing Claude/Codex subscriptions)

## Later (after v1)
- **Overwatch HUD scanning.** Deterministic reads of the kill feed, hero, ultimate use and map, used to suggest footage for a slot, with the reason shown.
- **Local visual search** (image-text model) to rank footage for fuzzy queries.
- Template library for lower thirds and callouts.
- Further AI helpers (footage search by description, auto-tagging, script feedback), considered only once v1's AI features prove useful.
- Motion tracking, so outlines can follow moving players. In v1, freeze-frame + outline covers this.

## Open questions (resolved by the Phase 0 spikes)
- Which word-alignment approach gives cut boundaries precise enough never to clip a word?
- Which local denoiser sounds best on Joshua's mic: DeepFilterNet or RNNoise?
- Is a native D3D11 compositor hosted in WPF smooth enough for 1080p60 multi-layer preview with blur?
