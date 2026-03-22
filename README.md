# BobTools

BobTools is a Windows WPF/.NET 8 OCR companion app for Star Citizen workflows. It captures configured regions from the visible game client, runs OCR, and supports both signature scanning and mining review flows.

## Current capabilities

- Separate `Signature` and `Mining` workflows with independent capture regions
- Global hotkey capture with in-app hotkey configuration
- Live result review and filtering for both scan and mining results
- Offline image replay/testing for OCR tuning and correction
- SQLite-backed internal persistence for live results and session history
- Paged live result views with lazy preview image loading
- Mining CSV/JSON export and scan-training batch export workflows
- Persisted session context including RSI handle, ship, system, and location
- Unit test project and Windows CI test workflow

## Repository layout

- `BobTools/` - main WPF application project
- `BobTools/BobTools.Tests/` - xUnit test project
- `BobTools/Docs/Memory/` - handoff notes and project-state documentation
- `BobTools/Assets/data/` - reference data such as `commodities.json` and `signatures.csv`
- `BobTools/Tools/` - support tooling such as the signature dataset exporter
- `.github/workflows/` - CI workflows

## Important runtime behavior

- Signature OCR uses a specialized icon-guided path when appropriate and matches results against `signatures.csv`
- Mining OCR uses the general OCR pipeline plus a structured rock scan parser
- Live review state is restored on startup from `LocalAppData/BobTools/bobtools.db`
- Legacy JSON stores are migrated into SQLite on demand
- Live scan and mining views page 25 rows at a time to keep the UI responsive

## Persistence and outputs

Primary internal store:

- `LocalAppData/BobTools/bobtools.db`

Legacy migration sources:

- `LocalAppData/BobTools/live-results.json`
- `LocalAppData/BobTools/Sessions/{sessionId}/session.json`

Other outputs:

- `LocalAppData/BobTools/DebugCaptures`
- `LocalAppData/BobTools/Exports/scan-training/{batchId}.zip`
- `LocalAppData/BobTools/RockScans/rock_scans.csv`
- `LocalAppData/BobTools/RockScans/Json`
- `LocalAppData/BobTools/ProfileImages`
- `LocalAppData/BobTools/logs/startup-error-*.log`

## Prerequisites

- Windows
- .NET 8 SDK
- Visual Studio 2022 or newer with desktop development workload, or the `dotnet` CLI
- `eng.traineddata` available under `BobTools/Assets/tessdata`

## Build and run

From the repository root:

```powershell
dotnet build BobTools.sln
dotnet run --project .\BobTools\BobTools.csproj
```

## Tests

Run tests from the repository root with:

```powershell
dotnet test .\BobTools\BobTools.Tests\BobTools.Tests.csproj
```

Current automated coverage includes:

- SQLite migration and persistence behavior
- settings normalization and round-trip behavior
- deterministic OCR utility and parsing logic

GitHub Actions runs the Windows test suite on push and pull request.

## First run

1. Open `BobTools.sln` in Visual Studio, or use the `dotnet` CLI from the repo root.
2. Restore packages and build.
3. Run the app.
4. Configure the `Signature` and/or `Mining` capture regions.
5. Confirm session context such as RSI handle, ship, system, and location.
6. Use the configured hotkey or the capture controls in the UI.
7. Review live results inline or use the offline workflow for replay and correction.

## Signature dataset workflow

- Raw scan captures can still be collected with `Capture only` mode.
- The signature dataset exporter under `BobTools/Tools/SignatureDatasetExporter` supports ingesting labeled captures with `--ingest-csv`.
- The ingest CSV format is `source_image_path,expected_text,destination_file_name`.

Example:

```powershell
dotnet run --project .\BobTools\Tools\SignatureDatasetExporter\SignatureDatasetExporter.csproj -- --ingest-csv .\BobTools\Tools\SignatureDatasetExporter\ingest-template.csv --ingest-only --overwrite
```

## Safety boundaries

BobTools is intentionally limited to user-triggered capture and OCR workflows:

- no memory reading
- no code injection
- no gameplay automation
- no unattended input generation

## Additional documentation

- `BobTools/README.md`
- `BobTools/Docs/Memory/README.md`
- `BobTools/Docs/Memory/CURRENT_STATE.md`
