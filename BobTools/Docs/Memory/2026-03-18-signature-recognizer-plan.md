# Signature Recognizer Implementation Plan — 2026-03-18

## Goal

Replace or supplement the current Tesseract-based Signature OCR path with a signature-specific recognizer that is more accurate on BobTools scan images, especially on the recurring hard cases.

The recognizer should remain local-first and should fit the current BobTools architecture rather than replacing the whole OCR stack at once.

## Why this is a good fit for BobTools

The Signature workflow is already more constrained than generic OCR:

- the app already locates a stable UI icon anchor
- the value region is to the right of that icon
- the character space is small
- the outputs map to known signature multiples from `Assets/data/signatures.csv`
- the current failure modes are repetitive rather than random

That makes this much closer to a small visual recognition problem than open-ended OCR.

## Recommended architecture

### Keep the current flow shape

Do not replace the whole OCR architecture.

Instead, preserve the current high-level Signature flow:

1. capture the saved Signature region
2. locate the signature icon
3. crop the candidate numeric region
4. run a signature-specific recognizer on that crop
5. resolve the recognized number against `Assets/data/signatures.csv`
6. keep the current fallback path available while the new recognizer is being evaluated

### Add a separate recognizer abstraction

Introduce a dedicated recognizer interface rather than overloading `IOcrService` with model-specific logic.

Recommended new contract:

- `ISignatureRecognizer`
- responsibilities:
  - accept a cropped signature value bitmap
  - return the predicted numeric string
  - return a confidence score
  - optionally return per-character confidence or a model source label

Suggested result type:

- `SignatureRecognitionResult`
  - `PredictedText`
  - `Confidence`
  - `RecognizerName`
  - `WasCorrectedByCatalog`
  - optional `Notes`

## Recommended model approach

### First choice: sequence recognizer

Use a sequence recognizer that reads the entire cropped numeric region and outputs the full string.

Good fit because:

- commas are meaningful
- per-digit segmentation can be error-prone
- the current crop already preserves enough context

Practical model families:

- CRNN with CTC decoding
- small CNN + recurrent decoder
- small transformer text recognizer if inference cost stays acceptable

### Alternative: constrained classifier

If the output space is narrow enough in practice, a constrained classifier over known valid signature multiples is also viable.

Use this if:

- the set of expected outputs is sufficiently bounded
- inference speed matters more than output flexibility

Tradeoff:

- simpler deployment and decoding
- less flexible if the game UI or signature catalog expands materially

### Avoid first-pass digit segmentation

Do not start with per-digit segmentation unless experimentation shows the cropped regions are extremely stable.

Reason:

- segmentation would become another failure source
- the current issue is already hard enough without introducing a second brittle vision step

## Data requirements

### Training set inputs

The dataset should be built from the existing offline scan fixtures and debug captures.

Minimum data fields:

- `sourceImagePath`
- `cropImagePath`
- `expectedText`
- `expectedResource`
- `isHardCase`
- `notes`

Recommended additional metadata:

- `uiScale` if known
- `captureMode` such as `offline-test` or `live-debug`
- `iconLocateOutcome` if captured from logs
- `currentTesseractText`
- `currentTesseractMatchedResource`

### Data sources already available in the repo/workflow

- `BobTools/Assets/TestImages/scanning`
- `LocalAppData/BobTools/DebugCaptures`
- `LocalAppData/BobTools/Sessions/.../session.json`
- `LocalAppData/BobTools/Logs/ocr-pipeline-*.jsonl`

### Immediate hard-case priority set

These should be included and explicitly tagged as hard cases:

- `01_7110.png`
- `01_6000.png`
- `01_8000.png`
- `01_12855.png`
- `01_16720.png`
- any known `5`/`6`/`8` confusion examples

### Dataset size guidance

Initial prototype target:

- a few hundred labeled crops

Better target for meaningful evaluation:

- `1000+` labeled crops with a deliberate bias toward hard cases and near-miss cases

## Recommended repository structure

Keep training assets and app runtime assets clearly separated.

Suggested additions:

- `BobTools/Assets/Models/`
  - production ONNX model files if a model is adopted
- `BobTools/Docs/Memory/`
  - model evaluation notes and decision logs
- `tools/signature-recognizer/`
  - training scripts
  - preprocessing/export scripts
  - evaluation scripts
  - dataset manifests

Suggested dataset layout outside the shipping app binary path:

- `tools/signature-recognizer/data/raw/`
- `tools/signature-recognizer/data/crops/`
- `tools/signature-recognizer/data/manifests/labels.csv`
- `tools/signature-recognizer/data/manifests/train.csv`
- `tools/signature-recognizer/data/manifests/val.csv`
- `tools/signature-recognizer/data/manifests/test.csv`

## App integration points

### Existing files most likely to change

- `Services/OcrService.cs`
- `ViewModels/MainViewModel.cs`
- `Models/OcrResult.cs`
- `Utils/SignatureCatalog.cs`

### New files likely needed

- `Services/ISignatureRecognizer.cs`
- `Services/OnnxSignatureRecognizer.cs`
- `Models/SignatureRecognitionResult.cs`
- optional configuration in `Models/AppSettings.cs`

### Integration strategy

Use a feature-flag style rollout.

Suggested runtime modes:

- `TesseractOnly`
- `ModelOnly`
- `ModelThenTesseractFallback`
- `TesseractThenModelVerify`

Recommended first production candidate:

- `ModelThenTesseractFallback`

Reason:

- it keeps the model path local and fast
- it preserves the current system as a safety net

## Evaluation plan

### Metrics that matter

Do not evaluate only by raw OCR text accuracy.

Track at least:

- exact numeric match rate
- resource match rate
- hard-case subset accuracy
- confusion counts for `5`, `6`, and `8`
- median inference latency

### Baselines to compare

At minimum compare:

1. current Tesseract signature path
2. current Tesseract signature path + catalog correction
3. candidate signature recognizer

### Evaluation outputs

For each test image, save:

- expected text
- current Tesseract text
- model text
- expected resource
- current Tesseract resource
- model resource
- timing for each path
- whether either path required correction

This should produce a CSV or JSON report that can be reviewed by hand.

## Training workflow

### Step 1. Export crops and labels

Build a script that:

- reads the known scan fixture set
- uses the existing icon-guided crop logic or debug artifacts
- exports a stable crop per example
- writes a label manifest

### Step 2. Train a first recognizer offline

Use a Python training workspace first.

Reason:

- faster iteration
- richer ML tooling
- easier experimentation before app integration

### Step 3. Export to ONNX

Target ONNX for app integration.

Reason:

- good .NET integration
- local inference
- avoids Python dependency at runtime

### Step 4. Add runtime inference path in BobTools

Integrate the recognizer behind `ISignatureRecognizer`.

### Step 5. Run head-to-head evaluation on the offline suite

Do not replace the current path until the model clearly wins on:

- exact numeric accuracy
- resource accuracy
- hard-case accuracy
- latency that is acceptable for local use

## Logging updates that should happen before or during this work

These should be added to make evaluation easier:

- `buildId` or app version in OCR logs
- `sourceImageName` or scenario ID in OCR logs for offline runs
- explicit pipeline outcome tags
- recognizer source in results, for example:
  - `tesseract-signature`
  - `onnx-signature-model`
  - `model-fallback-tesseract`

## Suggested implementation phases

### Phase A. Data/export foundation

- add dataset export scripts
- produce labeled crop manifest
- freeze a benchmark test set

### Phase B. First model prototype

- train first recognizer offline
- export ONNX
- evaluate on frozen benchmark set

### Phase C. App-side optional integration

- add recognizer abstraction
- add ONNX inference path
- add feature flag / settings toggle

### Phase D. Comparative evaluation

- compare Tesseract vs model on the offline suite
- compare both exact text and resolved resource

### Phase E. Gradual adoption

- use the model for Signature only
- keep Mining on the current OCR path
- keep Tesseract as fallback until confidence is high

## Practical recommendation

The next engineering step should not be model training immediately.

The next step should be building the dataset/export/evaluation harness first.

Reason:

- without a stable benchmark set, it will be impossible to know if the model genuinely beats the tuned Tesseract path

## Concrete next action if work starts

Implement a dataset export tool that turns the current scan fixtures and debug captures into a labeled manifest and normalized crop set for training.
