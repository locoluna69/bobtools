using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BobTools.Models;
using BobTools.Services;
using BobTools.Utils;
using BobTools.Views;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinForms = System.Windows.Forms;

namespace BobTools.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly ISessionHistoryService _sessionHistoryService;
    private readonly IScreenCaptureService _screenCaptureService;
    private readonly IOcrService _ocrService;
    private readonly IRsiProfileImageService _rsiProfileImageService;
    private readonly AppSettings _settings;
    private readonly CaptureOverlayWindow _captureOverlayWindow;
    private readonly DispatcherTimer _overlayRefreshTimer;
    private readonly Channel<QueuedLiveScanCapture> _liveScanQueue = Channel.CreateUnbounded<QueuedLiveScanCapture>();
    private readonly Channel<QueuedLiveMiningCapture> _liveMiningQueue = Channel.CreateUnbounded<QueuedLiveMiningCapture>();
    private readonly CancellationTokenSource _liveMiningQueueCancellation = new();
    private readonly Task _liveScanQueueTask;
    private readonly Task _liveMiningQueueTask;
    private readonly List<string> _offlineScanInputImagePaths = [];
    private readonly List<string> _offlineMiningInputImagePaths = [];
    private bool _isUpdatingScanTargetSelection;
    private bool _isSyncingOfflineScanSelection;
    private CancellationTokenSource? _sessionProfileImageCancellation;
    private int _sessionProfileImageRequestVersion;

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
    [ObservableProperty] private string signatureResourceSummary = "Scan resource: none yet";
    [ObservableProperty] private string miningRockSummary = "Rock scan: none yet";
    [ObservableProperty] private string currentSessionId = App.SessionId;
    [ObservableProperty] private string liveScanQueueSummary = "No live scan captures yet.";
    [ObservableProperty] private string liveMiningQueueSummary = "No live mining captures yet.";
    [ObservableProperty] private int lifetimeSignatureCount;
    [ObservableProperty] private int lifetimeRockCount;
    [ObservableProperty] private int lifetimeHighValueOreCount;
    [ObservableProperty] private string selectedWorkflowMode = WorkflowModeCatalog.Scanning;
    [ObservableProperty] private string selectedCaptureProfile = CaptureProfileCatalog.Signature;
    [ObservableProperty] private bool captureOnlyMode;
    [ObservableProperty] private string sessionRsiHandle = string.Empty;
    [ObservableProperty] private ImageSource? sessionProfileImage;
    [ObservableProperty] private string sessionProfileImageStatus = "Enter an RSI handle to load the public profile image.";
    [ObservableProperty] private string selectedSessionShip = string.Empty;
    [ObservableProperty] private string selectedSessionSystem = string.Empty;
    [ObservableProperty] private string sessionLocationSearchText = string.Empty;
    [ObservableProperty] private MiningLocationOption? selectedSessionLocation;
    [ObservableProperty] private string sessionContextSummary = "Session context: add RSI handle, then select ship, system, and location.";
    [ObservableProperty] private string hotkeyTargetSummary = "Alt+B target: Scan capture";
    [ObservableProperty] private string offlineSelectedCaptureProfile = CaptureProfileCatalog.Signature;
    [ObservableProperty] private ImageSource? offlinePreviewImage;
    [ObservableProperty] private string offlineImagePath = "Drop an image here to test Scan or Mining OCR.";
    [ObservableProperty] private string offlineImageInfo = "No image loaded.";
    [ObservableProperty] private string offlineResultText = string.Empty;
    [ObservableProperty] private string offlineSignatureResourceSummary = "Scan resource: none yet";
    [ObservableProperty] private string offlineMiningRockSummary = "Rock scan: none yet";
    [ObservableProperty] private string offlineSelectionModeSummary = "Selection mode: none";
    [ObservableProperty] private int selectedMainTabIndex;
    [ObservableProperty] private bool isScanTargetSelectorOpen;
    [ObservableProperty] private string selectedScanTargetsSummary = "Highlight targets: none";
    [ObservableProperty] private double offlinePreviewWidth = 640;
    [ObservableProperty] private double offlinePreviewHeight = 280;
    [ObservableProperty] private double offlineSelectionLeft;
    [ObservableProperty] private double offlineSelectionTop;
    [ObservableProperty] private double offlineSelectionWidth;
    [ObservableProperty] private double offlineSelectionHeight;
    [ObservableProperty] private Visibility offlineSelectionVisibility = Visibility.Collapsed;
    [ObservableProperty] private OfflineInputImageItem? selectedOfflineInputImage;
    [ObservableProperty] private OfflineScanResultItem? selectedOfflineScanResult;
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
    public ICommand LoadLiveMiningFolderCommand { get; }
    public ICommand ExportScanTrainingPackageCommand { get; }
    public ICommand ReplayDebugFolderCommand { get; }
    public ICommand RunOfflineTestCommand { get; }
    public IAsyncRelayCommand SaveOfflineMiningEditsCommand { get; }
    public IRelayCommand AddOfflineMiningRowCommand { get; }
    public IRelayCommand<OfflineMiningElementEditRow> DeleteOfflineMiningRowCommand { get; }
    public IRelayCommand SelectAllScanTargetsCommand { get; }
    public IRelayCommand ClearScanTargetsCommand { get; }
    public IReadOnlyList<OcrModeOption> OcrModeOptions { get; } = OcrModeCatalog.Options;
    public IReadOnlyList<CaptureProfileOption> WorkflowModeOptions { get; } = WorkflowModeCatalog.Options;
    public IReadOnlyList<CaptureProfileOption> CaptureProfileOptions { get; } = CaptureProfileCatalog.Options;
    public ObservableCollection<OfflineInputImageItem> OfflineInputImages { get; } = new();
    public ObservableCollection<OfflineScanResultItem> OfflineScanResults { get; } = new();
    public ObservableCollection<OfflineMiningResultItem> OfflineMiningResults { get; } = new();
    public ObservableCollection<ScanTargetOption> ScanTargetOptions { get; } = new();
    public ObservableCollection<string> SessionShipOptions { get; } = new();
    public ObservableCollection<string> SessionSystemOptions { get; } = new();
    public ObservableCollection<MiningLocationOption> SessionLocationOptions { get; } = new();
    public ObservableCollection<LiveScanResultItem> LiveScanResults { get; } = new();
    public ObservableCollection<OfflineMiningResultItem> LiveMiningResults { get; } = new();

    public bool IsOfflineMiningSelected => string.Equals(OfflineSelectedCaptureProfile, CaptureProfileCatalog.Mining, StringComparison.OrdinalIgnoreCase);
    public bool IsScanningMode => string.Equals(SelectedWorkflowMode, WorkflowModeCatalog.Scanning, StringComparison.OrdinalIgnoreCase);
    public bool IsMiningMode => string.Equals(SelectedWorkflowMode, WorkflowModeCatalog.Mining, StringComparison.OrdinalIgnoreCase);
    public bool IsOfflineMode => string.Equals(SelectedWorkflowMode, WorkflowModeCatalog.Offline, StringComparison.OrdinalIgnoreCase);
    public bool IsSessionContextComplete => !string.IsNullOrWhiteSpace(SessionRsiHandle)
        && !string.IsNullOrWhiteSpace(SelectedSessionShip)
        && !string.IsNullOrWhiteSpace(SelectedSessionSystem)
        && SelectedSessionLocation is not null;

    public MainViewModel(ISettingsService settingsService, ISessionHistoryService sessionHistoryService, IScreenCaptureService screenCaptureService, IOcrService ocrService, IRsiProfileImageService rsiProfileImageService)
    {
        _settingsService = settingsService;
        _sessionHistoryService = sessionHistoryService;
        _screenCaptureService = screenCaptureService;
        _ocrService = ocrService;
        _rsiProfileImageService = rsiProfileImageService;
        _settings = _settingsService.Load();
        saveDebugImages = _settings.SaveDebugImages;
        captureOnlyMode = _settings.CaptureOnlyMode;
        lifetimeSignatureCount = _settings.LifetimeSignatureCount;
        lifetimeRockCount = _settings.LifetimeRockCount;
        lifetimeHighValueOreCount = _settings.LifetimeHighValueOreCount;
        selectedOcrMode = OcrModeCatalog.Auto;
        selectedWorkflowMode = WorkflowModeCatalog.Scanning;
        selectedCaptureProfile = CaptureProfileCatalog.Signature;
        var settingsChanged = false;

        if (!string.Equals(_settings.OcrMode, OcrModeCatalog.Auto, StringComparison.OrdinalIgnoreCase))
        {
            _settings.OcrMode = OcrModeCatalog.Auto;
            settingsChanged = true;
        }

        if (!string.Equals(_settings.ActiveWorkflowMode, WorkflowModeCatalog.Scanning, StringComparison.OrdinalIgnoreCase))
        {
            _settings.ActiveWorkflowMode = WorkflowModeCatalog.Scanning;
            settingsChanged = true;
        }

        if (!string.Equals(_settings.ActiveCaptureProfile, CaptureProfileCatalog.Signature, StringComparison.OrdinalIgnoreCase))
        {
            _settings.ActiveCaptureProfile = CaptureProfileCatalog.Signature;
            settingsChanged = true;
        }

        if (_settings.SignatureCaptureRegion.IsValid)
        {
            var normalizedSignatureRegion = NormalizeCaptureRegion(CaptureProfileCatalog.Signature, _settings.SignatureCaptureRegion);
            if (!AreRegionsEqual(_settings.SignatureCaptureRegion, normalizedSignatureRegion))
            {
                _settings.SignatureCaptureRegion = normalizedSignatureRegion;
                settingsChanged = true;
            }
        }

        if (settingsChanged)
        {
            _settingsService.Save(_settings);
        }

        SessionRsiHandle = _settings.RsiHandle;
        _captureOverlayWindow = new CaptureOverlayWindow();
        _overlayRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _overlayRefreshTimer.Tick += OverlayRefreshTimer_Tick;
        _overlayRefreshTimer.Start();
        _liveScanQueueTask = Task.Run(() => ProcessLiveScanQueueAsync(_liveMiningQueueCancellation.Token));
        _liveMiningQueueTask = Task.Run(() => ProcessLiveMiningQueueAsync(_liveMiningQueueCancellation.Token));
        LoadScanTargetOptions();
        LoadMiningSessionContextOptions();
        LoadPersistedLiveResults();
        UpdateCaptureRegionSummaries();
        UpdateLiveScanQueueSummary();
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
        LoadLiveMiningFolderCommand = new AsyncRelayCommand(LoadLiveMiningFolderAsync);
        ExportScanTrainingPackageCommand = new RelayCommand(ExportScanTrainingPackage);
        ReplayDebugFolderCommand = new AsyncRelayCommand(ReplayDebugFolderAsync);
        RunOfflineTestCommand = new AsyncRelayCommand(RunOfflineTestAsync);
        SaveOfflineMiningEditsCommand = new AsyncRelayCommand(SaveOfflineMiningEditsAsync, () => SelectedOfflineMiningResult is not null);
        AddOfflineMiningRowCommand = new RelayCommand(AddOfflineMiningRow, () => SelectedOfflineMiningResult is not null);
        DeleteOfflineMiningRowCommand = new RelayCommand<OfflineMiningElementEditRow>(DeleteOfflineMiningRow);
        SelectAllScanTargetsCommand = new RelayCommand(() => SetAllScanTargets(true));
        ClearScanTargetsCommand = new RelayCommand(() => SetAllScanTargets(false));
    }

    public Task CaptureAndReviewAsync()
    {
        var captureProfile = WorkflowModeCatalog.GetCaptureProfile(SelectedWorkflowMode);
        if (string.IsNullOrWhiteSpace(captureProfile))
        {
            Status = "Offline mode does not use Alt+B capture. Use Queue Folder to add mining images for processing.";
            return Task.CompletedTask;
        }

        return CaptureAndReviewAsync(captureProfile);
    }

    private async Task CaptureAndReviewAsync(string captureProfile)
    {
        SelectedCaptureProfile = captureProfile;

        if (!EnsureSessionContextReady())
        {
            return;
        }

        var region = GetCaptureRegion(captureProfile);

        if (!region.IsValid)
        {
            Status = $"Set the {GetCaptureProfileLabel(captureProfile)} region first.";
            return;
        }

        if (CaptureOnlyMode)
        {
            await CaptureOnlyAsync(captureProfile);
            return;
        }

        if (string.Equals(captureProfile, CaptureProfileCatalog.Signature, StringComparison.OrdinalIgnoreCase)
            && IsScanningMode)
        {
            await QueueLiveScanCaptureAsync(region, captureProfile);
            return;
        }

        if (string.Equals(captureProfile, CaptureProfileCatalog.Mining, StringComparison.OrdinalIgnoreCase))
        {
            await QueueLiveMiningCaptureAsync(region, captureProfile);
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
                PngMetadataWriter.SaveBitmapWithSessionMetadata(
                    bitmap,
                    Path.Combine(debugOutputDirectory, "01_capture.png"),
                    BuildCurrentSessionMetadata(),
                    App.SessionId,
                    captureProfile,
                    "debug-capture");
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
                if (string.Equals(captureProfile, CaptureProfileCatalog.Signature, StringComparison.OrdinalIgnoreCase))
                {
                    IncrementSignatureCount();
                }
                else if (appendSummary.CompletedScan is not null && HasMeaningfulMiningData(appendSummary.CompletedScan))
                {
                    IncrementMiningStats(appendSummary.CompletedScan);
                }
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

        if (!EnsureSessionContextReady())
        {
            return Task.CompletedTask;
        }

        var region = GetCaptureRegion(captureProfile);

        if (!region.IsValid)
        {
            Status = $"Set the {GetCaptureProfileLabel(captureProfile)} region first.";
            return Task.CompletedTask;
        }

        try
        {
            var captureLabel = GetCaptureProfileLabel(captureProfile);
            Status = $"Capturing {captureLabel} region to raw dataset folder...";
            _captureOverlayWindow.HideOverlay();
            using Bitmap bitmap = _screenCaptureService.Capture(region);

            var outputPath = GetCaptureOnlyOutputPath(captureProfile);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            PngMetadataWriter.SaveBitmapWithSessionMetadata(
                bitmap,
                outputPath,
                BuildCurrentSessionMetadata(),
                App.SessionId,
                captureProfile,
                "capture-only");

            Status = $"Saved raw {captureLabel} capture to {outputPath}";
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

    private async Task QueueLiveMiningCaptureAsync(CaptureRegion region, string captureProfile)
    {
        try
        {
            var captureLabel = GetCaptureProfileLabel(captureProfile);
            Status = $"Capturing {captureLabel} region...";
            _captureOverlayWindow.HideOverlay();

            using Bitmap bitmap = _screenCaptureService.Capture(region);
            var captureFolder = CreateLiveCaptureDirectory(captureProfile);
            var rawImagePath = Path.Combine(captureFolder, "01_capture.png");
            PngMetadataWriter.SaveBitmapWithSessionMetadata(
                bitmap,
                rawImagePath,
                BuildCurrentSessionMetadata(),
                App.SessionId,
                captureProfile,
                "live-queue-capture");

            await EnqueueLiveMiningImageAsync(rawImagePath, SaveDebugImages ? captureFolder : null, captureProfile, "Queued live capture for OCR...");

            Status = $"Queued {captureLabel} capture for background OCR. Session: {CurrentSessionId}";
        }
        catch (OperationCanceledException)
        {
            Status = "Live mining queue is shutting down.";
        }
        catch (Exception ex)
        {
            Status = $"Error queuing mining capture: {ex.Message}";
        }
        finally
        {
            UpdateCaptureOverlay();
        }
    }

    private async Task QueueLiveScanCaptureAsync(CaptureRegion region, string captureProfile)
    {
        try
        {
            Status = "Capturing Scan region...";
            _captureOverlayWindow.HideOverlay();

            using Bitmap bitmap = _screenCaptureService.Capture(region);
            var captureFolder = CreateLiveCaptureDirectory(captureProfile);
            var rawImagePath = Path.Combine(captureFolder, "01_capture.png");
            PngMetadataWriter.SaveBitmapWithSessionMetadata(
                bitmap,
                rawImagePath,
                BuildCurrentSessionMetadata(),
                App.SessionId,
                captureProfile,
                "live-queue-capture");

            await EnqueueLiveScanImageAsync(rawImagePath, SaveDebugImages ? captureFolder : null, captureProfile, "Queued live scan for OCR...");

            Status = $"Queued Scan capture for background OCR. Session: {CurrentSessionId}";
        }
        catch (OperationCanceledException)
        {
            Status = "Scan queue is shutting down.";
        }
        catch (Exception ex)
        {
            Status = $"Error queuing scan capture: {ex.Message}";
        }
        finally
        {
            UpdateCaptureOverlay();
        }
    }

    private async Task LoadLiveMiningFolderAsync()
    {
        if (!EnsureSessionContextReady())
        {
            return;
        }

        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Choose a folder of mining screenshots to enqueue in the live queue.",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            UseDescriptionForTitle = true,
            AutoUpgradeEnabled = true,
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            Status = "Live queue folder import canceled.";
            return;
        }

        var imageFiles = Directory.GetFiles(dialog.SelectedPath, "*", SearchOption.TopDirectoryOnly)
            .Where(IsSupportedOfflineImage)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (imageFiles.Count == 0)
        {
            Status = "No supported images found in the selected folder.";
            return;
        }

        var queuedCount = 0;
        foreach (var imageFile in imageFiles)
        {
            var captureFolder = CreateLiveCaptureDirectory(CaptureProfileCatalog.Mining);
            var destinationPath = Path.Combine(captureFolder, "01_capture.png");
            PngMetadataWriter.SaveFileWithSessionMetadata(
                imageFile,
                destinationPath,
                BuildCurrentSessionMetadata(),
                App.SessionId,
                CaptureProfileCatalog.Mining,
                "imported-live-queue-capture",
                new Dictionary<string, string?>
                {
                    ["BobTools.SourceImagePath"] = imageFile
                });

            await EnqueueLiveMiningImageAsync(
                destinationPath,
                SaveDebugImages ? captureFolder : null,
                CaptureProfileCatalog.Mining,
                $"Queued imported image: {Path.GetFileName(imageFile)}");

            queuedCount++;
        }

        Status = $"Queued {queuedCount} image(s) from {dialog.SelectedPath} into the live mining queue.";
    }

    private async Task EnqueueLiveMiningImageAsync(string sourceImagePath, string? debugOutputDirectory, string captureProfile, string queuedSummary)
    {
        var placeholderScan = new RockScan
        {
            SessionId = App.SessionId,
            TimestampUtc = DateTime.UtcNow,
            SourceType = "live",
            CaptureProfile = captureProfile,
            SourceImagePath = sourceImagePath,
            DebugFolderPath = debugOutputDirectory
        };

        var resultItem = new OfflineMiningResultItem(
            sourceImagePath,
            sourceImagePath,
            placeholderScan,
            queuedSummary,
            null,
            null,
            message => Status = message,
            PersistMiningSessionHistoryFromItem,
            "Queued",
            true);

        LiveMiningResults.Insert(0, resultItem);
        PersistMiningSessionHistory(placeholderScan, WorkflowModeCatalog.Mining, string.Empty, string.Empty, "Queued", queuedSummary, sourceImagePath);
        UpdateLiveMiningQueueSummary();

        await _liveMiningQueue.Writer.WriteAsync(
            new QueuedLiveMiningCapture(sourceImagePath, debugOutputDirectory, SelectedOcrMode, placeholderScan, resultItem),
            _liveMiningQueueCancellation.Token);
    }

    private async Task EnqueueLiveScanImageAsync(string sourceImagePath, string? debugOutputDirectory, string captureProfile, string processingState)
    {
        var resultItem = new LiveScanResultItem(
            Guid.NewGuid().ToString(),
            App.SessionId,
            DateTime.UtcNow,
            sourceImagePath,
            debugOutputDirectory,
            sourceImagePath,
            processingState,
            true,
            PersistLiveScanResultItem,
            message => Status = message);

        resultItem.SelectedTargets = GetSelectedScanTargets();

        LiveScanResults.Insert(0, resultItem);
        PersistLiveScanResultItem(resultItem);
        UpdateLiveScanQueueSummary();

        await _liveScanQueue.Writer.WriteAsync(
            new QueuedLiveScanCapture(sourceImagePath, debugOutputDirectory, SelectedOcrMode, captureProfile, resultItem),
            _liveMiningQueueCancellation.Token);
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

            var selector = new RegionSelectorWindow(captureProfile, GetCaptureRegion(captureProfile));
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
            ? $"{_settings.SignatureCaptureRegion} (fixed {CaptureProfileCatalog.SignatureFixedWidth} x {CaptureProfileCatalog.SignatureFixedHeight})"
            : "No region selected.";

        MiningCaptureRegionSummary = _settings.MiningCaptureRegion.IsValid
            ? _settings.MiningCaptureRegion.ToString()
            : "No region selected.";
    }

    private void LoadScanTargetOptions()
    {
        var selectedResources = new HashSet<string>(_settings.SelectedScanResources ?? [], StringComparer.OrdinalIgnoreCase);

        ScanTargetOptions.Clear();
        foreach (var resource in SignatureCatalog.GetAvailableResources())
        {
            var option = new ScanTargetOption(resource, selectedResources.Contains(resource));
            option.PropertyChanged += ScanTargetOption_PropertyChanged;
            ScanTargetOptions.Add(option);
        }

        PersistSelectedScanTargets();
        UpdateSelectedScanTargetsSummary();
        UpdateLiveScanTargetHighlights();
    }

    private void ScanTargetOption_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isUpdatingScanTargetSelection || !string.Equals(e.PropertyName, nameof(ScanTargetOption.IsSelected), StringComparison.Ordinal))
        {
            return;
        }

        PersistSelectedScanTargets();
        UpdateSelectedScanTargetsSummary();
        UpdateLiveScanTargetHighlights();
    }

    private void SetAllScanTargets(bool isSelected)
    {
        _isUpdatingScanTargetSelection = true;
        try
        {
            foreach (var option in ScanTargetOptions)
            {
                option.IsSelected = isSelected;
            }
        }
        finally
        {
            _isUpdatingScanTargetSelection = false;
        }

        PersistSelectedScanTargets();
        UpdateSelectedScanTargetsSummary();
        UpdateLiveScanTargetHighlights();
    }

    private void PersistSelectedScanTargets()
    {
        _settings.SelectedScanResources = ScanTargetOptions
            .Where(option => option.IsSelected)
            .Select(option => option.Resource)
            .ToList();

        _settingsService.Save(_settings);
    }

    private void UpdateSelectedScanTargetsSummary()
    {
        var selectedResources = ScanTargetOptions
            .Where(option => option.IsSelected)
            .Select(option => option.Resource)
            .ToList();

        SelectedScanTargetsSummary = selectedResources.Count switch
        {
            0 => "Highlight targets: none",
            1 => $"Highlight targets: {selectedResources[0]}",
            2 => $"Highlight targets: {selectedResources[0]}, {selectedResources[1]}",
            _ => $"Highlight targets: {selectedResources[0]}, {selectedResources[1]} +{selectedResources.Count - 2}"
        };
    }

    private void UpdateLiveScanTargetHighlights()
    {
        foreach (var resultItem in LiveScanResults)
        {
            resultItem.ApplyTargetMatchState(IsSelectedScanTarget(resultItem.MatchedResourceName));
        }
    }

    private void LoadPersistedLiveResults()
    {
        LiveScanResults.Clear();
        LiveMiningResults.Clear();

        var historyEntries = _sessionHistoryService.LoadAllDocuments()
            .SelectMany(document => document.Events.Select(entry => (Document: document, Entry: entry)))
            .OrderByDescending(item => item.Entry.TimestampUtc)
            .ToList();

        foreach (var (document, entry) in historyEntries)
        {
            if (string.Equals(entry.ResultKind, "scan", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entry.SourceType, "offline", StringComparison.OrdinalIgnoreCase))
            {
                LiveScanResults.Add(CreateLiveScanResultFromHistory(document, entry));
                continue;
            }

            if (string.Equals(entry.ResultKind, "mining", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entry.WorkflowMode, WorkflowModeCatalog.Offline, StringComparison.OrdinalIgnoreCase))
            {
                LiveMiningResults.Add(CreateLiveMiningResultFromHistory(document, entry));
            }
        }

        UpdateLiveScanTargetHighlights();
        UpdateLiveScanQueueSummary();
        UpdateLiveMiningQueueSummary();
    }

    private LiveScanResultItem CreateLiveScanResultFromHistory(SessionHistoryDocument document, SessionHistoryEntry entry)
    {
        var sourceImagePath = entry.SourceImagePath
            ?? entry.ProcessedImagePath
            ?? string.Empty;
        var resultItem = new LiveScanResultItem(
            entry.Id,
            document.SessionId,
            entry.TimestampUtc,
            sourceImagePath,
            entry.DebugFolderPath,
            entry.ProcessedImagePath ?? sourceImagePath,
            entry.ProcessingState,
            false,
            PersistLiveScanResultItem,
            message => Status = message);

        resultItem.ApplyProcessedResult(
            entry.OcrText,
            entry.Scan?.Summary ?? "Scan resource: none yet",
            entry.OcrMode,
            entry.OcrSourceSummary,
            entry.Scan?.MatchedResource,
            entry.Scan?.IsTargetMatch ?? false,
            entry.ProcessedImagePath ?? sourceImagePath,
            entry.ProcessingState,
            entry.Scan?.SelectedTargets,
            string.IsNullOrWhiteSpace(entry.Scan?.CorrectValue) ? entry.OcrText : entry.Scan!.CorrectValue);

        return resultItem;
    }

    private OfflineMiningResultItem CreateLiveMiningResultFromHistory(SessionHistoryDocument document, SessionHistoryEntry entry)
    {
        var sourceImagePath = entry.SourceImagePath
            ?? entry.ProcessedImagePath
            ?? string.Empty;

        var restoredScan = (entry.Mining?.RockScan ?? new RockScan()) with
        {
            Id = entry.Id,
            SessionId = document.SessionId,
            TimestampUtc = entry.TimestampUtc,
            SourceType = entry.SourceType,
            CaptureProfile = entry.CaptureProfile,
            SourceImagePath = sourceImagePath,
            DebugFolderPath = entry.DebugFolderPath,
            SourceText = entry.OcrText
        };

        var resultItem = new OfflineMiningResultItem(
            sourceImagePath,
            entry.ProcessedImagePath ?? sourceImagePath,
            restoredScan,
            entry.Mining?.Summary ?? entry.ProcessingState,
            null,
            null,
            message => Status = message,
            PersistMiningSessionHistoryFromItem,
            entry.ProcessingState,
            false);

        resultItem.UsedOcrModeSummary = entry.OcrMode;
        resultItem.OcrSourceSummary = entry.OcrSourceSummary;
        return resultItem;
    }

    private void LoadMiningSessionContextOptions()
    {
        SessionShipOptions.Clear();
        foreach (var ship in MiningContextCatalog.GetShipOptions())
        {
            SessionShipOptions.Add(ship);
        }

        SessionSystemOptions.Clear();
        foreach (var systemName in MiningContextCatalog.GetSystemOptions())
        {
            SessionSystemOptions.Add(systemName);
        }

        SelectedSessionShip = ResolveInitialShipSelection();
        SelectedSessionSystem = ResolveInitialSystemSelection();
        RefreshMiningLocationOptions();

        if (!string.IsNullOrWhiteSpace(_settings.LastLocationKey))
        {
            SelectedSessionLocation = SessionLocationOptions.FirstOrDefault(option => string.Equals(option.Key, _settings.LastLocationKey, StringComparison.OrdinalIgnoreCase));
        }

        UpdateSessionContextSummary();
        PersistMiningSessionMetadata();
    }

    private string ResolveInitialShipSelection()
    {
        if (!string.IsNullOrWhiteSpace(_settings.LastShip)
            && SessionShipOptions.Any(option => string.Equals(option, _settings.LastShip, StringComparison.OrdinalIgnoreCase)))
        {
            return _settings.LastShip;
        }

        return SessionShipOptions.FirstOrDefault() ?? string.Empty;
    }

    private string ResolveInitialSystemSelection()
    {
        if (!string.IsNullOrWhiteSpace(_settings.LastStarSystem)
            && SessionSystemOptions.Any(option => string.Equals(option, _settings.LastStarSystem, StringComparison.OrdinalIgnoreCase)))
        {
            return _settings.LastStarSystem;
        }

        if (SessionSystemOptions.Any(option => string.Equals(option, "Stanton", StringComparison.OrdinalIgnoreCase)))
        {
            return "Stanton";
        }

        return SessionSystemOptions.FirstOrDefault() ?? string.Empty;
    }

    private void RefreshMiningLocationOptions()
    {
        var normalizedSystem = SelectedSessionSystem?.Trim();
        var normalizedSearch = SessionLocationSearchText?.Trim();
        var searchText = normalizedSearch ?? string.Empty;
        var currentSelectionKey = SelectedSessionLocation?.Key;

        var selectedPath = SelectedSessionLocation?.DisplayPath;
        var shouldApplySearch = !string.IsNullOrWhiteSpace(normalizedSearch)
            && !string.Equals(normalizedSearch, selectedPath, StringComparison.OrdinalIgnoreCase);

        var filteredLocations = MiningContextCatalog.GetLocationOptions()
            .Where(option => string.IsNullOrWhiteSpace(normalizedSystem)
                || string.Equals(option.SystemName, normalizedSystem, StringComparison.OrdinalIgnoreCase))
            .Where(option => !shouldApplySearch
                || option.SearchText.Contains(searchText, StringComparison.OrdinalIgnoreCase)
                || option.DisplayPath.Contains(searchText, StringComparison.OrdinalIgnoreCase))
            .ToList();

        SessionLocationOptions.Clear();
        foreach (var option in filteredLocations)
        {
            SessionLocationOptions.Add(option);
        }

        if (string.IsNullOrWhiteSpace(currentSelectionKey))
        {
            return;
        }

        SelectedSessionLocation = SessionLocationOptions.FirstOrDefault(option => string.Equals(option.Key, currentSelectionKey, StringComparison.OrdinalIgnoreCase));
    }

    private bool EnsureSessionContextReady()
    {
        if (IsSessionContextComplete)
        {
            return true;
        }

        Status = "Enter RSI handle, then select ship, system, and location before capturing or processing images.";
        return false;
    }

    private void UpdateSessionContextSummary()
    {
        var rsiHandle = string.IsNullOrWhiteSpace(SessionRsiHandle) ? "RSI handle missing" : SessionRsiHandle;
        var shipName = string.IsNullOrWhiteSpace(SelectedSessionShip) ? "ship missing" : SelectedSessionShip;
        var systemName = string.IsNullOrWhiteSpace(SelectedSessionSystem) ? "system missing" : SelectedSessionSystem;
        var locationPath = SelectedSessionLocation?.DisplayPath ?? "location missing";
        SessionContextSummary = $"Session context: {rsiHandle} | {shipName} | {systemName} | {locationPath}";
        OnPropertyChanged(nameof(IsSessionContextComplete));
    }

    private SessionMetadata BuildCurrentSessionMetadata()
    {
        return new SessionMetadata
        {
            RsiHandle = SessionRsiHandle,
            Ship = SelectedSessionShip,
            StarSystem = SelectedSessionSystem,
            LocationKey = SelectedSessionLocation?.Key ?? string.Empty,
            LocationName = SelectedSessionLocation?.DisplayName ?? string.Empty,
            LocationPath = SelectedSessionLocation?.DisplayPath ?? string.Empty
        };
    }

    private void PersistMiningSessionMetadata()
    {
        _sessionHistoryService.UpsertSessionMetadata(BuildCurrentSessionMetadata());
    }

    private bool IsSelectedScanTarget(string? matchedResourceName)
    {
        return !string.IsNullOrWhiteSpace(matchedResourceName)
            && ScanTargetOptions.Any(option => option.IsSelected && string.Equals(option.Resource, matchedResourceName, StringComparison.OrdinalIgnoreCase));
    }

    partial void OnSaveDebugImagesChanged(bool value)
    {
        _settings.SaveDebugImages = value;
        _settingsService.Save(_settings);
    }

    partial void OnCaptureOnlyModeChanged(bool value)
    {
        _settings.CaptureOnlyMode = value;
        _settingsService.Save(_settings);
        UpdateHotkeyTargetSummary();
    }

    partial void OnSelectedOcrModeChanged(string value)
    {
        _settings.OcrMode = OcrModeCatalog.Auto;
        _settingsService.Save(_settings);
        if (!string.Equals(value, OcrModeCatalog.Auto, StringComparison.OrdinalIgnoreCase))
        {
            SelectedOcrMode = OcrModeCatalog.Auto;
        }
    }

    partial void OnSelectedWorkflowModeChanged(string value)
    {
        if (!WorkflowModeCatalog.IsValid(value))
        {
            SelectedWorkflowMode = WorkflowModeCatalog.Scanning;
            return;
        }

        _settings.ActiveWorkflowMode = value;
        _settingsService.Save(_settings);

        var captureProfile = WorkflowModeCatalog.GetCaptureProfile(value);
        if (!string.IsNullOrWhiteSpace(captureProfile) && !string.Equals(SelectedCaptureProfile, captureProfile, StringComparison.OrdinalIgnoreCase))
        {
            SelectedCaptureProfile = captureProfile;
        }

        OnPropertyChanged(nameof(IsScanningMode));
        OnPropertyChanged(nameof(IsMiningMode));
        OnPropertyChanged(nameof(IsOfflineMode));
        if (!string.Equals(value, WorkflowModeCatalog.Scanning, StringComparison.OrdinalIgnoreCase))
        {
            IsScanTargetSelectorOpen = false;
        }
        SelectedMainTabIndex = string.Equals(value, WorkflowModeCatalog.Offline, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        UpdateHotkeyTargetSummary();
        UpdateCaptureOverlay();
    }

    partial void OnSelectedSessionShipChanged(string value)
    {
        _settings.LastShip = value ?? string.Empty;
        _settingsService.Save(_settings);
        UpdateSessionContextSummary();
        PersistMiningSessionMetadata();
    }

    partial void OnSessionRsiHandleChanged(string value)
    {
        _settings.RsiHandle = value?.Trim() ?? string.Empty;
        if (!string.Equals(SessionRsiHandle, _settings.RsiHandle, StringComparison.Ordinal))
        {
            SessionRsiHandle = _settings.RsiHandle;
            return;
        }

        _settingsService.Save(_settings);
        UpdateSessionContextSummary();
        PersistMiningSessionMetadata();
        _ = RefreshSessionProfileImageAsync(_settings.RsiHandle);
    }

    private async Task RefreshSessionProfileImageAsync(string rsiHandle)
    {
        var normalizedHandle = rsiHandle?.Trim() ?? string.Empty;
        var requestVersion = Interlocked.Increment(ref _sessionProfileImageRequestVersion);

        _sessionProfileImageCancellation?.Cancel();
        _sessionProfileImageCancellation?.Dispose();
        _sessionProfileImageCancellation = new CancellationTokenSource();
        var cancellationToken = _sessionProfileImageCancellation.Token;

        if (string.IsNullOrWhiteSpace(normalizedHandle))
        {
            SessionProfileImage = null;
            SessionProfileImageStatus = "Enter an RSI handle to load the public profile image.";
            return;
        }

        SessionProfileImage = null;
        SessionProfileImageStatus = $"Loading RSI profile image for {normalizedHandle}...";

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            var imagePath = await _rsiProfileImageService.GetProfileImagePathAsync(normalizedHandle, cancellationToken);
            if (requestVersion != _sessionProfileImageRequestVersion || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            SessionProfileImage = LoadImageFromPath(imagePath);
            SessionProfileImageStatus = SessionProfileImage is null
                ? $"No public RSI profile image found for {normalizedHandle}."
                : string.Empty;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            SessionProfileImage = null;
            SessionProfileImageStatus = $"Could not load the RSI profile image for {normalizedHandle}.";
        }
    }

    private static ImageSource? LoadImageFromPath(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return null;
        }

        var bitmapImage = new BitmapImage();
        bitmapImage.BeginInit();
        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
        bitmapImage.UriSource = new Uri(imagePath, UriKind.Absolute);
        bitmapImage.EndInit();
        bitmapImage.Freeze();
        return bitmapImage;
    }

    partial void OnSelectedSessionSystemChanged(string value)
    {
        _settings.LastStarSystem = value ?? string.Empty;
        _settingsService.Save(_settings);
        if (!string.IsNullOrWhiteSpace(SessionLocationSearchText))
        {
            SessionLocationSearchText = string.Empty;
        }
        if (SelectedSessionLocation is not null && !string.Equals(SelectedSessionLocation.SystemName, value, StringComparison.OrdinalIgnoreCase))
        {
            SelectedSessionLocation = null;
        }
        RefreshMiningLocationOptions();
        UpdateSessionContextSummary();
        PersistMiningSessionMetadata();
    }

    partial void OnSessionLocationSearchTextChanged(string value)
    {
        RefreshMiningLocationOptions();
    }

    partial void OnSelectedSessionLocationChanged(MiningLocationOption? value)
    {
        _settings.LastLocationKey = value?.Key ?? string.Empty;
        _settingsService.Save(_settings);
        if (value is not null && !string.Equals(SessionLocationSearchText, value.DisplayPath, StringComparison.OrdinalIgnoreCase))
        {
            SessionLocationSearchText = value.DisplayPath;
        }
        UpdateSessionContextSummary();
        PersistMiningSessionMetadata();
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
        RefreshOfflineInputImages(value);
        UpdateOfflineSelectionPreview();
    }

    partial void OnSelectedOfflineInputImageChanged(OfflineInputImageItem? value)
    {
        LoadOfflinePreview(value?.FilePath);
        UpdateOfflineSelectionPreview();

        if (_isSyncingOfflineScanSelection || IsOfflineMiningSelected || value is null)
        {
            return;
        }

        var matchingResult = OfflineScanResults.FirstOrDefault(item => string.Equals(item.SourceImagePath, value.FilePath, StringComparison.OrdinalIgnoreCase));
        if (matchingResult is not null && !ReferenceEquals(SelectedOfflineScanResult, matchingResult))
        {
            _isSyncingOfflineScanSelection = true;
            try
            {
                SelectedOfflineScanResult = matchingResult;
            }
            finally
            {
                _isSyncingOfflineScanSelection = false;
            }
        }
    }

    partial void OnSelectedOfflineScanResultChanged(OfflineScanResultItem? value)
    {
        if (value is null)
        {
            OfflineResultText = string.Empty;
            OfflineSignatureResourceSummary = "Scan resource: none yet";
            OfflineSelectionModeSummary = "Selection mode: none";
            return;
        }

        OfflineResultText = value.OcrText;
        OfflineSignatureResourceSummary = value.SignatureResourceSummary;
        OfflineMiningRockSummary = "Rock scan: n/a for Scan";
        OfflineSelectionModeSummary = value.SelectionModeSummary;

        if (_isSyncingOfflineScanSelection)
        {
            return;
        }

        var matchingInput = OfflineInputImages.FirstOrDefault(item => string.Equals(item.FilePath, value.SourceImagePath, StringComparison.OrdinalIgnoreCase));
        if (matchingInput is not null && !ReferenceEquals(SelectedOfflineInputImage, matchingInput))
        {
            _isSyncingOfflineScanSelection = true;
            try
            {
                SelectedOfflineInputImage = matchingInput;
            }
            finally
            {
                _isSyncingOfflineScanSelection = false;
            }
        }
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

        var activeQueue = GetOfflineInputQueue(OfflineSelectedCaptureProfile);
        foreach (var imagePath in validImagePaths)
        {
            if (activeQueue.Any(path => string.Equals(path, imagePath, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            activeQueue.Add(imagePath);
        }

        RefreshOfflineInputImages(OfflineSelectedCaptureProfile, SelectedOfflineInputImage?.FilePath);

        OfflineResultText = string.Empty;
        OfflineSignatureResourceSummary = "Scan resource: none yet";
        OfflineMiningRockSummary = "Rock scan: none yet";

        Status = validImagePaths.Count == 1
            ? $"Loaded offline test image: {Path.GetFileName(validImagePaths[0])}"
            : $"Loaded {validImagePaths.Count} offline test images.";
    }

    private List<string> GetOfflineInputQueue(string captureProfile)
    {
        return string.Equals(captureProfile, CaptureProfileCatalog.Mining, StringComparison.OrdinalIgnoreCase)
            ? _offlineMiningInputImagePaths
            : _offlineScanInputImagePaths;
    }

    private void RefreshOfflineInputImages(string captureProfile, string? preferredSelectionPath = null)
    {
        var activeQueue = GetOfflineInputQueue(captureProfile);

        OfflineInputImages.Clear();
        foreach (var imagePath in activeQueue)
        {
            OfflineInputImages.Add(new OfflineInputImageItem(imagePath));
        }

        var selectedItem = !string.IsNullOrWhiteSpace(preferredSelectionPath)
            ? OfflineInputImages.FirstOrDefault(item => string.Equals(item.FilePath, preferredSelectionPath, StringComparison.OrdinalIgnoreCase))
            : null;

        SelectedOfflineInputImage = selectedItem ?? OfflineInputImages.FirstOrDefault();

        if (SelectedOfflineInputImage is null)
        {
            LoadOfflinePreview(null);
        }
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
            OfflineImagePath = "Drop an image here to test Scan or Mining OCR.";
            OfflineImageInfo = "No image loaded.";
            return;
        }

        var bitmapImage = LoadImageFromPath(imagePath) as BitmapImage;
        if (bitmapImage is null)
        {
            _offlineImagePixelWidth = 0;
            _offlineImagePixelHeight = 0;
            OfflinePreviewImage = null;
            OfflineImagePath = "Drop an image here to test Scan or Mining OCR.";
            OfflineImageInfo = "No image loaded.";
            return;
        }

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

        if (!EnsureSessionContextReady())
        {
            return;
        }

        if (string.Equals(OfflineSelectedCaptureProfile, CaptureProfileCatalog.Mining, StringComparison.OrdinalIgnoreCase))
        {
            await RunOfflineMiningBatchAsync();
            return;
        }

        await RunOfflineScanBatchAsync();
    }

    private async Task RunOfflineScanBatchAsync()
    {
        var region = GetCaptureRegion(OfflineSelectedCaptureProfile);
        if (!region.IsValid)
        {
            Status = $"Set the {GetCaptureProfileLabel(OfflineSelectedCaptureProfile)} region first.";
            return;
        }

        OfflineScanResults.Clear();
        SelectedOfflineScanResult = null;

        var processedCount = 0;
        foreach (var inputImage in OfflineInputImages)
        {
            Status = $"Running offline OCR {processedCount + 1}/{OfflineInputImages.Count}: {inputImage.DisplayName}";

            try
            {
                using var sourceBitmap = new Bitmap(inputImage.FilePath);
                if (!TryGetOfflineCropBounds(region, sourceBitmap.Width, sourceBitmap.Height, out var cropBounds, out var useWholeImage, out var errorMessage))
                {
                    OfflineScanResults.Add(new OfflineScanResultItem(
                        inputImage.FilePath,
                        inputImage.FilePath,
                        string.Empty,
                        "Scan resource: unable to evaluate",
                        string.Empty,
                        string.Empty,
                        null,
                        false,
                        $"Skipped: {errorMessage}",
                        "Selection mode: unavailable"));
                    continue;
                }

                var debugOutputDirectory = CreateOfflineDebugOutputDirectory(OfflineSelectedCaptureProfile);
                using var croppedBitmap = sourceBitmap.Clone(cropBounds, sourceBitmap.PixelFormat);

                if (debugOutputDirectory is not null)
                {
                    SaveOfflineDebugArtifacts(debugOutputDirectory, inputImage.FilePath, sourceBitmap, croppedBitmap, cropBounds, useWholeImage);
                }

                var result = await _ocrService.ExtractTextAsync(croppedBitmap, debugOutputDirectory, SelectedOcrMode, OfflineSelectedCaptureProfile);
                var usedOcrModeSummary = OcrModeCatalog.GetDisplayName(result.UsedMode);
                var ocrSourceSummary = GetOcrSourceSummary(result.UsedMode, OfflineSelectedCaptureProfile);
                var offlineSignatureSummary = BuildSignatureResourceSummary(OfflineSelectedCaptureProfile, result.NormalizedText);
                var matchedResourceName = GetMatchedScanResource(OfflineSelectedCaptureProfile, result.NormalizedText);
                var isTargetMatch = IsSelectedScanTarget(matchedResourceName);
                var processedImagePath = ResolveProcessedImagePath(debugOutputDirectory, result.UsedMode) ?? inputImage.FilePath;
                var selectionModeSummary = useWholeImage
                    ? $"Selection mode: full dropped image ({GetCaptureProfileLabel(OfflineSelectedCaptureProfile)} box did not fit)"
                    : $"Selection mode: saved {GetCaptureProfileLabel(OfflineSelectedCaptureProfile)} box";

                LastUsedOcrMode = $"Last OCR mode: {usedOcrModeSummary}";
                LastOcrSourceSummary = ocrSourceSummary;

                OfflineScanResults.Add(new OfflineScanResultItem(
                    inputImage.FilePath,
                    processedImagePath,
                    result.NormalizedText,
                    offlineSignatureSummary,
                    usedOcrModeSummary,
                    ocrSourceSummary,
                    matchedResourceName,
                    isTargetMatch,
                    "Processed",
                    selectionModeSummary));

                PersistScanSessionHistory(
                    Guid.NewGuid().ToString(),
                    DateTime.UtcNow,
                    WorkflowModeCatalog.Offline,
                    "offline",
                    OfflineSelectedCaptureProfile,
                    result.NormalizedText,
                    usedOcrModeSummary,
                    ocrSourceSummary,
                    "Processed",
                    offlineSignatureSummary,
                    result.NormalizedText,
                    matchedResourceName,
                    isTargetMatch,
                    inputImage.FilePath,
                    processedImagePath,
                    debugOutputDirectory,
                    GetSelectedScanTargets());
                IncrementSignatureCount();

                processedCount++;
            }
            catch (Exception ex)
            {
                OfflineScanResults.Add(new OfflineScanResultItem(
                    inputImage.FilePath,
                    inputImage.FilePath,
                    string.Empty,
                    "Scan resource: processing failed",
                    string.Empty,
                    string.Empty,
                    null,
                    false,
                    $"Failed: {ex.Message}",
                    "Selection mode: unavailable"));
            }
        }

        SelectedOfflineScanResult = OfflineScanResults.FirstOrDefault(item => SelectedOfflineInputImage is not null
            && string.Equals(item.SourceImagePath, SelectedOfflineInputImage.FilePath, StringComparison.OrdinalIgnoreCase))
            ?? OfflineScanResults.FirstOrDefault();

        Status = OfflineScanResults.Count == 0
            ? "No offline scan images were processed."
            : $"Processed {processedCount} scan image(s). Review the results list for OCR accuracy.";
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
                csvPath,
                persistCallback: PersistMiningSessionHistoryFromItem);

            OfflineMiningResults.Add(resultItem);
            resultItem.UsedOcrModeSummary = OcrModeCatalog.GetDisplayName(ocrResult.UsedMode);
            resultItem.OcrSourceSummary = GetOcrSourceSummary(ocrResult.UsedMode, OfflineSelectedCaptureProfile);
            PersistMiningSessionHistoryFromItem(resultItem);
            if (HasMeaningfulMiningData(scan))
            {
                IncrementMiningStats(scan);
            }
            processedCount++;
        }

        SelectedOfflineMiningResult = OfflineMiningResults.FirstOrDefault();
        OfflineSignatureResourceSummary = "Scan resource: n/a for Mining";

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
                PersistMiningSessionHistoryFromItem(SelectedOfflineMiningResult);
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
        var filePrefix = string.Equals(captureProfile, CaptureProfileCatalog.Mining, StringComparison.OrdinalIgnoreCase)
            ? "mine"
            : "scan";

        var captureOnlyDirectory = GetCaptureOnlyOutputDirectory(captureProfile);
        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        return Path.Combine(captureOnlyDirectory, $"{filePrefix}_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{uniqueSuffix}.png");
    }

    private static string GetCaptureOnlyOutputDirectory(string captureProfile)
    {
        var folderName = string.Equals(captureProfile, CaptureProfileCatalog.Mining, StringComparison.OrdinalIgnoreCase)
            ? "raw-captures-mine"
            : "raw-captures-scan";

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BobTools",
            folderName);
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

        var sessionMetadata = BuildCurrentSessionMetadata();
        PngMetadataWriter.SaveBitmapWithSessionMetadata(
            sourceBitmap,
            Path.Combine(debugOutputDirectory, "00_source.png"),
            sessionMetadata,
            App.SessionId,
            OfflineSelectedCaptureProfile,
            "offline-source",
            new Dictionary<string, string?>
            {
                ["BobTools.SourceImagePath"] = sourceImagePath
            });
        PngMetadataWriter.SaveBitmapWithSessionMetadata(
            croppedBitmap,
            Path.Combine(debugOutputDirectory, "01_capture.png"),
            sessionMetadata,
            App.SessionId,
            OfflineSelectedCaptureProfile,
            "offline-crop",
            new Dictionary<string, string?>
            {
                ["BobTools.SourceImagePath"] = sourceImagePath,
                ["BobTools.SelectionMode"] = useWholeImage ? "full-image" : "saved-region",
                ["BobTools.CropBounds"] = cropBounds.ToString()
            });

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
        var activeCaptureProfile = WorkflowModeCatalog.GetCaptureProfile(SelectedWorkflowMode);
        if (string.IsNullOrWhiteSpace(activeCaptureProfile))
        {
            _captureOverlayWindow.HideOverlay();
            return;
        }

        var activeRegion = GetCaptureRegion(activeCaptureProfile);
        if (activeRegion.IsValid)
        {
            _captureOverlayWindow.SetCaptureProfile(activeCaptureProfile);
            _captureOverlayWindow.SetRegion(activeRegion);
            return;
        }

        _captureOverlayWindow.HideOverlay();
    }

    private void UpdateHotkeyTargetSummary()
    {
        HotkeyTargetSummary = SelectedWorkflowMode switch
        {
            WorkflowModeCatalog.Scanning when CaptureOnlyMode => "Alt+B target: raw Scan capture",
            WorkflowModeCatalog.Scanning => "Alt+B target: Scan capture",
            WorkflowModeCatalog.Mining when CaptureOnlyMode => "Alt+B target: raw Mining capture",
            WorkflowModeCatalog.Mining => "Alt+B target: Mining queue",
            WorkflowModeCatalog.Offline => "Alt+B disabled in Offline mode",
            _ => "Alt+B target: unknown"
        };
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

        _settings.SignatureCaptureRegion = NormalizeCaptureRegion(captureProfile, region);
    }

    private static CaptureRegion NormalizeCaptureRegion(string captureProfile, CaptureRegion region)
    {
        var fixedSize = CaptureProfileCatalog.GetFixedRegionSize(captureProfile);
        if (fixedSize is null || !region.IsValid)
        {
            return region;
        }

        var centerX = region.X + (region.Width / 2);
        var centerY = region.Y + (region.Height / 2);
        return new CaptureRegion
        {
            X = Math.Max(0, centerX - (fixedSize.Value.Width / 2)),
            Y = Math.Max(0, centerY - (fixedSize.Value.Height / 2)),
            Width = fixedSize.Value.Width,
            Height = fixedSize.Value.Height
        };
    }

    private static bool AreRegionsEqual(CaptureRegion left, CaptureRegion right)
    {
        return left.X == right.X
            && left.Y == right.Y
            && left.Width == right.Width
            && left.Height == right.Height;
    }

    private static string GetCaptureProfileLabel(string captureProfile)
    {
        return CaptureProfileCatalog.GetDisplayName(captureProfile);
    }

    private static string BuildSignatureResourceSummary(string? captureProfile, string? normalizedText)
    {
        return string.Equals(captureProfile, CaptureProfileCatalog.Signature, StringComparison.OrdinalIgnoreCase)
            ? SignatureCatalog.BuildResourceSummary(normalizedText).Replace("Signature resource:", "Scan resource:", StringComparison.Ordinal)
            : "Scan resource: n/a for Mining";
    }

    private static string? GetMatchedScanResource(string? captureProfile, string? normalizedText)
    {
        return string.Equals(captureProfile, CaptureProfileCatalog.Signature, StringComparison.OrdinalIgnoreCase)
            ? SignatureCatalog.FindMatchedResource(normalizedText)
            : null;
    }

    private static string BuildMiningRockSummary(string? captureProfile, string? normalizedText)
    {
        if (!string.Equals(captureProfile, CaptureProfileCatalog.Mining, StringComparison.OrdinalIgnoreCase))
        {
            return "Rock scan: n/a for Scan";
        }

        return RockScanParser.TryParse(normalizedText, out var rockScan)
            ? RockScanParser.BuildSummary(rockScan)
            : "Rock scan: unable to parse";
    }

    private void PersistLiveScanResultItem(LiveScanResultItem resultItem)
    {
        PersistScanSessionHistory(
            resultItem.HistoryEntryId,
            resultItem.TimestampUtc,
            WorkflowModeCatalog.Scanning,
            "live",
            CaptureProfileCatalog.Signature,
            resultItem.OcrText,
            resultItem.UsedOcrModeSummary,
            resultItem.OcrSourceSummary,
            resultItem.ProcessingState,
            resultItem.SignatureResourceSummary,
            resultItem.CorrectValue,
            resultItem.MatchedResourceName,
            resultItem.IsTargetMatch,
            resultItem.SourceImagePath,
            resultItem.ProcessedImagePath,
            resultItem.DebugFolderPath,
            resultItem.SelectedTargets);

        WriteScanTrainingCsv(resultItem.SessionId);
    }

    private void PersistScanSessionHistory(
        string entryId,
        DateTime timestampUtc,
        string workflowMode,
        string sourceType,
        string captureProfile,
        string ocrText,
        string ocrModeSummary,
        string ocrSourceSummary,
        string processingState,
        string signatureSummary,
        string correctValue,
        string? matchedResourceName,
        bool isTargetMatch,
        string? sourceImagePath,
        string? processedImagePath,
        string? debugFolderPath,
        IReadOnlyCollection<string>? selectedTargets = null)
    {
        _sessionHistoryService.UpsertEntry(new SessionHistoryEntry
        {
            Id = entryId,
            ResultKind = "scan",
            TimestampUtc = timestampUtc,
            WorkflowMode = workflowMode,
            CaptureProfile = captureProfile,
            SourceType = sourceType,
            SourceImagePath = sourceImagePath,
            ProcessedImagePath = processedImagePath,
            DebugFolderPath = debugFolderPath,
            OcrText = ocrText,
            OcrMode = ocrModeSummary,
            OcrSourceSummary = ocrSourceSummary,
            ProcessingState = processingState,
            Scan = new ScanSessionPayload
            {
                Summary = signatureSummary,
                CorrectValue = correctValue,
                MatchedResource = matchedResourceName,
                IsTargetMatch = isTargetMatch,
                SelectedTargets = (selectedTargets ?? GetSelectedScanTargets())
                    .Where(target => !string.IsNullOrWhiteSpace(target))
                    .ToList()
            }
        });
    }

    private void PersistMiningSessionHistory(RockScan rockScan, string workflowMode, string ocrModeSummary, string ocrSourceSummary, string processingState, string summary, string? processedImagePath)
    {
        _sessionHistoryService.UpsertEntry(new SessionHistoryEntry
        {
            Id = rockScan.Id,
            ResultKind = "mining",
            TimestampUtc = rockScan.TimestampUtc,
            WorkflowMode = workflowMode,
            CaptureProfile = rockScan.CaptureProfile,
            SourceType = rockScan.SourceType,
            SourceImagePath = rockScan.SourceImagePath,
            ProcessedImagePath = processedImagePath,
            DebugFolderPath = rockScan.DebugFolderPath,
            OcrText = rockScan.SourceText,
            OcrMode = ocrModeSummary,
            OcrSourceSummary = ocrSourceSummary,
            ProcessingState = processingState,
            Mining = new MiningSessionPayload
            {
                Summary = summary,
                RockScan = rockScan
            }
        });
    }

    private void PersistMiningSessionHistoryFromItem(OfflineMiningResultItem resultItem)
    {
        var workflowMode = string.Equals(resultItem.ExtractedScan.SourceType, "offline", StringComparison.OrdinalIgnoreCase)
            ? WorkflowModeCatalog.Offline
            : WorkflowModeCatalog.Mining;

        PersistMiningSessionHistory(
            resultItem.ExtractedScan,
            workflowMode,
            resultItem.UsedOcrModeSummary,
            resultItem.OcrSourceSummary,
            resultItem.ProcessingState,
            resultItem.Summary,
            resultItem.ProcessedImagePath);
    }

    private List<string> GetSelectedScanTargets()
    {
        return ScanTargetOptions
            .Where(option => option.IsSelected)
            .Select(option => option.Resource)
            .ToList();
    }

    private void WriteScanTrainingCsv(string sessionId)
    {
        var rows = LiveScanResults
            .Where(item => string.Equals(item.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            .Select(item => new ScanTrainingExportStore.ScanTrainingRow(
                item.SessionId,
                item.HistoryEntryId,
                item.TimestampUtc,
                CaptureProfileCatalog.Signature,
                ScanTrainingExportStore.ToSessionRelativePath(item.SessionId, item.SourceImagePath),
                ScanTrainingExportStore.ToSessionRelativePath(item.SessionId, item.ProcessedImagePath),
                item.OcrText,
                item.CorrectValue,
                item.SignatureResourceSummary,
                item.MatchedResourceName ?? string.Empty,
                item.IsTargetMatch,
                item.UsedOcrModeSummary,
                item.OcrSourceSummary,
                item.ProcessingState,
                string.Join(" | ", item.SelectedTargets)))
            .ToList();

        if (rows.Count == 0)
        {
            return;
        }

        ScanTrainingExportStore.WriteSessionCsv(sessionId, rows);
    }

    private void ExportScanTrainingPackage()
    {
        var sessionId = LiveScanResults.Any(item => string.Equals(item.SessionId, App.SessionId, StringComparison.OrdinalIgnoreCase))
            ? App.SessionId
            : LiveScanResults.FirstOrDefault()?.SessionId;

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            Status = "No persisted scan results are available to export yet.";
            return;
        }

        try
        {
            WriteScanTrainingCsv(sessionId);

            var sessionRoot = ScanTrainingExportStore.GetSessionRoot(sessionId);
            Directory.CreateDirectory(sessionRoot);
            if (File.Exists(_sessionHistoryService.SessionFilePath) && string.Equals(sessionId, App.SessionId, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(_sessionHistoryService.SessionFilePath, Path.Combine(sessionRoot, "session.json"), true);
            }

            var zipPath = ScanTrainingExportStore.CreateSessionZip(sessionId);
            Status = $"Exported scan training package for session {sessionId} to {zipPath}";
        }
        catch (Exception ex)
        {
            Status = $"Unable to export scan training package: {ex.Message}";
        }
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
        var baseScan = new RockScan
        {
            SessionId = App.SessionId,
            TimestampUtc = DateTime.UtcNow,
            SourceType = "offline",
            CaptureProfile = CaptureProfileCatalog.Mining,
            SourceImagePath = sourceImagePath,
            DebugFolderPath = debugOutputDirectory
        };

        return BuildMiningScan(normalizedText, baseScan);
    }

    private async Task ProcessLiveScanQueueAsync(CancellationToken cancellationToken)
    {
        await foreach (var queuedCapture in _liveScanQueue.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                queuedCapture.ResultItem.SetProcessingState("Processing OCR...", true);

                using var bitmap = new Bitmap(queuedCapture.SourceImagePath);
                var ocrResult = await _ocrService.ExtractTextAsync(bitmap, queuedCapture.DebugOutputDirectory, queuedCapture.RequestedOcrMode, queuedCapture.CaptureProfile);
                var signatureSummary = BuildSignatureResourceSummary(queuedCapture.CaptureProfile, ocrResult.NormalizedText);
                var matchedResourceName = GetMatchedScanResource(queuedCapture.CaptureProfile, ocrResult.NormalizedText);
                var sourceSummary = GetOcrSourceSummary(ocrResult.UsedMode, queuedCapture.CaptureProfile);
                var processedImagePath = ResolveProcessedImagePath(queuedCapture.DebugOutputDirectory, ocrResult.UsedMode) ?? queuedCapture.SourceImagePath;

                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var isTargetMatch = IsSelectedScanTarget(matchedResourceName);
                    var usedOcrModeSummary = OcrModeCatalog.GetDisplayName(ocrResult.UsedMode);

                    LastExtractedText = ocrResult.NormalizedText;
                    AcceptedText = ocrResult.NormalizedText;
                    LastUsedOcrMode = $"Last OCR mode: {usedOcrModeSummary}";
                    LastOcrSourceSummary = sourceSummary;
                    SignatureResourceSummary = signatureSummary;
                    MiningRockSummary = "Rock scan: n/a for Scan";
                    queuedCapture.ResultItem.ApplyProcessedResult(
                        ocrResult.NormalizedText,
                        signatureSummary,
                        usedOcrModeSummary,
                        sourceSummary,
                        matchedResourceName,
                        isTargetMatch,
                        processedImagePath,
                        "Processed",
                        queuedCapture.ResultItem.SelectedTargets);
                    PersistScanSessionHistory(
                        queuedCapture.ResultItem.HistoryEntryId,
                        queuedCapture.ResultItem.TimestampUtc,
                        WorkflowModeCatalog.Scanning,
                        "live",
                        queuedCapture.CaptureProfile,
                        ocrResult.NormalizedText,
                        usedOcrModeSummary,
                        sourceSummary,
                        "Processed",
                        signatureSummary,
                        queuedCapture.ResultItem.CorrectValue,
                        matchedResourceName,
                        isTargetMatch,
                        queuedCapture.SourceImagePath,
                        processedImagePath,
                        queuedCapture.DebugOutputDirectory,
                        queuedCapture.ResultItem.SelectedTargets);
                    WriteScanTrainingCsv(queuedCapture.ResultItem.SessionId);
                    IncrementSignatureCount();
                    UpdateLiveScanQueueSummary();
                    Status = isTargetMatch
                        ? $"Processed live scan capture {queuedCapture.ResultItem.DisplayName}. Target match: {matchedResourceName}."
                        : $"Processed live scan capture {queuedCapture.ResultItem.DisplayName}.";
                });
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    queuedCapture.ResultItem.ApplyProcessingFailure($"Failed: {ex.Message}");
                    PersistLiveScanResultItem(queuedCapture.ResultItem);
                    UpdateLiveScanQueueSummary();
                    Status = $"Live scan OCR failed for {queuedCapture.ResultItem.DisplayName}: {ex.Message}";
                });
            }
        }
    }

    private async Task ProcessLiveMiningQueueAsync(CancellationToken cancellationToken)
    {
        await foreach (var queuedCapture in _liveMiningQueue.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                queuedCapture.ResultItem.SetProcessingState("Processing OCR...", true);

                using var bitmap = new Bitmap(queuedCapture.SourceImagePath);
                var ocrResult = await _ocrService.ExtractTextAsync(bitmap, queuedCapture.DebugOutputDirectory, queuedCapture.RequestedOcrMode, CaptureProfileCatalog.Mining);
                var completedScan = BuildMiningScan(ocrResult.NormalizedText, queuedCapture.PlaceholderScan);
                var persistedJsonPath = RockScanJsonStore.Save(completedScan);
                var persistedCsvPath = RockScanCsvStore.Append(completedScan);
                var processedImagePath = ResolveProcessedImagePath(queuedCapture.DebugOutputDirectory, ocrResult.UsedMode) ?? queuedCapture.SourceImagePath;
                var summary = RockScanParser.BuildSummary(completedScan);

                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var usedOcrModeSummary = OcrModeCatalog.GetDisplayName(ocrResult.UsedMode);
                    var ocrSourceSummary = GetOcrSourceSummary(ocrResult.UsedMode, CaptureProfileCatalog.Mining);

                    LastExtractedText = ocrResult.NormalizedText;
                    AcceptedText = ocrResult.NormalizedText;
                    LastUsedOcrMode = $"Last OCR mode: {usedOcrModeSummary}";
                    LastOcrSourceSummary = ocrSourceSummary;
                    SignatureResourceSummary = "Scan resource: n/a for Mining";
                    MiningRockSummary = summary;
                    queuedCapture.ResultItem.ApplyProcessedResult(completedScan, summary, usedOcrModeSummary, ocrSourceSummary, processedImagePath, persistedJsonPath, persistedCsvPath, "Processed");
                    PersistMiningSessionHistory(completedScan, WorkflowModeCatalog.Mining, usedOcrModeSummary, ocrSourceSummary, "Processed", summary, processedImagePath);
                    if (HasMeaningfulMiningData(completedScan))
                    {
                        IncrementMiningStats(completedScan);
                    }
                    UpdateLiveMiningQueueSummary();
                    Status = $"Processed live mining capture {queuedCapture.ResultItem.DisplayName}.";
                });
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    queuedCapture.ResultItem.ApplyProcessingFailure($"Failed: {ex.Message}");
                    PersistMiningSessionHistory(
                        queuedCapture.PlaceholderScan,
                        WorkflowModeCatalog.Mining,
                        string.Empty,
                        string.Empty,
                        queuedCapture.ResultItem.ProcessingState,
                        queuedCapture.ResultItem.Summary,
                        queuedCapture.ResultItem.ProcessedImagePath);
                    UpdateLiveMiningQueueSummary();
                    Status = $"Live mining OCR failed for {queuedCapture.ResultItem.DisplayName}: {ex.Message}";
                });
            }
        }
    }

    private static RockScan BuildMiningScan(string normalizedText, RockScan baseScan)
    {
        var parsed = RockScanParser.TryParse(normalizedText, out var parsedScan)
            ? parsedScan
            : new RockScan();

        return parsed with
        {
            Id = baseScan.Id,
            SessionId = baseScan.SessionId,
            TimestampUtc = baseScan.TimestampUtc,
            SourceType = baseScan.SourceType,
            CaptureProfile = baseScan.CaptureProfile,
            SourceImagePath = baseScan.SourceImagePath,
            DebugFolderPath = baseScan.DebugFolderPath,
            SourceText = normalizedText
        };
    }

    private void UpdateLiveMiningQueueSummary()
    {
        var processingCount = LiveMiningResults.Count(item => item.IsProcessing);
        LiveMiningQueueSummary = LiveMiningResults.Count == 0
            ? "No live mining captures yet."
            : $"Results: {LiveMiningResults.Count} | Active jobs: {processingCount} | Current session: {CurrentSessionId}";
    }

    private void UpdateLiveScanQueueSummary()
    {
        var processingCount = LiveScanResults.Count(item => item.IsProcessing);
        LiveScanQueueSummary = LiveScanResults.Count == 0
            ? "No live scan captures yet."
            : $"Results: {LiveScanResults.Count} | Active jobs: {processingCount} | Current session: {CurrentSessionId}";
    }

    private static string CreateLiveCaptureDirectory(string captureProfile)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BobTools",
            "LiveCaptures",
            App.SessionId,
            $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{captureProfile}");

        Directory.CreateDirectory(root);
        return root;
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

        if (usedMode.StartsWith("signature-icon-value|", StringComparison.OrdinalIgnoreCase))
        {
            var signatureValuePath = Path.Combine(debugOutputDirectory, "00_signature_value_region.png");
            if (File.Exists(signatureValuePath))
            {
                return signatureValuePath;
            }
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

    private void IncrementSignatureCount()
    {
        LifetimeSignatureCount++;
        _settings.LifetimeSignatureCount = LifetimeSignatureCount;
        _settingsService.Save(_settings);
    }

    private void IncrementMiningStats(RockScan rockScan)
    {
        LifetimeRockCount++;
        LifetimeHighValueOreCount += CountHighValueOres(rockScan);
        _settings.LifetimeRockCount = LifetimeRockCount;
        _settings.LifetimeHighValueOreCount = LifetimeHighValueOreCount;
        _settingsService.Save(_settings);
    }

    private static int CountHighValueOres(RockScan rockScan)
    {
        return rockScan.Elements.Count(element => element.Quality is >= 900);
    }

    private static (string Summary, string? CsvPath, string? JsonPath, RockScan? CompletedScan) AppendMiningRockScanIfApplicable(string captureProfile, string? normalizedText, string sourceType, string? sourceImagePath, string? debugOutputDirectory)
    {
        if (!string.Equals(captureProfile, CaptureProfileCatalog.Mining, StringComparison.OrdinalIgnoreCase))
        {
            return ("Rock scan: n/a for Scan", null, null, null);
        }

        if (!RockScanParser.TryParse(normalizedText, out var rockScan))
        {
            return ("Rock scan: unable to parse", null, null, null);
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
            RockScanJsonStore.Save(completedScan),
            completedScan);
    }

    private static string GetOcrSourceSummary(string? usedMode, string? captureProfile)
    {
        if (usedMode?.StartsWith("signature-icon-value|", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "OCR source: Scan icon-guided crop";
        }

        if (string.Equals(captureProfile, CaptureProfileCatalog.Signature, StringComparison.OrdinalIgnoreCase))
        {
            return "OCR source: Scan fallback OCR";
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
        _sessionProfileImageCancellation?.Cancel();
        _sessionProfileImageCancellation?.Dispose();
        _liveMiningQueueCancellation.Cancel();
        _liveScanQueue.Writer.TryComplete();
        _liveMiningQueue.Writer.TryComplete();
        _overlayRefreshTimer.Stop();
        _overlayRefreshTimer.Tick -= OverlayRefreshTimer_Tick;
        if (_ocrService is IDisposable disposableOcrService)
        {
            disposableOcrService.Dispose();
        }
        _captureOverlayWindow.Close();
    }

    private sealed record QueuedLiveScanCapture(
        string SourceImagePath,
        string? DebugOutputDirectory,
        string RequestedOcrMode,
        string CaptureProfile,
        LiveScanResultItem ResultItem);

    private sealed record QueuedLiveMiningCapture(
        string SourceImagePath,
        string? DebugOutputDirectory,
        string RequestedOcrMode,
        RockScan PlaceholderScan,
        OfflineMiningResultItem ResultItem);
}
