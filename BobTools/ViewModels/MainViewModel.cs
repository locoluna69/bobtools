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
    private readonly AppSettings _settings;
    private readonly CaptureOverlayWindow _captureOverlayWindow;
    private readonly DispatcherTimer _overlayRefreshTimer;
    private readonly Channel<QueuedLiveScanCapture> _liveScanQueue = Channel.CreateUnbounded<QueuedLiveScanCapture>();
    private readonly Channel<QueuedLiveMiningCapture> _liveMiningQueue = Channel.CreateUnbounded<QueuedLiveMiningCapture>();
    private readonly CancellationTokenSource _liveMiningQueueCancellation = new();
    private readonly Task _liveScanQueueTask;
    private readonly Task _liveMiningQueueTask;
    private bool _isUpdatingScanTargetSelection;
    private bool _isSyncingOfflineScanSelection;

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
    [ObservableProperty] private string selectedWorkflowMode = WorkflowModeCatalog.Scanning;
    [ObservableProperty] private string selectedCaptureProfile = CaptureProfileCatalog.Signature;
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
    public ObservableCollection<LiveScanResultItem> LiveScanResults { get; } = new();
    public ObservableCollection<OfflineMiningResultItem> LiveMiningResults { get; } = new();

    public bool IsOfflineMiningSelected => string.Equals(OfflineSelectedCaptureProfile, CaptureProfileCatalog.Mining, StringComparison.OrdinalIgnoreCase);
    public bool IsScanningMode => string.Equals(SelectedWorkflowMode, WorkflowModeCatalog.Scanning, StringComparison.OrdinalIgnoreCase);
    public bool IsMiningMode => string.Equals(SelectedWorkflowMode, WorkflowModeCatalog.Mining, StringComparison.OrdinalIgnoreCase);
    public bool IsOfflineMode => string.Equals(SelectedWorkflowMode, WorkflowModeCatalog.Offline, StringComparison.OrdinalIgnoreCase);

    public MainViewModel(ISettingsService settingsService, ISessionHistoryService sessionHistoryService, IScreenCaptureService screenCaptureService, IOcrService ocrService)
    {
        _settingsService = settingsService;
        _sessionHistoryService = sessionHistoryService;
        _screenCaptureService = screenCaptureService;
        _ocrService = ocrService;
        _settings = _settingsService.Load();
        saveDebugImages = _settings.SaveDebugImages;
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

        if (settingsChanged)
        {
            _settingsService.Save(_settings);
        }

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
        var region = GetCaptureRegion(captureProfile);

        if (!region.IsValid)
        {
            Status = $"Set the {GetCaptureProfileLabel(captureProfile)} region first.";
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
            bitmap.Save(rawImagePath, System.Drawing.Imaging.ImageFormat.Png);

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
            bitmap.Save(rawImagePath, System.Drawing.Imaging.ImageFormat.Png);

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
            File.Copy(imageFile, destinationPath, overwrite: true);

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
        UpdateLiveMiningQueueSummary();

        await _liveMiningQueue.Writer.WriteAsync(
            new QueuedLiveMiningCapture(sourceImagePath, debugOutputDirectory, SelectedOcrMode, placeholderScan, resultItem),
            _liveMiningQueueCancellation.Token);
    }

    private async Task EnqueueLiveScanImageAsync(string sourceImagePath, string? debugOutputDirectory, string captureProfile, string processingState)
    {
        var resultItem = new LiveScanResultItem(
            sourceImagePath,
            sourceImagePath,
            processingState,
            true);

        LiveScanResults.Insert(0, resultItem);
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

        foreach (var imagePath in validImagePaths)
        {
            if (OfflineInputImages.Any(item => string.Equals(item.FilePath, imagePath, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            OfflineInputImages.Add(new OfflineInputImageItem(imagePath));
        }

        OfflineResultText = string.Empty;
        OfflineSignatureResourceSummary = "Scan resource: none yet";
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
            OfflineImagePath = "Drop an image here to test Scan or Mining OCR.";
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
                    matchedResourceName,
                    isTargetMatch,
                    inputImage.FilePath,
                    processedImagePath,
                    debugOutputDirectory);

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
        var activeCaptureProfile = WorkflowModeCatalog.GetCaptureProfile(SelectedWorkflowMode);
        if (string.IsNullOrWhiteSpace(activeCaptureProfile))
        {
            _captureOverlayWindow.HideOverlay();
            return;
        }

        var activeRegion = GetCaptureRegion(activeCaptureProfile);
        if (activeRegion.IsValid)
        {
            _captureOverlayWindow.SetRegion(activeRegion);
            return;
        }

        _captureOverlayWindow.HideOverlay();
    }

    private void UpdateHotkeyTargetSummary()
    {
        HotkeyTargetSummary = SelectedWorkflowMode switch
        {
            WorkflowModeCatalog.Scanning => "Alt+B target: Scan capture",
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

        _settings.SignatureCaptureRegion = region;
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
        string? matchedResourceName,
        bool isTargetMatch,
        string? sourceImagePath,
        string? processedImagePath,
        string? debugFolderPath)
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
                MatchedResource = matchedResourceName,
                IsTargetMatch = isTargetMatch,
                SelectedTargets = ScanTargetOptions
                    .Where(option => option.IsSelected)
                    .Select(option => option.Resource)
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
                        "Processed");
                    PersistScanSessionHistory(
                        queuedCapture.ResultItem.HistoryEntryId,
                        DateTime.UtcNow,
                        WorkflowModeCatalog.Scanning,
                        "live",
                        queuedCapture.CaptureProfile,
                        ocrResult.NormalizedText,
                        usedOcrModeSummary,
                        sourceSummary,
                        "Processed",
                        signatureSummary,
                        matchedResourceName,
                        isTargetMatch,
                        queuedCapture.SourceImagePath,
                        processedImagePath,
                        queuedCapture.DebugOutputDirectory);
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
            : $"Session {CurrentSessionId} | Captures: {LiveMiningResults.Count} | Active jobs: {processingCount}";
    }

    private void UpdateLiveScanQueueSummary()
    {
        var processingCount = LiveScanResults.Count(item => item.IsProcessing);
        LiveScanQueueSummary = LiveScanResults.Count == 0
            ? "No live scan captures yet."
            : $"Session {CurrentSessionId} | Captures: {LiveScanResults.Count} | Active jobs: {processingCount}";
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

    private static (string Summary, string? CsvPath, string? JsonPath) AppendMiningRockScanIfApplicable(string captureProfile, string? normalizedText, string sourceType, string? sourceImagePath, string? debugOutputDirectory)
    {
        if (!string.Equals(captureProfile, CaptureProfileCatalog.Mining, StringComparison.OrdinalIgnoreCase))
        {
            return ("Rock scan: n/a for Scan", null, null);
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
