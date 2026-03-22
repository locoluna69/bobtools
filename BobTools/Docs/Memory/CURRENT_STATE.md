# Current State

## What this app is

BobTools is a Windows WPF overlay app for Star Citizen that:

- captures user-defined screen regions from the visible Star Citizen client area
- runs OCR on those captures
- supports separate `Signature` and `Mining` workflows
- provides offline OCR testing and replay/debug tooling

## Current most important behaviors

### Shared persistence and review workflow

- Live scan and live mining rows are now persisted in a canonical global store at `LocalAppData/BobTools/live-results.json`.
- Session history still exists under `LocalAppData/BobTools/Sessions/{sessionId}/session.json`, but it is no longer the primary mutable store for live review state.
- The Settings dialog can clear the canonical live-results store without deleting session history or exported artifacts.
- Rehydrated live rows are restored from the canonical store on startup.

### Signature

- Signature OCR uses a specialized icon-guided path when OCR mode is `Auto` or `Rightmost Numeric Value`.
- The app looks for `Assets/images/signature_icon.png`, crops candidate regions to the right, OCRs those numeric regions, and ranks the results.
- Signature OCR results are matched against `Assets/data/signatures.csv`.
- Signature OCR now includes several tuning layers beyond the original icon-guided crop:
  - pooled numeric/default Tesseract engines
  - append-only JSONL pipeline timing logs under `LocalAppData/BobTools/Logs`
  - reduced candidate-region search and early-stop on strong numeric matches
  - rejection of implausible icon matches and weak fast-path results
  - catalog-aware signature correction for common `5`/`6`/`8` OCR drift
  - signature-specific fallback scoring that prefers clean numeric tokens over verbose garbage OCR blocks
- The UI displays the matched resource in:
  - the live results tab
  - the offline test tab
  - the live review dialog
- Live scan rows now support a review-driven dataset workflow:
  - editable `CorrectValue` with debounced auto-save
  - review state `Pending`, `Reviewed`, or `Rejected`
  - export metadata for reviewed scan records
  - live-results filtering by review state
- Settings now shows a scan dataset summary and can purge rejected scan records from the canonical store while deleting only unreferenced source, processed, and debug artifacts.
- Scan export is now batch-based rather than session-based. Reviewed and unexported scan rows are written to `LocalAppData/BobTools/Exports/scan-training/{batchId}.zip` with:
  - `manifest.csv`
  - `images/source/...`
  - `images/processed/...`

### Mining

- Mining still uses the general OCR pipeline.
- Mining OCR now has a first-pass structured parser for rock scan data.
- Parsed mining scans can be appended to `LocalAppData/BobTools/RockScans/rock_scans.csv`.
- Parsed mining scans are also saved as JSON. When a debug capture folder exists, the JSON is written into that same debug folder as `rock_scan.json`; otherwise it falls back to `LocalAppData/BobTools/RockScans/Json`.
- The UI now shows a mining rock summary in both live and offline results.
- Shared control styling and theme brushes now live in `App.xaml`, and `MainWindow`, `ReviewWindow`, `RegionSelectorWindow`, and `CaptureOverlayWindow` use the same visual language.
- `App.xaml` now also provides custom `ComboBox` and `TabItem` templates so dropdowns and tab selection match the rest of the theme instead of using default WPF chrome.
- Mining rock exports now follow the updated sample contract:
  - app-lifetime `SESSIONID`
  - numeric `MASS`, `RESISTANCE`, `INSTABILITY`, and `COMPOSITION`
  - `Elements` array for the per-material breakdown
  - `PARSE_WARNINGS` for reconstructed or incomplete fields
- Mining OCR now normalizes parsed material names against `Assets/data/commodities.json`, filtered to extractable mining commodities and still tolerant of common OCR/spelling drift.
- Offline mining now supports a queued multi-image workflow: drop multiple images, run OCR across the batch, inspect the selected OCR image/debug artifact, edit the structured scan result inline, add or delete element rows, and save corrected JSON/CSV output.
- Offline mining results now track unsaved edits and visually highlight the save buttons when field or element-row changes need to be persisted.
- Live mining rows now mirror the scan review workflow in full view:
  - editable mining fields with debounced auto-save
  - review state `Pending`, `Reviewed`, or `Rejected`
  - live-results filtering by review state
  - Settings summary of mining review counts
  - purge of rejected mining rows from the canonical store with reference-safe file cleanup
- Session context now includes a persisted `RSI handle` alongside ship, system, and location; it is required before capture/processing and is written into saved PNG metadata.
- The app now also attempts to load and display the public RSI citizen profile image for the current RSI handle, caching it under `LocalAppData/BobTools/ProfileImages` when the profile exposes a public avatar.
- The top-left header now includes persisted lifetime counters for completed `Signatures`, meaningful `Rocks`, and tiered ore finds: `Common` for quality `0-749`, `Rare` for `750-800`, `Legendary` for `801-899`, and `Mythic` for `900-999`. Only Rare/Legendary/Mythic use special highlights in mining results. Legacy `High Quality` totals migrate into the `Mythic` counter.

## Important files

- [MainWindow.xaml](MainWindow.xaml)
- [ViewModels/MainViewModel.cs](ViewModels/MainViewModel.cs)
- [Services/OcrService.cs](Services/OcrService.cs)
- [Utils/SignatureCatalog.cs](Utils/SignatureCatalog.cs)
- [Utils/RockScanParser.cs](Utils/RockScanParser.cs)
- [Utils/RockScanCsvStore.cs](Utils/RockScanCsvStore.cs)
- [Utils/RockScanJsonStore.cs](Utils/RockScanJsonStore.cs)
- [Assets/data/SampleRock.json](Assets/data/SampleRock.json)
- [Assets/data/commodities.json](Assets/data/commodities.json)
- [Assets/data/signatures.csv](Assets/data/signatures.csv)
- [Views/ReviewWindow.xaml](Views/ReviewWindow.xaml)
- [Views/ReviewWindow.xaml.cs](Views/ReviewWindow.xaml.cs)
- [Docs/Memory/2026-03-15-handoff.md](Docs/Memory/2026-03-15-handoff.md)
- [Docs/Memory/2026-03-17-ocr-performance-tuning.md](Docs/Memory/2026-03-17-ocr-performance-tuning.md)

## Current OCR workflow summary

### Live capture

1. capture selected Signature or Mining region
2. run OCR
3. update OCR mode/source summary
4. if Signature, resolve the resource from the CSV
5. if Mining, build a rock summary and export accepted scans to both CSV and JSON
6. persist or refresh the corresponding live row in the canonical global store
7. review and edit the live row inline from the Live Results tab

### Offline capture

1. drop image into the offline tab
2. use saved region if it fits; otherwise use the whole image
3. run OCR
4. if Signature, resolve the resource from the CSV
5. if Mining, build a rock summary and export the parsed scan to both CSV and JSON
6. show OCR text and structured summaries in the offline panel

## Current data/debug locations

- Canonical live results store: `LocalAppData/BobTools/live-results.json`
- Session history: `LocalAppData/BobTools/Sessions/{sessionId}/session.json`
- Local debug captures: `LocalAppData/BobTools/DebugCaptures`
- Scan training batch exports: `LocalAppData/BobTools/Exports/scan-training/{batchId}.zip`
- Mining scan CSV: `LocalAppData/BobTools/RockScans/rock_scans.csv`
- Mining scan JSON: saved into the matching debug capture folder when available, otherwise `LocalAppData/BobTools/RockScans/Json`
- Mining session ID: generated once when the app starts and reused until the app closes
- Saved PNGs written by BobTools include `BobTools.*` text metadata such as session ID, RSI handle, ship, system, and location.
- Signature lookup data: [Assets/data/signatures.csv](Assets/data/signatures.csv)
- Mining material reference data: [Assets/data/commodities.json](Assets/data/commodities.json)
- Signature icon template: [Assets/images/signature_icon.png](Assets/images/signature_icon.png)

## Known-good current decisions

- Keep capture constrained to Star Citizen only.
- Keep Signature and Mining as separate saved regions.
- Use saved debug artifacts for OCR tuning.
- Use icon-guided OCR only for Signature, not globally.
- Use divisor-based CSV lookup for Signature resource matching.
- Use pattern-based field extraction for Mining and log parsed scans to CSV/JSON.
- Preserve partial mining elements with parse warnings instead of dropping them when OCR misses a field.
- Keep `Assets/data/commodities.json` current, because the mining parser now uses its extractable commodity entries as the canonical material list for normalization.
- Offline mining edits persist by overwriting `rock_scan.json` for the selected result and upserting the matching CSV row by rock scan `ID`.
- Use review state on live rows as the gate for dataset curation instead of the old session-export model.

## Good next steps if work resumes

- add resource-aware replay CSV output
- re-evaluate Signature resource after edited review text is accepted
- add quick-open for the latest debug capture folder
- add more visual debugging around icon match and crop region
- improve mining field extraction robustness for noisier OCR cases
- add `buildId` or app version to OCR log entries for exact build-to-build drift analysis
- add `sourceImageName` or scenario labels to OCR log entries for offline test-set comparisons
- add explicit pipeline outcome labels such as `fast-success`, `fast-rejected`, and `fallback-numeric`
- consider a secondary icon-search pass constrained to the expected in-game UI band for the last difficult signature images

## If something breaks

Check these first:

- [Services/OcrService.cs](Services/OcrService.cs) for OCR behavior
- [ViewModels/MainViewModel.cs](ViewModels/MainViewModel.cs) for live/offline flow
- newest debug capture folder in `LocalAppData/BobTools/DebugCaptures`
- [Docs/Memory/2026-03-15-handoff.md](Docs/Memory/2026-03-15-handoff.md) for the full reasoning/history
