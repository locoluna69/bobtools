# BobTools

BobTools is a Windows WPF helper app for Star Citizen that captures screen regions from the active client, runs OCR, and supports both signature scanning and mining workflows.

## Current capabilities

- Separate `Signature` and `Mining` workflows with saved capture regions
- Live OCR review for scan and mining results
- Offline replay/testing for captured images
- SQLite-backed internal persistence for live results and session history
- Paged live result views with lazy preview image loading
- Review-state workflows for curation and export
- Mining CSV/JSON output and scan-training export batches
- Unit test project with Windows CI coverage

## Solution layout

- `MainWindow.xaml` and `MainWindow.xaml.cs` host the main shell and startup wiring
- `ViewModels/MainViewModel.cs` owns most application workflow state
- `Services/` contains OCR, settings, persistence, capture, and profile-image services
- `Utils/` contains catalogs, OCR parsing helpers, and file exporters
- `Models/` contains application settings, OCR results, and live/offline result items
- `Assets/data/` contains reference data such as `commodities.json` and `signatures.csv`
- `Docs/Memory/` contains handoff notes and project-state documentation
- `BobTools.Tests/` contains the unit test project

## Persistence and data locations

Internal application state now uses SQLite:

- `LocalAppData/BobTools/bobtools.db`

Legacy files are still recognized for migration:

- `LocalAppData/BobTools/live-results.json`
- `LocalAppData/BobTools/Sessions/{sessionId}/session.json`

Other runtime outputs remain file-based:

- `LocalAppData/BobTools/DebugCaptures`
- `LocalAppData/BobTools/Exports/scan-training/{batchId}.zip`
- `LocalAppData/BobTools/RockScans/rock_scans.csv`
- `LocalAppData/BobTools/RockScans/Json`
- `LocalAppData/BobTools/ProfileImages`
- `LocalAppData/BobTools/logs`

## Build and run

Requirements:

- Windows
- .NET 8 SDK

Commands:

```powershell
dotnet build BobTools.sln
dotnet run --project BobTools.csproj
```

## Tests

Run the unit tests with:

```powershell
dotnet test BobTools.Tests/BobTools.Tests.csproj
```

Current test coverage focuses on deterministic logic and service behavior:

- SQLite migration and persistence for global live results
- SQLite migration and persistence for session history
- settings normalization and save/load round-trips
- OCR utility/catalog logic such as ore tiers and rock scan parsing

GitHub Actions runs the same suite on Windows using:

- `.github/workflows/dotnet-tests.yml`

## UI and performance notes

- Live scan and mining review lists are paged to 25 results at a time
- Preview images are loaded only for the currently visible page
- Live review state is restored from SQLite on startup

## Important files

- `ViewModels/MainViewModel.cs`
- `Services/GlobalResultsStore.cs`
- `Services/SessionHistoryService.cs`
- `Services/SettingsService.cs`
- `Services/OcrService.cs`
- `Utils/SignatureCatalog.cs`
- `Utils/RockScanParser.cs`
- `BobTools.Tests/StoreTests.cs`
- `BobTools.Tests/SettingsServiceTests.cs`

## Additional project notes

See these for deeper context:

- `Docs/Memory/CURRENT_STATE.md`
- `Docs/Memory/README.md`
- `Docs/Memory/2026-03-15-handoff.md`
- `Docs/Memory/2026-03-17-ocr-performance-tuning.md`