using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StarCitizenOverlayStarter.Models;
using StarCitizenOverlayStarter.Services;
using StarCitizenOverlayStarter.Utils;
using StarCitizenOverlayStarter.Views;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinForms = System.Windows.Forms;

namespace StarCitizenOverlayStarter.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly IScreenCaptureService _screenCaptureService;
    private readonly IOcrService _ocrService;
    private readonly AppSettings _settings;
    private readonly CaptureOverlayWindow _captureOverlayWindow;
    private readonly DispatcherTimer _overlayRefreshTimer;

    [ObservableProperty] private string status = "Ready.";
    [ObservableProperty] private string acceptedText = string.Empty;
    [ObservableProperty] private string lastExtractedText = string.Empty;
    [ObservableProperty] private string signatureCaptureRegionSummary = "No region selected.";
    [ObservableProperty] private string miningCaptureRegionSummary = "No region selected.";
    [ObservableProperty] private bool saveDebugImages;
    [ObservableProperty] private string selectedOcrMode = OcrModeCatalog.Auto;
    [ObservableProperty] private string lastUsedOcrMode = "Last OCR mode: none yet";
    [ObservableProperty] private string lastOcrSourceSummary = "OCR source: none yet";
    [ObservableProperty] private System.Windows.Media.Brush lastOcrSourceBrush = System.Windows.Media.Brushes.SlateGray;
    [ObservableProperty] private string signatureResourceSummary = "Signature resource: none yet";
    [ObservableProperty] private string miningRockSummary = "Rock scan: none yet";
    [ObservableProperty] private string selectedCaptureProfile = CaptureProfileCatalog.Signature;
    [ObservableProperty] private string hotkeyTargetSummary = "Alt+B target: Signature";
    [ObservableProperty] private string offlineSelectedCaptureProfile = CaptureProfileCatalog.Signature;
    [ObservableProperty] private ImageSource? offlinePreviewImage;
    [ObservableProperty] private string offlineImagePath = "Drop an image here to test Signature or Mining OCR.";
    [ObservableProperty] private string offlineImageInfo = "No image loaded.";
    [ObservableProperty] private string offlineResultText = string.Empty;
    [ObservableProperty] private string offlineSignatureResourceSummary = "Signature resource: none yet";
    [ObservableProperty] private string offlineMiningRockSummary = "Rock scan: none yet";
    [ObservableProperty] private string offlineSelectionModeSummary = "Selection mode: none";
    [ObservableProperty] private double offlinePreviewWidth = 640;
    [ObservableProperty] private double offlinePreviewHeight = 280;
    [ObservableProperty] private double offlineSelectionLeft;
    [ObservableProperty] private double offlineSelectionTop;
    [ObservableProperty] private double offlineSelectionWidth;
    [ObservableProperty] private double offlineSelectionHeight;
    [ObservableProperty] private Visibility offlineSelectionVisibility = Visibility.Collapsed;
    [ObservableProperty] private OfflineInputImageItem? selectedOfflineInputImage;
    [ObservableProperty] private OfflineMiningResultItem? selectedOfflineMiningResult;

    private int _offlineImagePixelWidth;
    private int _offlineImagePixelHeight;

    public ICommand SelectSignatureRegionCommand { get; }
    public ICommand SelectMiningRegionCommand { get; }
    public ICommand CaptureSignatureCommand { get; }
    public ICommand CaptureMiningCommand { get; }
    public ICommand CaptureSignatureOnlyCommand { get; }
    public ICommand CaptureMiningOnlyCommand { get; }
    public ICommand CaptureNowCommand { get; }
    public ICommand OpenDebugFolderCommand { get; }
    public ICommand ReplayDebugFolderCommand { get; }
    public ICommand RunOfflineTestCommand { get; }
    public IAsyncRelayCommand SaveOfflineMiningEditsCommand { get; }
    public IRelayCommand AddOfflineMiningRowCommand { get; }
    public IRelayCommand<OfflineMiningElementEditRow> DeleteOfflineMiningRowCommand { get; }
    public IReadOnlyList<OcrModeOption> OcrModeOptions { get; } = OcrModeCatalog.Options;
    public IReadOnlyList<CaptureProfileOption> CaptureProfileOptions { get; } = CaptureProfileCatalog.Options;
    public ObservableCollection<OfflineInputImageItem> OfflineInputImages { get; } = new();
    public ObservableCollection<OfflineMiningResultItem> OfflineMiningResults { get; } = new();

    public bool IsOfflineMiningSelected => string.Equals(OfflineSelectedCaptureProfile, CaptureProfileCatalog.Mining, StringComparison.OrdinalIgnoreCase);

    public MainViewModel(ISettingsService settingsService, IScreenCaptureService screenCaptureService, IOcrService ocrService)
    {
        _settingsService = settingsService;
        _screenCaptureService = screenCaptureService;
        _ocrService = ocrService;
        _settings = _settingsService.Load();
        saveDebugImages = _settings.SaveDebugImages;
        selectedOcrMode = OcrModeCatalog.IsValid(_settings.OcrMode) ? _settings.OcrMode : OcrModeCatalog.Auto;
        selectedCaptureProfile = CaptureProfileCatalog.IsValid(_settings.ActiveCaptureProfile) ? _settings.ActiveCaptureProfile : CaptureProfileCatalog.Signature;
        _captureOverlayWindow = new CaptureOverlayWindow();
        _overlayRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _overlayRefreshTimer.Tick += OverlayRefreshTimer_Tick;
        _overlayRefreshTimer.Start();
        UpdateCaptureRegionSummaries();
        UpdateHotkeyTargetSummary();
        UpdateCaptureOverlay();

        SelectSignatureRegionCommand = new RelayCommand(() => SelectRegion(CaptureProfileCatalog.Signature));
        SelectMiningRegionCommand = new RelayCommand(() => SelectRegion(CaptureProfileCatalog.Mining));
        CaptureSignatureCommand = new AsyncRelayCommand(() => CaptureAndReviewAsync(CaptureProfileCatalog.Signature));
        CaptureMiningCommand = new AsyncRelayCommand(() => CaptureAndReviewAsync(CaptureProfileCatalog.Mining));
        CaptureSignatureOnlyCommand = new AsyncRelayCommand(() => CaptureOnlyAsync(CaptureProfileCatalog.Signature));
        CaptureMiningOnlyCommand = new AsyncRelayCommand(() => CaptureOnlyAsync(CaptureProfileCatalog.Mining));
        CaptureNowCommand = new AsyncRelayCommand(CaptureAndReviewAsync);
        OpenDebugFolderCommand = new RelayCommand(OpenDebugFolder);
        ReplayDebugFolderCommand = new AsyncRelayCommand(ReplayDebugFolderAsync);
        RunOfflineTestCommand = new AsyncRelayCommand(RunOfflineTestAsync);
        SaveOfflineMiningEditsCommand = new AsyncRelayCommand(SaveOfflineMiningEditsAsync, () => SelectedOfflineMiningResult is not null);
        AddOfflineMiningRowCommand = new RelayCommand(AddOfflineMiningRow, () => SelectedOfflineMiningResult is not null);
        DeleteOfflineMiningRowCommand = new RelayCommand<OfflineMiningElementEditRow>(DeleteOfflineMiningRow);
    }

    public Task CaptureAndReviewAsync()
    {
        return CaptureAndReviewAsync(SelectedCaptureProfile);
    }

    private async Task CaptureAndReviewAsync(string captureProfile)
    {
        SelectedCaptureProfile = captureProfile;
        var region = GetCaptureRegion(captureProfile);

        if (!region.IsValid)
        {
            Status = $"Set the {GetCaptureProfileLabel(captureProfile)} region first.";
            return;
        }

        try
        {
            var captureLabel = GetCaptureProfileLabel(captureProfile);
            Status = $"Capturing {captureLabel} region...";
            _captureOverlayWindow.HideOverlay();
            using Bitmap bitmap = _screenCaptureService.Capture(region);
            var debugOutputDirectory = CreateDebugOutputDirectory(captureProfile);

            if (debugOutputDirectory is not null)
            {
                bitmap.Save(Path.Combine(debugOutputDirectory, "01_capture.png"), System.Drawing.Imaging.ImageFormat.Png);
            }

            Status = $"Running OCR for {captureLabel}...";
            var result = await _ocrService.ExtractTextAsync(bitmap, debugOutputDirectory, SelectedOcrMode, captureProfile);
            LastExtractedText = result.NormalizedText;
            LastUsedOcrMode = $"Last OCR mode: {OcrModeCatalog.GetDisplayName(result.UsedMode)}";
            LastOcrSourceSummary = GetOcrSourceSummary(result.UsedMode, captureProfile);
            SignatureResourceSummary = BuildSignatureResourceSummary(captureProfile, result.NormalizedText);
            MiningRockSummary = BuildMiningRockSummary(captureProfile, result.NormalizedText);

            var reviewWindow = new ReviewWindow(
                result.NormalizedText,
                captureLabel,
                string.Equals(captureProfile, CaptureProfileCatalog.Signature, StringComparison.OrdinalIgnoreCase)
                    ? SignatureResourceSummary
                    : null)
            {
                Owner = System.Windows.Application.Current.MainWindow
            };

            var accepted = reviewWindow.ShowDialog();
            if (accepted == true)
            {
                AcceptedText = reviewWindow.EditedText;
                SignatureResourceSummary = BuildSignatureResourceSummary(captureProfile, AcceptedText);
                var appendSummary = AppendMiningRockScanIfApplicable(captureProfile, AcceptedText, "live", null, debugOutputDirectory);
                MiningRockSummary = appendSummary.Summary;
                Status = BuildAcceptedStatus(captureLabel, AcceptedText.Length, debugOutputDirectory, appendSummary.CsvPath, appendSummary.JsonPath);
            }
            else
            {
                Status = debugOutputDirectory is null
                    ? $"{captureLabel} review canceled."
                    : $"{captureLabel} review canceled. Debug saved to {debugOutputDirectory}.";
            }
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
        }
        finally
        {
            UpdateCaptureOverlay();
        }
    }

    private Task CaptureOnlyAsync(string captureProfile)
    {
        SelectedCaptureProfile = captureProfile;
        var region = GetCaptureRegion(captureProfile);

        if (!region.IsValid)
        {
            Status = $"Set the {GetCaptureProfileLabel(captureProfile)} region first.";
            return Task.CompletedTask;
        }

        try
        {
            var captureLabel = GetCaptureProfileLabel(captureProfile);
            Status = $"Capturing {captureLabel} region to disk...";
            _captureOverlayWindow.HideOverlay();
            using Bitmap bitmap = _screenCaptureService.Capture(region);

            var outputPath = GetCaptureOnlyOutputPath(captureProfile);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            bitmap.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);

            Status = $"Saved {captureLabel} capture to {outputPath}";
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
        }
        finally
        {
            UpdateCaptureOverlay();
        }

        return Task.CompletedTask;
    }

    private void SelectRegion(string captureProfile)
    {
        SelectedCaptureProfile = captureProfile;
        var mainWindow = System.Windows.Application.Current.MainWindow;
        var previousState = mainWindow?.WindowState ?? WindowState.Normal;

        try
        {
            _captureOverlayWindow.HideOverlay();

            if (mainWindow is not null)
            {
                mainWindow.Hide();
            }

            var selector = new RegionSelectorWindow();
            var accepted = selector.ShowDialog();
            if (accepted == true && selector.SelectedRegion is { IsValid: true } region)
            {
                SetCaptureRegion(captureProfile, region);
                _settingsService.Save(_settings);
                UpdateCaptureRegionSummaries();
                UpdateCaptureOverlay();
                Status = $"{GetCaptureProfileLabel(captureProfile)} region saved.";
            }
            else
            {
                UpdateCaptureOverlay();
                Status = $"{GetCaptureProfileLabel(captureProfile)} region selection canceled.";
            }
        }
        finally
        {
            if (mainWindow is not null)
            {
                mainWindow.Show();
                mainWindow.WindowState = previousState;
                mainWindow.Activate();
            }
        }
    }

    private void UpdateCaptureRegionSummaries()
    {
        SignatureCaptureRegionSummary = _settings.SignatureCaptureRegion.IsValid
            ? _settings.SignatureCaptureRegion.ToString()
            : "No region selected.";

        MiningCaptureRegionSummary = _settings.MiningCaptureRegion.IsValid
            ? _settings.MiningCaptureRegion.ToString()
            : "No region selected.";
    }

    partial void OnSaveDebugImagesChanged(bool value)
    {
        _settings.SaveDebugImages = value;
        _settingsService.Save(_settings);
    }

    partial void OnSelectedOcrModeChanged(string value)
    {
        _settings.OcrMode = OcrModeCatalog.IsValid(value) ? value : OcrModeCatalog.Auto;
        _settingsService.Save(_settings);
        Status = $"OCR mode set to {OcrModeOptions.First(option => option.Value == _settings.OcrMode).DisplayName}.";
    }

    partial void OnSelectedCaptureProfileChanged(string value)
    {
        _settings.ActiveCaptureProfile = CaptureProfileCatalog.IsValid(value) ? value : CaptureProfileCatalog.Signature;
        _settingsService.Save(_settings);
        UpdateHotkeyTargetSummary();
        UpdateCaptureOverlay();
    }

    partial void OnLastOcrSourceSummaryChanged(string value)
    {
        LastOcrSourceBrush = GetOcrSourceBrush(value);
    }

    partial void OnOfflineSelectedCaptureProfileChanged(string value)
    {
        if (!CaptureProfileCatalog.IsValid(value))
        {
            OfflineSelectedCaptureProfile = CaptureProfileCatalog.Signature;
            return;
        }

        OnPropertyChanged(nameof(IsOfflineMiningSelected));
        UpdateOfflineSelectionPreview();
    }

    partial void OnSelectedOfflineInputImageChanged(OfflineInputImageItem? value)
    {
        LoadOfflinePreview(value?.FilePath);
        UpdateOfflineSelectionPreview();
    }

    partial void OnSelectedOfflineMiningResultChanged(OfflineMiningResultItem? value)
    {
        OfflineMiningRockSummary = value?.Summary ?? "Rock scan: none yet";
        SaveOfflineMiningEditsCommand.NotifyCanExecuteChanged();
        AddOfflineMiningRowCommand.NotifyCanExecuteChanged();
    }

    private void OpenDebugFolder()
    {
        var debugFolder = GetDebugCapturesRootDirectory();
        Directory.CreateDirectory(debugFolder);

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{debugFolder}\"",
            UseShellExecute = true
        });

        Status = $"Opened debug folder: {debugFolder}";
    }

    private async Task ReplayDebugFolderAsync()
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Choose a folder containing saved BobTools capture folders.",
            InitialDirectory = GetDebugCapturesRootDirectory(),
            UseDescriptionForTitle = true,
            AutoUpgradeEnabled = true,
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            Status = "Replay canceled.";
            return;
        }

        var rootFolder = dialog.SelectedPath;
        var captureFiles = Directory.GetFiles(rootFolder, "01_capture.png", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (captureFiles.Count == 0)
        {
            Status = "No saved 01_capture.png files found in the selected folder.";
            return;
        }

        var outputPath = Path.Combine(rootFolder, $"replay_results_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        var csv = new StringBuilder();
        csv.AppendLine("capture_file,requested_mode,used_mode,confidence,extracted_text,expected_text,exact_match,contains_expected");

        for (var captureIndex = 0; captureIndex < captureFiles.Count; captureIndex++)
        {
            var captureFile = captureFiles[captureIndex];
            Status = $"Replaying {captureIndex + 1}/{captureFiles.Count}: {Path.GetFileName(Path.GetDirectoryName(captureFile))}";

            var captureFolder = Path.GetDirectoryName(captureFile)!;
            var expectedPath = Path.Combine(captureFolder, "expected.txt");
            var expectedText = File.Exists(expectedPath)
                ? File.ReadAllText(expectedPath).Trim()
                : string.Empty;

            using var bitmap = new Bitmap(captureFile);
            foreach (var mode in OcrModeOptions)
            {
                var result = await _ocrService.ExtractTextAsync(bitmap, null, mode.Value, InferCaptureProfileFromPath(captureFile));
                var normalizedExpected = expectedText.Trim();
                var isExactMatch = !string.IsNullOrWhiteSpace(normalizedExpected)
                    && string.Equals(result.NormalizedText.Trim(), normalizedExpected, StringComparison.Ordinal);
                var containsExpected = !string.IsNullOrWhiteSpace(normalizedExpected)
                    && result.NormalizedText.Contains(normalizedExpected, StringComparison.Ordinal);

                csv.AppendLine(string.Join(",",
                    EscapeCsv(captureFile),
                    EscapeCsv(mode.DisplayName),
                    EscapeCsv(OcrModeCatalog.GetDisplayName(result.UsedMode)),
                    result.Confidence.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
                    EscapeCsv(result.NormalizedText),
                    EscapeCsv(normalizedExpected),
                    isExactMatch ? "true" : "false",
                    containsExpected ? "true" : "false"));
            }
        }

        File.WriteAllText(outputPath, csv.ToString());
        Status = $"Replay complete. CSV saved to {outputPath}";
    }

    public void LoadOfflineTestImages(IEnumerable<string> imagePaths)
    {
        var validImagePaths = imagePaths
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Where(IsSupportedOfflineImage)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (validImagePaths.Count == 0)
        {
            Status = "No supported offline images were found in the dropped selection.";
            return;
        }

        foreach (var imagePath in validImagePaths)
        {
            if (OfflineInputImages.Any(item => string.Equals(item.FilePath, imagePath, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            OfflineInputImages.Add(new OfflineInputImageItem(imagePath));
        }

        OfflineResultText = string.Empty;
        OfflineSignatureResourceSummary = "Signature resource: none yet";
        OfflineMiningRockSummary = "Rock scan: none yet";
        if (SelectedOfflineInputImage is null)
        {
            SelectedOfflineInputImage = OfflineInputImages.FirstOrDefault();
        }
        else if (validImagePaths.Any(path => string.Equals(path, SelectedOfflineInputImage.FilePath, StringComparison.OrdinalIgnoreCase)))
        {
            LoadOfflinePreview(SelectedOfflineInputImage.FilePath);
        }

        Status = validImagePaths.Count == 1
            ? $"Loaded offline test image: {Path.GetFileName(validImagePaths[0])}"
            : $"Loaded {validImagePaths.Count} offline test images.";
    }

    public void LoadOfflineTestImage(string imagePath)
    {
        LoadOfflineTestImages(new[] { imagePath });
    }

    private void LoadOfflinePreview(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            _offlineImagePixelWidth = 0;
            _offlineImagePixelHeight = 0;
            OfflinePreviewImage = null;
            OfflineImagePath = "Drop an image here to test Signature or Mining OCR.";
            OfflineImageInfo = "No image loaded.";
            return;
        }

        var bitmapImage = new BitmapImage();
        bitmapImage.BeginInit();
        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
        bitmapImage.UriSource = new Uri(imagePath, UriKind.Absolute);
        bitmapImage.EndInit();
        bitmapImage.Freeze();

        _offlineImagePixelWidth = bitmapImage.PixelWidth;
        _offlineImagePixelHeight = bitmapImage.PixelHeight;
        OfflinePreviewImage = bitmapImage;
        OfflineImagePath = imagePath;
        OfflineImageInfo = $"Loaded image: {bitmapImage.PixelWidth} x {bitmapImage.PixelHeight} | Queue: {OfflineInputImages.Count}";
    }

    private static bool IsSupportedOfflineImage(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".bmp", StringComparison.OrdinalIgnoreCase);
    }

    private async Task RunOfflineTestAsync()
    {
        if (OfflineInputImages.Count == 0)
        {
            Status = "Drop an image into the Offline Test tab first.";
            return;
        }

        if (string.Equals(OfflineSelectedCaptureProfile, CaptureProfileCatalog.Mining, StringComparison.OrdinalIgnoreCase))
        {
            await RunOfflineMiningBatchAsync();
            return;
        }

        if (SelectedOfflineInputImage is null || !File.Exists(SelectedOfflineInputImage.FilePath))
        {
            Status = "Select an offline image to process first.";
            return;
        }

        var region = GetCaptureRegion(OfflineSelectedCaptureProfile);
        if (!region.IsValid)
        {
            Status = $"Set the {GetCaptureProfileLabel(OfflineSelectedCaptureProfile)} region first.";
            return;
        }

        try
        {
            using var sourceBitmap = new Bitmap(SelectedOfflineInputImage.FilePath);
            if (!TryGetOfflineCropBounds(region, sourceBitmap.Width, sourceBitmap.Height, out var cropBounds, out var useWholeImage, out var errorMessage))
            {
                Status = errorMessage;
                return;
            }

            var debugOutputDirectory = CreateOfflineDebugOutputDirectory(OfflineSelectedCaptureProfile);
            Status = useWholeImage
                ? $"Running offline OCR on full dropped image for {GetCaptureProfileLabel(OfflineSelectedCaptureProfile)}..."
                : $"Running offline OCR for {GetCaptureProfileLabel(OfflineSelectedCaptureProfile)}...";
            using var croppedBitmap = sourceBitmap.Clone(cropBounds, sourceBitmap.PixelFormat);

            if (debugOutputDirectory is not null)
            {
                SaveOfflineDebugArtifacts(debugOutputDirectory, SelectedOfflineInputImage.FilePath, sourceBitmap, croppedBitmap, cropBounds, useWholeImage);
            }

            var result = await _ocrService.ExtractTextAsync(croppedBitmap, debugOutputDirectory, SelectedOcrMode, OfflineSelectedCaptureProfile);
            OfflineResultText = result.NormalizedText;
            LastUsedOcrMode = $"Last OCR mode: {OcrModeCatalog.GetDisplayName(result.UsedMode)}";
            LastOcrSourceSummary = GetOcrSourceSummary(result.UsedMode, OfflineSelectedCaptureProfile);
            OfflineSignatureResourceSummary = BuildSignatureResourceSummary(OfflineSelectedCaptureProfile, result.NormalizedText);
            var appendSummary = AppendMiningRockScanIfApplicable(OfflineSelectedCaptureProfile, result.NormalizedText, "offline", SelectedOfflineInputImage.FilePath, debugOutputDirectory);
            OfflineMiningRockSummary = appendSummary.Summary;
            Status = BuildOfflineCompletionStatus(OfflineSelectedCaptureProfile, useWholeImage, debugOutputDirectory, appendSummary.CsvPath, appendSummary.JsonPath);
        }
        catch (Exception ex)
        {
            Status = $"Offline OCR error: {ex.Message}";
        }
    }

    private async Task RunOfflineMiningBatchAsync()
    {
        var region = GetCaptureRegion(OfflineSelectedCaptureProfile);
        if (!region.IsValid)
        {
            Status = $"Set the {GetCaptureProfileLabel(OfflineSelectedCaptureProfile)} region first.";
            return;
        }

        OfflineMiningResults.Clear();
        SelectedOfflineMiningResult = null;

        var processedCount = 0;
        var savedCount = 0;

        foreach (var inputImage in OfflineInputImages)
        {
            Status = $"Running offline OCR {processedCount + 1}/{OfflineInputImages.Count}: {inputImage.DisplayName}";

            using var sourceBitmap = new Bitmap(inputImage.FilePath);
            if (!TryGetOfflineCropBounds(region, sourceBitmap.Width, sourceBitmap.Height, out var cropBounds, out var useWholeImage, out var errorMessage))
            {
                Status = $"Skipped {inputImage.DisplayName}: {errorMessage}";
                continue;
            }

            var debugOutputDirectory = CreateOfflineDebugOutputDirectory(OfflineSelectedCaptureProfile);
            using var croppedBitmap = sourceBitmap.Clone(cropBounds, sourceBitmap.PixelFormat);

            if (debugOutputDirectory is not null)
            {
                SaveOfflineDebugArtifacts(debugOutputDirectory, inputImage.FilePath, sourceBitmap, croppedBitmap, cropBounds, useWholeImage);
            }

            var ocrResult = await _ocrService.ExtractTextAsync(croppedBitmap, debugOutputDirectory, SelectedOcrMode, OfflineSelectedCaptureProfile);
            LastUsedOcrMode = $"Last OCR mode: {OcrModeCatalog.GetDisplayName(ocrResult.UsedMode)}";
            LastOcrSourceSummary = GetOcrSourceSummary(ocrResult.UsedMode, OfflineSelectedCaptureProfile);

            var scan = BuildOfflineMiningScan(ocrResult.NormalizedText, inputImage.FilePath, debugOutputDirectory);
            string? csvPath = null;
            string? jsonPath = null;

            if (HasMeaningfulMiningData(scan))
            {
                jsonPath = RockScanJsonStore.Save(scan);
                csvPath = RockScanCsvStore.Append(scan);
                savedCount++;
            }
            else if (!string.IsNullOrWhiteSpace(debugOutputDirectory))
            {
                jsonPath = RockScanJsonStore.Save(scan);
            }

            var resultItem = new OfflineMiningResultItem(
                inputImage.FilePath,
                ResolveProcessedImagePath(debugOutputDirectory, ocrResult.UsedMode) ?? Path.Combine(debugOutputDirectory ?? string.Empty, "01_capture.png"),
                scan,
                RockScanParser.BuildSummary(scan),
                jsonPath,
                csvPath);

            OfflineMiningResults.Add(resultItem);
            processedCount++;
        }

        SelectedOfflineMiningResult = OfflineMiningResults.FirstOrDefault();
        OfflineSignatureResourceSummary = "Signature resource: n/a for Mining";

        Status = processedCount == 0
            ? "No offline mining images were processed."
            : $"Processed {processedCount} mining image(s). Persisted {savedCount} parsed result(s).";
    }

    private async Task SaveOfflineMiningEditsAsync()
    {
        if (SelectedOfflineMiningResult is null)
        {
            Status = "Select a mining result to save first.";
            return;
        }

        await Task.Run(() =>
        {
            var updatedScan = SelectedOfflineMiningResult.BuildUpdatedScan();
            var jsonPath = RockScanJsonStore.Save(updatedScan);
            var csvPath = RockScanCsvStore.Upsert(updatedScan);

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                SelectedOfflineMiningResult.MarkSaved(updatedScan, jsonPath, csvPath);
                OfflineMiningRockSummary = SelectedOfflineMiningResult.Summary;
                Status = $"Saved updated mining data to {jsonPath} and synchronized {csvPath}.";
            });
        });
    }

    private void AddOfflineMiningRow()
    {
        SelectedOfflineMiningResult?.EditableElements.Add(new OfflineMiningElementEditRow(string.Empty, string.Empty, string.Empty));
    }

    private void DeleteOfflineMiningRow(OfflineMiningElementEditRow? row)
    {
        if (row is null || SelectedOfflineMiningResult is null)
        {
            return;
        }

        SelectedOfflineMiningResult.EditableElements.Remove(row);
    }

    private string? CreateDebugOutputDirectory(string captureProfile)
    {
        if (!SaveDebugImages)
        {
            return null;
        }

        var appDataRoot = GetDebugCapturesRootDirectory();

        var captureFolder = Path.Combine(appDataRoot, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{captureProfile}");
        Directory.CreateDirectory(captureFolder);
        return captureFolder;
    }

    private string? CreateOfflineDebugOutputDirectory(string captureProfile)
    {
        if (!SaveDebugImages)
        {
            return null;
        }

        var appDataRoot = GetDebugCapturesRootDirectory();
        var captureFolder = Path.Combine(appDataRoot, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{captureProfile}_offline");
        Directory.CreateDirectory(captureFolder);
        return captureFolder;
    }

    private static string GetDebugCapturesRootDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BobTools",
            "DebugCaptures");
    }

    private static string GetCaptureOnlyOutputPath(string captureProfile)
    {
        var prefix = string.Equals(captureProfile, CaptureProfileCatalog.Mining, StringComparison.OrdinalIgnoreCase)
            ? "mining"
            : "signature";

        var captureOnlyDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BobTools",
            "CaptureOnly");

        return Path.Combine(captureOnlyDirectory, $"{prefix}_{DateTime.Now:yyyyMMddHHmmssfff}.png");
    }

    private static string EscapeCsv(string value)
    {
        var safeValue = value.Replace("\r", " ").Replace("\n", " ");
        if (safeValue.Contains(',') || safeValue.Contains('"'))
        {
            return $"\"{safeValue.Replace("\"", "\"\"")}\"";
        }

        return safeValue;
    }

    private void SaveOfflineDebugArtifacts(string debugOutputDirectory, string sourceImagePath, Bitmap sourceBitmap, Bitmap croppedBitmap, System.Drawing.Rectangle cropBounds, bool useWholeImage)
    {
        Directory.CreateDirectory(debugOutputDirectory);

        sourceBitmap.Save(Path.Combine(debugOutputDirectory, "00_source.png"), System.Drawing.Imaging.ImageFormat.Png);
        croppedBitmap.Save(Path.Combine(debugOutputDirectory, "01_capture.png"), System.Drawing.Imaging.ImageFormat.Png);

        var metadata = new StringBuilder();
        metadata.AppendLine($"Source file: {sourceImagePath}");
        metadata.AppendLine($"Capture profile: {OfflineSelectedCaptureProfile}");
        metadata.AppendLine($"Selection mode: {(useWholeImage ? "full dropped image" : "saved region crop")}");
        metadata.AppendLine($"Crop bounds: {cropBounds}");
        metadata.AppendLine($"Requested OCR mode: {SelectedOcrMode}");

        File.WriteAllText(Path.Combine(debugOutputDirectory, "offline_context.txt"), metadata.ToString());
    }

    private static string BuildOfflineCompletionStatus(string captureProfile, bool useWholeImage, string? debugOutputDirectory)
    {
        var baseStatus = useWholeImage
            ? $"Offline OCR complete using the full dropped image for {GetCaptureProfileLabel(captureProfile)}."
            : $"Offline OCR complete for {GetCaptureProfileLabel(captureProfile)}.";

        return debugOutputDirectory is null
            ? baseStatus
            : $"{baseStatus} Debug saved to {debugOutputDirectory}.";
    }

    private bool TryGetOfflineCropBounds(CaptureRegion region, int imageWidth, int imageHeight, out System.Drawing.Rectangle cropBounds, out bool useWholeImage, out string errorMessage)
    {
        cropBounds = System.Drawing.Rectangle.Empty;
        useWholeImage = false;
        errorMessage = string.Empty;

        if (imageWidth <= 0 || imageHeight <= 0)
        {
            errorMessage = "No offline image dimensions are available.";
            return false;
        }

        if (region.X < 0 || region.Y < 0 || region.X + region.Width > imageWidth || region.Y + region.Height > imageHeight)
        {
            cropBounds = new System.Drawing.Rectangle(0, 0, imageWidth, imageHeight);
            useWholeImage = true;
            return true;
        }

        cropBounds = new System.Drawing.Rectangle(region.X, region.Y, region.Width, region.Height);
        return true;
    }

    private void OverlayRefreshTimer_Tick(object? sender, EventArgs e)
    {
        UpdateCaptureOverlay();
    }

    private void UpdateCaptureOverlay()
    {
        var activeRegion = GetCaptureRegion(SelectedCaptureProfile);
        if (activeRegion.IsValid)
        {
            _captureOverlayWindow.SetRegion(activeRegion);
            return;
        }

        _captureOverlayWindow.HideOverlay();
    }

    private void UpdateHotkeyTargetSummary()
    {
        HotkeyTargetSummary = $"Alt+B target: {GetCaptureProfileLabel(SelectedCaptureProfile)}";
    }

    private void UpdateOfflineSelectionPreview()
    {
        if (OfflinePreviewImage is not BitmapSource bitmapSource)
        {
            OfflineSelectionVisibility = Visibility.Collapsed;
            OfflineSelectionModeSummary = "Selection mode: none";
            return;
        }

        const double maxWidth = 760;
        const double maxHeight = 320;

        var widthScale = maxWidth / bitmapSource.PixelWidth;
        var heightScale = maxHeight / bitmapSource.PixelHeight;
        var scale = Math.Min(1d, Math.Min(widthScale, heightScale));

        OfflinePreviewWidth = Math.Max(1d, bitmapSource.PixelWidth * scale);
        OfflinePreviewHeight = Math.Max(1d, bitmapSource.PixelHeight * scale);

        var region = GetCaptureRegion(OfflineSelectedCaptureProfile);
        if (!region.IsValid)
        {
            OfflineSelectionVisibility = Visibility.Collapsed;
            OfflineSelectionModeSummary = $"Selection mode: no saved {GetCaptureProfileLabel(OfflineSelectedCaptureProfile)} region";
            return;
        }

        var regionFitsImage = region.X >= 0
            && region.Y >= 0
            && region.X + region.Width <= bitmapSource.PixelWidth
            && region.Y + region.Height <= bitmapSource.PixelHeight;

        if (!regionFitsImage)
        {
            OfflineSelectionLeft = 0;
            OfflineSelectionTop = 0;
            OfflineSelectionWidth = OfflinePreviewWidth;
            OfflineSelectionHeight = OfflinePreviewHeight;
            OfflineSelectionVisibility = Visibility.Visible;
            OfflineSelectionModeSummary = $"Selection mode: full dropped image (saved {GetCaptureProfileLabel(OfflineSelectedCaptureProfile)} box does not fit this image)";
            return;
        }

        OfflineSelectionLeft = region.X * scale;
        OfflineSelectionTop = region.Y * scale;
        OfflineSelectionWidth = Math.Max(1d, region.Width * scale);
        OfflineSelectionHeight = Math.Max(1d, region.Height * scale);
        OfflineSelectionVisibility = Visibility.Visible;
        OfflineSelectionModeSummary = $"Selection mode: saved {GetCaptureProfileLabel(OfflineSelectedCaptureProfile)} box";
    }

    private CaptureRegion GetCaptureRegion(string captureProfile)
    {
        return string.Equals(captureProfile, CaptureProfileCatalog.Mining, StringComparison.OrdinalIgnoreCase)
            ? _settings.MiningCaptureRegion
            : _settings.SignatureCaptureRegion;
    }

    private void SetCaptureRegion(string captureProfile, CaptureRegion region)
    {
        if (string.Equals(captureProfile, CaptureProfileCatalog.Mining, StringComparison.OrdinalIgnoreCase))
        {
            _settings.MiningCaptureRegion = region;
            return;
        }

        _settings.SignatureCaptureRegion = region;
    }

    private static string GetCaptureProfileLabel(string captureProfile)
    {
        return CaptureProfileCatalog.GetDisplayName(captureProfile);
    }

    private static string BuildSignatureResourceSummary(string? captureProfile, string? normalizedText)
    {
        return string.Equals(captureProfile, CaptureProfileCatalog.Signature, StringComparison.OrdinalIgnoreCase)
            ? SignatureCatalog.BuildResourceSummary(normalizedText)
            : "Signature resource: n/a for Mining";
    }

    private static string BuildMiningRockSummary(string? captureProfile, string? normalizedText)
    {
        if (!string.Equals(captureProfile, CaptureProfileCatalog.Mining, StringComparison.OrdinalIgnoreCase))
        {
            return "Rock scan: n/a for Signature";
        }

        return RockScanParser.TryParse(normalizedText, out var rockScan)
            ? RockScanParser.BuildSummary(rockScan)
            : "Rock scan: unable to parse";
    }

    private static string BuildAcceptedStatus(string captureLabel, int acceptedLength, string? debugOutputDirectory, string? csvPath, string? jsonPath)
    {
        var baseStatus = debugOutputDirectory is null
            ? $"Accepted {captureLabel} text ({acceptedLength} chars)."
            : $"Accepted {captureLabel} text ({acceptedLength} chars). Debug saved to {debugOutputDirectory}.";

        return AppendRockExportStatus(baseStatus, csvPath, jsonPath);
    }

    private static string BuildOfflineCompletionStatus(string captureProfile, bool useWholeImage, string? debugOutputDirectory, string? csvPath, string? jsonPath)
    {
        var baseStatus = useWholeImage
            ? $"Offline OCR complete using the full dropped image for {GetCaptureProfileLabel(captureProfile)}."
            : $"Offline OCR complete for {GetCaptureProfileLabel(captureProfile)}.";

        var withDebug = debugOutputDirectory is null
            ? baseStatus
            : $"{baseStatus} Debug saved to {debugOutputDirectory}.";

        return AppendRockExportStatus(withDebug, csvPath, jsonPath);
    }

    private static bool HasMeaningfulMiningData(RockScan rockScan)
    {
        return !string.IsNullOrWhiteSpace(rockScan.Material)
            || rockScan.Mass is not null
            || rockScan.Resistance is not null
            || rockScan.Instability is not null
            || rockScan.Composition is not null
            || rockScan.Elements.Count > 0;
    }

    private static RockScan BuildOfflineMiningScan(string normalizedText, string sourceImagePath, string? debugOutputDirectory)
    {
        var parsed = RockScanParser.TryParse(normalizedText, out var parsedScan)
            ? parsedScan
            : new RockScan();

        return parsed with
        {
            SessionId = App.SessionId,
            TimestampUtc = DateTime.UtcNow,
            SourceType = "offline",
            CaptureProfile = CaptureProfileCatalog.Mining,
            SourceImagePath = sourceImagePath,
            DebugFolderPath = debugOutputDirectory,
            SourceText = normalizedText
        };
    }

    private static string? ResolveProcessedImagePath(string? debugOutputDirectory, string? usedMode)
    {
        if (string.IsNullOrWhiteSpace(debugOutputDirectory) || !Directory.Exists(debugOutputDirectory))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(usedMode))
        {
            var capturePath = Path.Combine(debugOutputDirectory, "01_capture.png");
            return File.Exists(capturePath) ? capturePath : null;
        }

        var normalizedMode = usedMode.Contains('|', StringComparison.Ordinal)
            ? usedMode.Split('|').Last()
            : usedMode;

        if (normalizedMode.StartsWith("grayscale-contrast", StringComparison.OrdinalIgnoreCase))
        {
            var contrastPath = Path.Combine(debugOutputDirectory, "03_grayscale_contrast.png");
            return File.Exists(contrastPath) ? contrastPath : null;
        }

        if (normalizedMode.StartsWith("grayscale", StringComparison.OrdinalIgnoreCase))
        {
            var grayscalePath = Path.Combine(debugOutputDirectory, "02_grayscale.png");
            return File.Exists(grayscalePath) ? grayscalePath : null;
        }

        foreach (var suffix in new[] { "-auto", "-block", "-sparse", "-line", "-word" })
        {
            if (normalizedMode.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                normalizedMode = normalizedMode[..^suffix.Length];
                break;
            }
        }

        var thresholdPath = Directory.GetFiles(debugOutputDirectory, $"*_{normalizedMode}.png")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(thresholdPath))
        {
            return thresholdPath;
        }

        var fallbackPath = Path.Combine(debugOutputDirectory, "01_capture.png");
        return File.Exists(fallbackPath) ? fallbackPath : null;
    }

    private static string AppendRockExportStatus(string baseStatus, string? csvPath, string? jsonPath)
    {
        var statusBuilder = new StringBuilder(baseStatus);

        if (!string.IsNullOrWhiteSpace(csvPath))
        {
            statusBuilder.Append($" Rock CSV appended to {csvPath}.");
        }

        if (!string.IsNullOrWhiteSpace(jsonPath))
        {
            statusBuilder.Append($" Rock JSON saved to {jsonPath}.");
        }

        return statusBuilder.ToString();
    }

    private static (string Summary, string? CsvPath, string? JsonPath) AppendMiningRockScanIfApplicable(string captureProfile, string? normalizedText, string sourceType, string? sourceImagePath, string? debugOutputDirectory)
    {
        if (!string.Equals(captureProfile, CaptureProfileCatalog.Mining, StringComparison.OrdinalIgnoreCase))
        {
            return ("Rock scan: n/a for Signature", null, null);
        }

        if (!RockScanParser.TryParse(normalizedText, out var rockScan))
        {
            return ("Rock scan: unable to parse", null, null);
        }

        var completedScan = rockScan with
        {
            SessionId = App.SessionId,
            TimestampUtc = DateTime.UtcNow,
            SourceType = sourceType,
            CaptureProfile = captureProfile,
            SourceImagePath = sourceImagePath,
            DebugFolderPath = debugOutputDirectory
        };

        return (
            RockScanParser.BuildSummary(completedScan),
            RockScanCsvStore.Append(completedScan),
            RockScanJsonStore.Save(completedScan));
    }

    private static string GetOcrSourceSummary(string? usedMode, string? captureProfile)
    {
        if (usedMode?.StartsWith("signature-icon-value|", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "OCR source: Signature icon-guided crop";
        }

        if (string.Equals(captureProfile, CaptureProfileCatalog.Signature, StringComparison.OrdinalIgnoreCase))
        {
            return "OCR source: Signature fallback OCR";
        }

        return "OCR source: Standard OCR";
    }

    private static System.Windows.Media.Brush GetOcrSourceBrush(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return System.Windows.Media.Brushes.SlateGray;
        }

        if (summary.Contains("icon-guided", StringComparison.OrdinalIgnoreCase))
        {
            return System.Windows.Media.Brushes.LightGreen;
        }

        if (summary.Contains("fallback", StringComparison.OrdinalIgnoreCase))
        {
            return System.Windows.Media.Brushes.Goldenrod;
        }

        return System.Windows.Media.Brushes.LightSteelBlue;
    }

    private static string? InferCaptureProfileFromPath(string filePath)
    {
        if (filePath.Contains("_mining", StringComparison.OrdinalIgnoreCase)
            || filePath.Contains("\\mining\\", StringComparison.OrdinalIgnoreCase)
            || filePath.Contains("/mining/", StringComparison.OrdinalIgnoreCase))
        {
            return CaptureProfileCatalog.Mining;
        }

        if (filePath.Contains("_signature", StringComparison.OrdinalIgnoreCase)
            || filePath.Contains("\\signature\\", StringComparison.OrdinalIgnoreCase)
            || filePath.Contains("/signature/", StringComparison.OrdinalIgnoreCase))
        {
            return CaptureProfileCatalog.Signature;
        }

        return null;
    }

    public void Dispose()
    {
        _overlayRefreshTimer.Stop();
        _overlayRefreshTimer.Tick -= OverlayRefreshTimer_Tick;
        _captureOverlayWindow.Close();
    }
}
