# Memory / Handoff Notes

This folder is the project memory bank for BobTools.

It complements the top-level project guide in `README.md` by tracking session-to-session engineering context.

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
- `2026-03-18-bobtools-export-training-guide.md` — step-by-step workflow for turning BobTools user export zips into reviewed training manifests.

## What should stay current

- `CURRENT_STATE.md` should reflect the real runtime architecture, storage model, and major workflows.
- `README.md` at the repo root should reflect how to build, run, and test the app.
- Handoff files should capture why a change was made when that reasoning is not obvious from code.
