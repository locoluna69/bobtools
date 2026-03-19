# Memory / Handoff Notes

This folder is the project memory bank for BobTools.

## Purpose

Use this folder to keep session-to-session context that is expensive to rediscover, including:

- what changed
- why it changed
- current architectural decisions
- known tradeoffs
- OCR tuning notes
- likely next steps

## Suggested workflow

- Add a dated handoff file when a work session ends.
- Prefer short, factual notes over long transcripts.
- Record both outcomes and reasoning.
- Include file names and key symbols when useful.
- If a tuning change is data-driven, note the sample image or debug folder that motivated it.

## Current files

- `CURRENT_STATE.md` — short always-current snapshot of the project.
- `2026-03-15-handoff.md` — initial project history and current-state summary.
- `2026-03-17-ocr-performance-tuning.md` — detailed notes on signature OCR instrumentation, tuning, drift observations, and remaining gaps.
- `2026-03-18-signature-recognizer-plan.md` — concrete plan for building and integrating a signature-specific recognizer for BobTools.
