# BobTools

A WPF/.NET 8 Windows overlay-style OCR helper for BobTools workflows.

## Included
- WPF desktop shell
- Global hotkey registration (`Ctrl+Shift+Q` by default)
- Full-screen region selector
- Small-region screen capture
- OCR service wrapper using Tesseract
- Editable review/accept dialog
- Simple JSON settings persistence

## Project structure
- `MainWindow` - main control window
- `Views/RegionSelectorWindow` - drag-to-select screen region
- `Views/ReviewWindow` - edit/accept OCR output
- `Services/HotkeyService` - Win32 global hotkey registration
- `Services/ScreenCaptureService` - region screenshot capture
- `Services/OcrService` - OCR + basic preprocessing
- `Services/SettingsService` - saves region/hotkey settings
- `Models` - app settings and OCR result models

## Prerequisites
- Windows
- .NET 8 SDK
- Visual Studio 2022 or newer with desktop development workload
- Tesseract language data files placed in the project folder under `Assets/tessdata`

## First run steps
1. Open `BobTools.sln` in Visual Studio.
2. Restore NuGet packages.
3. Put `eng.traineddata` in the project folder under `Assets/tessdata`.
4. Build and run.
5. Click **Set Signature** or **Set Mining** and drag over the target UI area.
6. Press `Ctrl+Shift+Q` or use the capture controls in the app.
7. Edit OCR output and click **Accept**.

## Good next improvements
- Add multiple named scan regions
- Add image thresholding/upscaling for game UI text
- Add a detached transparent always-on-top mini overlay
- Add output formatting/parsing for Star Citizen-specific text
- Add hotkey editor UI
- Add export/copy-to-clipboard/history
- Add per-region OCR profiles

## Notes
This starter intentionally stays on the safe side of a game companion tool:
- no memory reading
- no injection
- no automation of gameplay actions
- user-triggered capture only
