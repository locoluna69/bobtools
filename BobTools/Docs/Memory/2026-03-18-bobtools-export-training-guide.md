# BobTools Export Training Workflow

> Status note: BobTools now exports reviewed scan data as batch zips containing `manifest.csv` plus copied source and processed images. The downstream importer in `tools/signature-recognizer/scripts/import_bobtools_exports.py` still expects the older legacy session-zip format with `scan_training_labels.csv` and `session.json`. This guide now documents both the current BobTools export format and the current importer limitation.

This guide explains how to go from BobTools live-scan exports to a training-ready manifest set for the signature recognizer workspace.

## What this workflow does today

The current tooling supports these stages:

1. Collect reviewed scan-export batch zip files from BobTools users.
2. Normalize those exports into a format the current recognizer importer can consume, or update the importer to read the batch manifest directly.
3. Review the imported manifest and remove obviously bad samples.
4. Prepare deduplicated `train`, `val`, and `test` manifests from the approved rows.

The current tooling does **not** yet train a model end-to-end. The last step today produces clean resolved manifests that the future training loop will consume.

## Prerequisites

Before you start, make sure you have:

1. A BobTools repo checkout.
2. Python installed and available on `PATH`.
3. One or more BobTools export zip files from users.
4. A Python environment with the recognizer dependencies installed:

```powershell
pip install -r .\tools\signature-recognizer\requirements.txt
```

## Folder locations used by this workflow

- BobTools app docs: `BobTools/Docs/Memory`
- Recognizer workspace: `tools/signature-recognizer`
- Import script: `tools/signature-recognizer/scripts/import_bobtools_exports.py`
- Manifest prep script: `tools/signature-recognizer/scripts/train.py`
- Imported images: `tools/signature-recognizer/data/imports/bobtools_exports`
- Imported manifests: `tools/signature-recognizer/data/manifests`
- Reports: `tools/signature-recognizer/outputs/reports`

## Step 1: Export labeled data from BobTools

For each user session you want to use:

1. Open BobTools.
2. Go to the live scan workflow.
3. Review the live scan rows.
4. Correct the `CorrectValue` field where OCR was wrong.
5. Wait for the row status to show that the label has been saved.
6. Use the Settings export action to create a reviewed scan batch zip.
7. Collect that zip file from the exported session location.

Each zip should contain:

- `manifest.csv`
- `images/source/...`
- `images/processed/...`

The current batch export only includes scan rows that are:

- marked `Reviewed`
- not previously exported
- still present in the canonical live-results dataset

## Step 2: Stage incoming zip files

Create a folder outside or inside the repo to hold incoming user submissions. A simple pattern is:

```text
I:\gh\BobTools\incoming-zips
```

Copy all user-submitted BobTools export zip files into that folder.

Keep the original zip files. They are the raw handoff artifact and should be preserved even after import.

## Step 3: Import the BobTools exports

### Current limitation

The current recognizer importer still expects the older legacy session export format:

- `scan_training_labels.csv`
- `session.json`

It does not yet consume the new `manifest.csv` batch format directly.

That means one of these needs to happen before normal import:

1. Update `import_bobtools_exports.py` to read the new batch `manifest.csv` format.
2. Build a small compatibility conversion step from batch zips to the legacy session-zip shape.

Until that importer change lands, the commands below describe the current recognizer-side import entry point, not a guaranteed out-of-the-box path for new BobTools exports.

From the repo root, run:

```powershell
python .\tools\signature-recognizer\scripts\import_bobtools_exports.py .\incoming-zips
```

If you want the imported rows to default to a different split, use `--split`:

```powershell
python .\tools\signature-recognizer\scripts\import_bobtools_exports.py .\incoming-zips --split val
```

What this script does:

1. Finds all zip files under the input path.
2. Extracts the images into `tools/signature-recognizer/data/imports/bobtools_exports/{sessionId}`.
3. Reads legacy `scan_training_labels.csv` from each zip.
4. Reads legacy session metadata from `session.json` when present.
5. Writes two manifests:
   - `bobtools_exports.csv`
   - `bobtools_exports_approved.csv`
6. Writes an ingest summary report.

Files created by this step:

- `tools/signature-recognizer/data/manifests/bobtools_exports.csv`
- `tools/signature-recognizer/data/manifests/bobtools_exports_approved.csv`
- `tools/signature-recognizer/outputs/reports/bobtools_export_ingest_summary.txt`

## Step 4: Review the approved manifest

Open `tools/signature-recognizer/data/manifests/bobtools_exports_approved.csv` in your editor or spreadsheet tool.

Check these fields first:

1. `expected_text`
2. `preferred_image_path`
3. `detected_value`
4. `processing_state`
5. `selected_targets`
6. `notes`

Remove or fix rows that have problems such as:

1. Blank or obviously wrong corrected labels.
2. Bad captures that do not actually show the signature value.
3. Duplicate junk submissions.
4. Images from unusual UI states that should be held back for later analysis.

Important:

- `bobtools_exports_approved.csv` is the current intake table for training prep.
- The importer already filters for rows that look usable, but it does not replace human review.
- When the importer is updated for batch exports, `manifest.csv` should become the primary upstream input instead of `scan_training_labels.csv`.

## Step 5: Prepare resolved train, val, and test manifests

From the repo root, run:

```powershell
python .\tools\signature-recognizer\scripts\train.py
```

By default this script prefers:

```text
tools/signature-recognizer/data/manifests/bobtools_exports_approved.csv
```

If that file is not present, it falls back to the older starter manifests.

Useful options:

```powershell
python .\tools\signature-recognizer\scripts\train.py --write-json
python .\tools\signature-recognizer\scripts\train.py --allow-unassigned
python .\tools\signature-recognizer\scripts\train.py --manifest .\tools\signature-recognizer\data\manifests\bobtools_exports_approved.csv
```

What this script does:

1. Loads the approved manifest.
2. Validates label presence and minimum length.
3. Resolves the best usable image path for each row.
4. Drops invalid rows.
5. Computes an image hash.
6. Deduplicates rows by image hash plus label.
7. Writes canonical resolved output manifests.
8. Writes a dropped-row report.
9. Writes a summary report.

Files created by this step:

- `tools/signature-recognizer/data/manifests/resolved_train.csv`
- `tools/signature-recognizer/data/manifests/resolved_val.csv`
- `tools/signature-recognizer/data/manifests/resolved_test.csv`
- `tools/signature-recognizer/data/manifests/resolved_dropped_rows.csv` when rows are rejected
- `tools/signature-recognizer/outputs/reports/training_manifest_summary.txt`

If you use `--write-json`, the script also writes:

- `tools/signature-recognizer/data/manifests/resolved_train.jsonl`
- `tools/signature-recognizer/data/manifests/resolved_val.jsonl`
- `tools/signature-recognizer/data/manifests/resolved_test.jsonl`

## Step 6: Inspect the training-prep outputs

After running `train.py`, inspect:

1. `resolved_train.csv`
2. `resolved_val.csv`
3. `resolved_test.csv`
4. `resolved_dropped_rows.csv`
5. `training_manifest_summary.txt`

Look for these issues:

1. Very small validation or test sets.
2. Heavy label imbalance.
3. Too many dropped rows.
4. Duplicates that still slipped through due to label changes on identical images.
5. Paths that point to unexpected source images.

## Step 7: Use the resolved manifests as the canonical training input

At this point, the repo is ready for the actual model-training code to consume:

- `resolved_train.csv`
- `resolved_val.csv`
- `resolved_test.csv`

Those files are now the cleanest source of truth for model-fitting work.

The current intended next engineering step is:

1. Build the real dataset loader and training loop around the resolved manifests.
2. Train against `resolved_train.csv`.
3. Evaluate against `resolved_val.csv` and `resolved_test.csv`.
4. Export ONNX only after the model beats the current OCR baseline.

## Recommended operating rules

Use these rules when collecting data from BobTools users:

1. Prefer varied ships, HUD scales, resolutions, and lighting conditions.
2. Keep exports from a single user session or batch out of both training and final evaluation when possible.
3. Preserve corrected labels even when OCR was badly wrong. Those are often the highest-value samples.
4. Keep original zip files archived separately from imported working data.
5. Review each batch before promoting it into the next training run.

## Quick command summary

Install dependencies:

```powershell
pip install -r .\tools\signature-recognizer\requirements.txt
```

Import BobTools zips with the current legacy importer:

```powershell
python .\tools\signature-recognizer\scripts\import_bobtools_exports.py .\incoming-zips
```

Prepare resolved manifests:

```powershell
python .\tools\signature-recognizer\scripts\train.py
```

Prepare resolved manifests and JSONL output:

```powershell
python .\tools\signature-recognizer\scripts\train.py --write-json
```

## Current limitation

This workflow prepares training data, but it does not yet run the neural-network training loop itself. The repo now has the export path, the import path, and the resolved-manifest generation path in place.

That also means there is currently a format gap between the latest BobTools batch export and the recognizer importer. Keep this guide aligned with whichever side changes next.