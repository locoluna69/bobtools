# Signature Recognizer Workspace

This folder is the starter training workspace for a BobTools signature-specific recognizer.

## Current scaffold status

The current scaffold provides:

- a dataset workspace layout
- CSV manifest locations
- placeholder Python scripts for training/evaluation/export
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