# Signature Recognizer Workspace

This folder is the starter training workspace for a BobTools signature-specific recognizer.

## Current scaffold status

The current scaffold provides:

- a dataset workspace layout
- CSV manifest locations
- placeholder Python scripts for training/evaluation/export
- a BobTools zip-import script for user-submitted live scan exports
- a .NET exporter project in `BobTools/Tools/SignatureDatasetExporter`

The exporter currently does the following:

- enumerates `BobTools/Assets/TestImages/scanning`
- copies the raw fixture images into `data/raw`
- generates normalized signature crops into `data/crops`
- derives starter labels from filenames where possible
- marks the known hard cases as `test`
- runs the current BobTools OCR pipeline across the fixtures and writes a baseline report
- writes:
  - `data/manifests/labels.csv`
  - `data/manifests/train.csv`
  - `data/manifests/val.csv`
  - `data/manifests/test.csv`
  - `outputs/reports/current_ocr_baseline.csv`
  - `outputs/reports/current_ocr_baseline_summary.txt`

The crop exporter uses icon-guided template matching to find the signature value area, then normalizes that crop onto a fixed canvas for training. If the icon cannot be located, it falls back to a deterministic top-right window so every fixture still gets a crop file.

## Suggested first-time workflow

1. Export the starter manifests and raw fixture copies:

```powershell
dotnet run --project .\BobTools\Tools\SignatureDatasetExporter\SignatureDatasetExporter.csproj -- --overwrite
```

2. Create a Python virtual environment in this folder.

3. Install dependencies:

```powershell
pip install -r .\tools\signature-recognizer\requirements.txt
```

4. Review and correct `data/manifests/labels.csv` as needed.

5. Inspect `outputs/reports/current_ocr_baseline.csv` to see what the current BobTools OCR stack predicts for each fixture.

6. Run baseline evaluation and then training scripts once they are implemented beyond the current scaffold.

## Planned next milestones

- add a first model training script
- add evaluation reports against the frozen test set
- add ONNX export after model quality is proven

## BobTools export ingestion

BobTools can now export a session zip that contains:

- the live scan images for that session
- `scan_training_labels.csv` with detected and corrected labels
- `session.json` with session metadata

Use the importer to turn one or more user-submitted zip files into a consolidated manifest:

```powershell
python .\tools\signature-recognizer\scripts\import_bobtools_exports.py .\incoming-zips
```

This writes:

- `tools/signature-recognizer/data/imports/bobtools_exports/...`
- `tools/signature-recognizer/data/manifests/bobtools_exports.csv`
- `tools/signature-recognizer/data/manifests/bobtools_exports_approved.csv`
- `tools/signature-recognizer/outputs/reports/bobtools_export_ingest_summary.txt`

The importer treats rows as approved for training when:

- the export row is in `Processed` state
- a `correct_value` is present
- at least one image was found in the zip

For now, `preferred_image_path` points at the best available imported image from the zip, usually the processed preview if present, otherwise the raw source image.

## Recommended collection process

1. Ask users to export a session zip from BobTools after they review and correct scan labels.
2. Store the submitted zip files in a local `incoming-zips` folder.
3. Run the importer across that folder.
4. Review `data/manifests/bobtools_exports_approved.csv` for obvious bad labels or junk captures.
5. Promote approved rows into the next model-training dataset.
6. Keep a held-out subset of user exports for validation and test before shipping a new ONNX model.

## Practical training plan

1. Collection
  Ask users for zip exports from varied ships, HUD scales, screen resolutions, lighting, and background clutter.
2. Intake
  Run `import_bobtools_exports.py` against each batch of submitted zips and archive the original zip files separately.
3. Review
  Filter out blank labels, obviously wrong corrections, and poor-quality captures. Preserve corrected examples where OCR was wrong because those are the highest-value samples.
4. Dataset shaping
  Start with `bobtools_exports_approved.csv` as the intake table. If the imported processed preview is too wide or inconsistent, add a follow-up crop-normalization step before model training.
5. Split strategy
  Reserve a user-level holdout. Avoid putting exports from the same user session into both training and test.
6. Baseline comparison
  Compare new model accuracy against `outputs/reports/current_ocr_baseline.csv` so you measure improvement over the current Tesseract path.
7. ONNX packaging
  Only export ONNX after the new model beats the current pipeline on the held-out user-export set as well as the frozen fixture set.

## Suggested next tooling steps

- add a crop-normalization stage for imported BobTools exports
- teach `train.py` to read `bobtools_exports_approved.csv`
- add a dedupe strategy based on image hash, not just `session_id + entry_id`
- add reviewer fields such as `approved_by`, `rejected_reason`, and `reviewed_at`