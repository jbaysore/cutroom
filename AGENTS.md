# Cutroom

Cutroom is a video editing application (timeline-based non-linear editor) for Windows. This file is the shared guide for AI coding agents working in this repo. `CLAUDE.md` points here.

## Status
Greenfield. No stack has been chosen yet. Record the decision below once made.

- Stack: _TBD_
- Media engine: _TBD_ (e.g. FFmpeg / libav, GStreamer, or MLT)
- Build / test / run commands: _TBD_ (fill in as soon as they exist)

## Goals
- Multi-track timeline editing: cut, trim, split, move, ripple delete.
- Non-destructive edits: source media is never modified; a project is a document that references media.
- Fast preview playback with proxy/cache support; export through a separate render pipeline.
- Project files are human-diffable (JSON) and versioned.

## Working rules
- Keep the editing model (timeline, clips, edits) separate from UI and from the media/codec layer so it can be unit tested without video files.
- Never commit media files, renders, caches, or large binaries. Use small generated fixtures for tests.
- Add tests with new editing-model logic; verify before reporting done and report results honestly.
- Prefer small, focused commits. Do not commit secrets or machine-specific paths.
- Keep this file current: when you add the stack, commands, or architecture, update it.

## Layout
_To be filled in as the project structure is created._
