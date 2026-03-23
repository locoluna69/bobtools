using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BobTools.Models;
using BobTools.Services;
using BobTools.Utils;
using BobTools.Views;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinForms = System.Windows.Forms;

namespace BobTools.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly ISessionHistoryService _sessionHistoryService;
    private readonly IGlobalResultsStore _globalResultsStore;
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
    private readonly ICollectionView _liveScanResultsView;
    private readonly ICollectionView _liveMiningResultsView;
    private readonly List<string> _offlineScanInputImagePaths = [];
    private readonly List<string> _offlineMiningInputImagePaths = [];
    private bool _isUpdatingScanTargetSelection;
    private bool _isUpdatingLiveScanReviewFilterSelection;
    private bool _isUpdatingLiveMiningReviewFilterSelection;
    private bool _isLoadingPersistedLiveResults;
    private bool _isSyncingOfflineScanSelection;
    private CancellationTokenSource? _sessionProfileImageCancellation;
    private int _sessionProfileImageRequestVersion;
    private const string ExportedReviewFilterKey = "Exported";
    private const int LiveResultsPageSize = 25;

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
    [ObservableProperty] private int lifetimeCommonOreCount;
    [ObservableProperty] private int lifetimeRareOreCount;
    [ObservableProperty] private int lifetimeLegendaryOreCount;
    [ObservableProperty] private int lifetimeMythicOreCount;
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
    [ObservableProperty] private string hotkeyTargetSummary = $"{HotkeyCatalog.RecommendedDisplay} target: Scan capture";
    [ObservableProperty] private uint selectedHotkeyModifiers = HotkeyCatalog.DefaultModifiers;
    [ObservableProperty] private uint selectedHotkeyVirtualKey = HotkeyCatalog.DefaultVirtualKey;
    [ObservableProperty] private string configuredHotkeySummary = $"Configured hotkey: {HotkeyCatalog.RecommendedDisplay}";
    [ObservableProperty] private string hotkeyRegistrationStatus = "Hotkey registration is waiting for the main window handle.";
    [ObservableProperty] private string recommendedHotkeySummary = $"Recommended for Star Citizen overlays: {HotkeyCatalog.RecommendedDisplay}";
    [ObservableProperty] private bool isRecordingHotkey;
    [ObservableProperty] private string hotkeyRecorderSummary = "Click Record Shortcut, then press the key combination you want BobTools to use.";
    [ObservableProperty] private string hotkeyRecorderButtonLabel = "Record Shortcut";
    [ObservableProperty] private double overlayOpacity = AppSettings.DefaultOverlayOpacity;
    [ObservableProperty] private double compactModeScale = AppSettings.DefaultCompactModeScale;
    [ObservableProperty] private string overlaySettingsSummary = string.Empty;
    [ObservableProperty] private bool isCompactMode;
    [ObservableProperty] private LiveScanResultItem? latestLiveScanResult;
    [ObservableProperty] private OfflineMiningResultItem? latestLiveMiningResult;
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
    [ObservableProperty] private bool isLiveScanReviewFilterSelectorOpen;
    [ObservableProperty] private bool isLiveMiningReviewFilterSelectorOpen;
    [ObservableProperty] private string selectedLiveScanReviewFilterSummary = "Review filter: all";
    [ObservableProperty] private string selectedLiveMiningReviewFilterSummary = "Review filter: all";
    [ObservableProperty] private string scanTrainingDatasetSummary = "Scan dataset: 0 total | 0 pending | 0 reviewed | 0 exported | 0 rejected";
    [ObservableProperty] private string miningReviewDatasetSummary = "Mining dataset: 0 total | 0 pending | 0 reviewed | 0 rejected";
    [ObservableProperty] private int liveScanCurrentPage = 1;
    [ObservableProperty] private int liveScanTotalPages = 1;
    [ObservableProperty] private string liveScanPageSummary = "Showing 0 of 0";
    [ObservableProperty] private int liveMiningCurrentPage = 1;
    [ObservableProperty] private int liveMiningTotalPages = 1;
    [ObservableProperty] private string liveMiningPageSummary = "Showing 0 of 0";
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
    public ICommand ClearPersistedResultsDataCommand { get; }
    public ICommand PurgeRejectedScanRecordsCommand { get; }
    public ICommand PurgeRejectedMiningRecordsCommand { get; }
    public ICommand ApplyRecommendedHotkeyCommand { get; }
    public ICommand ToggleHotkeyRecordingCommand { get; }
    public ICommand ToggleCompactModeCommand { get; }
    public ICommand ToggleCompactWorkflowModeCommand { get; }
    public ICommand SelectActiveRegionCommand { get; }
    public ICommand CaptureActiveModeCommand { get; }
    public ICommand ReplayDebugFolderCommand { get; }
    public ICommand RunOfflineTestCommand { get; }
    public IAsyncRelayCommand SaveOfflineMiningEditsCommand { get; }
    public IRelayCommand AddOfflineMiningRowCommand { get; }
    public IRelayCommand<OfflineMiningElementEditRow> DeleteOfflineMiningRowCommand { get; }
    public IRelayCommand SelectAllScanTargetsCommand { get; }
    public IRelayCommand ClearScanTargetsCommand { get; }
    public IRelayCommand SelectAllLiveScanReviewFiltersCommand { get; }
    public IRelayCommand ClearLiveScanReviewFiltersCommand { get; }
    public IRelayCommand SelectAllLiveMiningReviewFiltersCommand { get; }
    public IRelayCommand ClearLiveMiningReviewFiltersCommand { get; }
    public IRelayCommand PreviousLiveScanPageCommand { get; }
    public IRelayCommand NextLiveScanPageCommand { get; }
    public IRelayCommand PreviousLiveMiningPageCommand { get; }
    public IRelayCommand NextLiveMiningPageCommand { get; }
    public IReadOnlyList<OcrModeOption> OcrModeOptions { get; } = OcrModeCatalog.Options;
    public IReadOnlyList<CaptureProfileOption> WorkflowModeOptions { get; } = WorkflowModeCatalog.Options;
    public IReadOnlyList<CaptureProfileOption> CaptureProfileOptions { get; } = CaptureProfileCatalog.Options;
    public IReadOnlyList<HotkeyModifierOption> HotkeyModifierOptions { get; } = HotkeyCatalog.ModifierOptions;
    public IReadOnlyList<HotkeyKeyOption> HotkeyKeyOptions { get; } = HotkeyCatalog.KeyOptions;
    public ObservableCollection<OfflineInputImageItem> OfflineInputImages { get; } = new();
    public ObservableCollection<OfflineScanResultItem> OfflineScanResults { get; } = new();
    public ObservableCollection<OfflineMiningResultItem> OfflineMiningResults { get; } = new();
    public ObservableCollection<ScanTargetOption> ScanTargetOptions { get; } = new();
    public ObservableCollection<string> SessionShipOptions { get; } = new();
    public ObservableCollection<string> SessionSystemOptions { get; } = new();
    public ObservableCollection<MiningLocationOption> SessionLocationOptions { get; } = new();
    public ObservableCollection<LiveScanResultItem> LiveScanResults { get; } = new();
    public ObservableCollection<OfflineMiningResultItem> LiveMiningResults { get; } = new();
    public ObservableCollection<LiveScanResultItem> VisibleLiveScanResults { get; } = new();
    public ObservableCollection<OfflineMiningResultItem> VisibleLiveMiningResults { get; } = new();
    public ObservableCollection<ReviewFilterOption> LiveScanReviewFilterOptions { get; } = new();
    public ObservableCollection<ReviewFilterOption> LiveMiningReviewFilterOptions { get; } = new();
    public ICollectionView LiveScanResultsView => _liveScanResultsView;
    public ICollectionView LiveMiningResultsView => _liveMiningResultsView;

    public bool IsOfflineMiningSelected => string.Equals(OfflineSelectedCaptureProfile, CaptureProfileCatalog.Mining, StringComparison.OrdinalIgnoreCase);
    public bool IsScanningMode => string.Equals(SelectedWorkflowMode, WorkflowModeCatalog.Scanning, StringComparison.OrdinalIgnoreCase);
    public bool IsMiningMode => string.Equals(SelectedWorkflowMode, WorkflowModeCatalog.Mining, StringComparison.OrdinalIgnoreCase);
    public bool IsOfflineMode => string.Equals(SelectedWorkflowMode, WorkflowModeCatalog.Offline, StringComparison.OrdinalIgnoreCase);
    public string LiveFooterSummary => SelectedWorkflowMode switch
    {
        WorkflowModeCatalog.Scanning => LiveScanQueueSummary,
        WorkflowModeCatalog.Mining => LiveMiningQueueSummary,
        WorkflowModeCatalog.Offline => "Offline mode uses queued folder import only.",
        _ => string.Empty
    };
    public bool IsSessionContextComplete => !string.IsNullOrWhiteSpace(SessionRsiHandle)
        && !string.IsNullOrWhiteSpace(SelectedSessionShip)
        && !string.IsNullOrWhiteSpace(SelectedSessionSystem)
        && SelectedSessionLocation is not null;
    public string CompactModeToggleLabel => IsCompactMode ? "Full View" : "Compact Mode";
    public string ActiveRegionButtonLabel => IsMiningMode ? "Set Mining Area" : "Set Scan Area";
    public string ActiveCaptureButtonLabel => IsMiningMode ? "Capture Mining" : "Capture Scan";
    public string CompactWorkflowToggleLabel => IsMiningMode ? "Switch To Scan" : "Switch To Mining";
    public string CompactModeBadgeLabel => IsMiningMode ? "MINING" : "SCAN";
    public string CompactModeSubtitle => IsMiningMode ? "Ore sweep and fracture stats" : "Signature sweep and target hits";
    public bool CanGoToPreviousLiveScanPage => LiveScanCurrentPage > 1;
    public bool CanGoToNextLiveScanPage => LiveScanCurrentPage < LiveScanTotalPages;
    public bool CanGoToPreviousLiveMiningPage => LiveMiningCurrentPage > 1;
    public bool CanGoToNextLiveMiningPage => LiveMiningCurrentPage < LiveMiningTotalPages;

    public MainViewModel(ISettingsService settingsService, ISessionHistoryService sessionHistoryService, IGlobalResultsStore globalResultsStore, IScreenCaptureService screenCaptureService, IOcrService ocrService, IRsiProfileImageService rsiProfileImageService)
    {
        _settingsService = settingsService;
        _sessionHistoryService = sessionHistoryService;
        _globalResultsStore = globalResultsStore;
        _screenCaptureService = screenCaptureService;
        _ocrService = ocrService;
        _rsiProfileImageService = rsiProfileImageService;
        _settings = _settingsService.Load();
        saveDebugImages = _settings.SaveDebugImages;
        captureOnlyMode = _settings.CaptureOnlyMode;
        selectedHotkeyModifiers = _settings.HotkeyModifiers;
        selectedHotkeyVirtualKey = _settings.HotkeyVirtualKey;
        overlayOpacity = _settings.OverlayOpacity;
        compactModeScale = _settings.CompactModeScale;
        isCompactMode = false;
        lifetimeSignatureCount = _settings.LifetimeSignatureCount;
        lifetimeRockCount = _settings.LifetimeRockCount;
        lifetimeCommonOreCount = _settings.LifetimeCommonOreCount;
        lifetimeRareOreCount = _settings.LifetimeRareOreCount;
        lifetimeLegendaryOreCount = _settings.LifetimeLegendaryOreCount;
        lifetimeMythicOreCount = _settings.LifetimeMythicOreCount;
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

        if (_settings.IsCompactMode)
        {
            _settings.IsCompactMode = false;
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
        _liveScanResultsView = CollectionViewSource.GetDefaultView(LiveScanResults);
        _liveScanResultsView.Filter = ShouldIncludeLiveScanResult;
        _liveMiningResultsView = CollectionViewSource.GetDefaultView(LiveMiningResults);
        _liveMiningResultsView.Filter = ShouldIncludeLiveMiningResult;
        LoadReviewFilterOptions();
        LiveScanResults.CollectionChanged += LiveScanResults_CollectionChanged;
        LiveMiningResults.CollectionChanged += LiveMiningResults_CollectionChanged;
        LoadScanTargetOptions();
        LoadMiningSessionContextOptions();
        SeedGlobalResultsStore();
        LoadPersistedLiveResults();
        _ = ResumePendingLiveQueueEntriesAsync();
        UpdateCaptureRegionSummaries();
        UpdateLiveScanQueueSummary();
        UpdateConfiguredHotkeySummary();
        UpdateHotkeyTargetSummary();
        UpdateOverlaySettingsSummary();
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
        ClearPersistedResultsDataCommand = new RelayCommand(ClearPersistedResultsData);
        PurgeRejectedScanRecordsCommand = new RelayCommand(PurgeRejectedScanRecords);
        PurgeRejectedMiningRecordsCommand = new RelayCommand(PurgeRejectedMiningRecords);
        ApplyRecommendedHotkeyCommand = new RelayCommand(ApplyRecommendedHotkey);
        ToggleHotkeyRecordingCommand = new RelayCommand(ToggleHotkeyRecording);
        ToggleCompactModeCommand = new RelayCommand(ToggleCompactMode);
        ToggleCompactWorkflowModeCommand = new RelayCommand(ToggleCompactWorkflowMode);
        SelectActiveRegionCommand = new RelayCommand(SelectActiveRegion);
        CaptureActiveModeCommand = new AsyncRelayCommand(CaptureActiveModeAsync);
        ReplayDebugFolderCommand = new AsyncRelayCommand(ReplayDebugFolderAsync);
        RunOfflineTestCommand = new AsyncRelayCommand(RunOfflineTestAsync);
        SaveOfflineMiningEditsCommand = new AsyncRelayCommand(SaveOfflineMiningEditsAsync, () => SelectedOfflineMiningResult is not null);
        AddOfflineMiningRowCommand = new RelayCommand(AddOfflineMiningRow, () => SelectedOfflineMiningResult is not null);
        DeleteOfflineMiningRowCommand = new RelayCommand<OfflineMiningElementEditRow>(DeleteOfflineMiningRow);
        SelectAllScanTargetsCommand = new RelayCommand(() => SetAllScanTargets(true));
        ClearScanTargetsCommand = new RelayCommand(() => SetAllScanTargets(false));
        SelectAllLiveScanReviewFiltersCommand = new RelayCommand(() => SetAllLiveScanReviewFilters(true));
        ClearLiveScanReviewFiltersCommand = new RelayCommand(() => SetAllLiveScanReviewFilters(false));
        SelectAllLiveMiningReviewFiltersCommand = new RelayCommand(() => SetAllLiveMiningReviewFilters(true));
        ClearLiveMiningReviewFiltersCommand = new RelayCommand(() => SetAllLiveMiningReviewFilters(false));
        PreviousLiveScanPageCommand = new RelayCommand(() => ChangeLiveScanPage(-1), () => CanGoToPreviousLiveScanPage);
        NextLiveScanPageCommand = new RelayCommand(() => ChangeLiveScanPage(1), () => CanGoToNextLiveScanPage);
        PreviousLiveMiningPageCommand = new RelayCommand(() => ChangeLiveMiningPage(-1), () => CanGoToPreviousLiveMiningPage);
        NextLiveMiningPageCommand = new RelayCommand(() => ChangeLiveMiningPage(1), () => CanGoToNextLiveMiningPage);
        UpdateScanTrainingDatasetSummary();
        UpdateMiningReviewDatasetSummary();
    }

    partial void OnIsCompactModeChanged(bool value)
    {
        _settings.IsCompactMode = value;
        _settingsService.Save(_settings);

        if (value && IsOfflineMode)
        {
            SelectedWorkflowMode = WorkflowModeCatalog.Scanning;
        }

        OnPropertyChanged(nameof(CompactModeToggleLabel));
    }
    private void UpdateCompactResults()
    {
        LatestLiveScanResult = LiveScanResults.FirstOrDefault();
        LatestLiveMiningResult = LiveMiningResults.FirstOrDefault();
    }

    private void ChangeLiveScanPage(int delta)
    {
        LiveScanCurrentPage = Math.Max(1, LiveScanCurrentPage + delta);
        RefreshLiveScanResultsPage();
    }

    private void ChangeLiveMiningPage(int delta)
    {
        LiveMiningCurrentPage = Math.Max(1, LiveMiningCurrentPage + delta);
        RefreshLiveMiningResultsPage();
    }

    private bool ShouldIncludeLiveScanResult(object item)
    {
        if (item is not LiveScanResultItem resultItem)
        {
            return false;
        }

        var selectedFilters = GetSelectedReviewFilters(LiveScanReviewFilterOptions);
        if (selectedFilters.Count == 0)
        {
            return false;
        }

        if (selectedFilters.Contains(ExportedReviewFilterKey) && resultItem.ExportedAtUtc is not null)
        {
            return true;
        }

        if (selectedFilters.Contains(ScanReviewCatalog.Pending)
            && string.Equals(resultItem.ReviewStatus, ScanReviewCatalog.Pending, StringComparison.Ordinal))
        {
            return true;
        }

        if (selectedFilters.Contains(ScanReviewCatalog.Reviewed)
            && string.Equals(resultItem.ReviewStatus, ScanReviewCatalog.Reviewed, StringComparison.Ordinal)
            && resultItem.ExportedAtUtc is null)
        {
            return true;
        }

        if (selectedFilters.Contains(ScanReviewCatalog.Rejected)
            && string.Equals(resultItem.ReviewStatus, ScanReviewCatalog.Rejected, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private bool ShouldIncludeLiveMiningResult(object item)
    {
        if (item is not OfflineMiningResultItem resultItem)
        {
            return false;
        }

        var selectedFilters = GetSelectedReviewFilters(LiveMiningReviewFilterOptions);
        if (selectedFilters.Count == 0)
        {
            return false;
        }

        return (selectedFilters.Contains(ScanReviewCatalog.Pending)
                && string.Equals(resultItem.ReviewStatus, ScanReviewCatalog.Pending, StringComparison.Ordinal))
            || (selectedFilters.Contains(ScanReviewCatalog.Reviewed)
                && string.Equals(resultItem.ReviewStatus, ScanReviewCatalog.Reviewed, StringComparison.Ordinal))
            || (selectedFilters.Contains(ScanReviewCatalog.Rejected)
                && string.Equals(resultItem.ReviewStatus, ScanReviewCatalog.Rejected, StringComparison.Ordinal));
    }

    private void LiveScanResults_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems.OfType<LiveScanResultItem>())
            {
                item.PropertyChanged -= LiveScanResultItem_PropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems.OfType<LiveScanResultItem>())
            {
                item.PropertyChanged += LiveScanResultItem_PropertyChanged;
            }
        }

        UpdateCompactResults();
        UpdateScanTrainingDatasetSummary();
        if (_isLoadingPersistedLiveResults)
        {
            return;
        }

        RefreshLiveScanResultsPage();
    }

    private void LiveScanResultItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LiveScanResultItem.ReviewStatus)
            or nameof(LiveScanResultItem.ExportedAtUtc)
            or nameof(LiveScanResultItem.ExportBatchId))
        {
            UpdateScanTrainingDatasetSummary();
            RefreshLiveScanResultsPage();
        }
    }

    private void LiveMiningResults_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems.OfType<OfflineMiningResultItem>())
            {
                item.PropertyChanged -= LiveMiningResultItem_PropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems.OfType<OfflineMiningResultItem>())
            {
                item.PropertyChanged += LiveMiningResultItem_PropertyChanged;
            }
        }

        UpdateCompactResults();
        UpdateMiningReviewDatasetSummary();
        if (_isLoadingPersistedLiveResults)
        {
            return;
        }

        RefreshLiveMiningResultsPage();
    }

    private void LiveMiningResultItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OfflineMiningResultItem.ReviewStatus)
            or nameof(OfflineMiningResultItem.ExportedAtUtc)
            or nameof(OfflineMiningResultItem.ExportBatchId))
        {
            UpdateMiningReviewDatasetSummary();
            RefreshLiveMiningResultsPage();
        }
    }

    private void UpdateScanTrainingDatasetSummary()
    {
        var totalCount = LiveScanResults.Count;
        var pendingCount = LiveScanResults.Count(item => string.Equals(item.ReviewStatus, ScanReviewCatalog.Pending, StringComparison.Ordinal));
        var reviewedCount = LiveScanResults.Count(item => string.Equals(item.ReviewStatus, ScanReviewCatalog.Reviewed, StringComparison.Ordinal) && item.ExportedAtUtc is null);
        var exportedCount = LiveScanResults.Count(item => item.ExportedAtUtc is not null);
        var rejectedCount = LiveScanResults.Count(item => string.Equals(item.ReviewStatus, ScanReviewCatalog.Rejected, StringComparison.Ordinal));
        ScanTrainingDatasetSummary = $"Scan dataset: {totalCount} total | {pendingCount} pending | {reviewedCount} reviewed | {exportedCount} exported | {rejectedCount} rejected";
    }

    private void UpdateMiningReviewDatasetSummary()
    {
        var totalCount = LiveMiningResults.Count;
        var pendingCount = LiveMiningResults.Count(item => string.Equals(item.ReviewStatus, ScanReviewCatalog.Pending, StringComparison.Ordinal));
        var reviewedCount = LiveMiningResults.Count(item => string.Equals(item.ReviewStatus, ScanReviewCatalog.Reviewed, StringComparison.Ordinal));
        var rejectedCount = LiveMiningResults.Count(item => string.Equals(item.ReviewStatus, ScanReviewCatalog.Rejected, StringComparison.Ordinal));
        MiningReviewDatasetSummary = $"Mining dataset: {totalCount} total | {pendingCount} pending | {reviewedCount} reviewed | {rejectedCount} rejected";
    }
    private void ToggleCompactMode()
    {
        IsCompactMode = !IsCompactMode;
        Status = IsCompactMode
            ? "Compact mode enabled for primary-screen overlay use."
            : "Full companion mode restored.";
    }

    private void ToggleCompactWorkflowMode()
    {
        SelectedWorkflowMode = IsMiningMode
            ? WorkflowModeCatalog.Scanning
            : WorkflowModeCatalog.Mining;
    }

    private void SelectActiveRegion()
    {
        SelectRegion(IsMiningMode ? CaptureProfileCatalog.Mining : CaptureProfileCatalog.Signature);
    }

    private Task CaptureActiveModeAsync()
    {
        return CaptureAndReviewAsync(IsMiningMode ? CaptureProfileCatalog.Mining : CaptureProfileCatalog.Signature);
    }

    public Task CaptureAndReviewAsync()
    {
        var captureProfile = WorkflowModeCatalog.GetCaptureProfile(SelectedWorkflowMode);
        if (string.IsNullOrWhiteSpace(captureProfile))
        {
            Status = $"Offline mode does not use {HotkeyCatalog.Format(SelectedHotkeyModifiers, SelectedHotkeyVirtualKey)} capture. Use Queue Folder to add mining images for processing.";
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
        var placeholderScan = CreateMiningScanBase("live", captureProfile, sourceImagePath, debugOutputDirectory);

        var resultItem = new OfflineMiningResultItem(
            sourceImagePath,
            sourceImagePath,
            placeholderScan,
            queuedSummary,
            null,
            null,
            message => Status = message,
            (item, previousScan, updatedScan) => PersistMiningSessionHistoryFromItem(item, previousScan, updatedScan),
            "Queued",
            true,
            true);

        LiveMiningResults.Insert(0, resultItem);
        PersistMiningSessionHistoryFromItem(resultItem);
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
        UpdateLiveMiningTargetHighlights();
    }

    private void LoadReviewFilterOptions()
    {
        InitializeReviewFilterOptions(
            LiveScanReviewFilterOptions,
            [
                (ScanReviewCatalog.Pending, ScanReviewCatalog.Pending),
                (ScanReviewCatalog.Reviewed, ScanReviewCatalog.Reviewed),
                (ScanReviewCatalog.Rejected, ScanReviewCatalog.Rejected),
                (ExportedReviewFilterKey, ExportedReviewFilterKey)
            ],
            LiveScanReviewFilterOption_PropertyChanged);

        InitializeReviewFilterOptions(
            LiveMiningReviewFilterOptions,
            [
                (ScanReviewCatalog.Pending, ScanReviewCatalog.Pending),
                (ScanReviewCatalog.Reviewed, ScanReviewCatalog.Reviewed),
                (ScanReviewCatalog.Rejected, ScanReviewCatalog.Rejected)
            ],
            LiveMiningReviewFilterOption_PropertyChanged);

        UpdateSelectedLiveScanReviewFilterSummary();
        UpdateSelectedLiveMiningReviewFilterSummary();
        RefreshLiveScanResultsPage(resetPage: true);
        RefreshLiveMiningResultsPage(resetPage: true);
    }

    private static void InitializeReviewFilterOptions(
        ObservableCollection<ReviewFilterOption> options,
        IReadOnlyList<(string Key, string DisplayName)> definitions,
        PropertyChangedEventHandler propertyChangedHandler)
    {
        options.Clear();
        foreach (var (key, displayName) in definitions)
        {
            var option = new ReviewFilterOption(key, displayName);
            option.PropertyChanged += propertyChangedHandler;
            options.Add(option);
        }
    }

    private void LiveScanReviewFilterOption_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isUpdatingLiveScanReviewFilterSelection || !string.Equals(e.PropertyName, nameof(ReviewFilterOption.IsSelected), StringComparison.Ordinal))
        {
            return;
        }

        UpdateSelectedLiveScanReviewFilterSummary();
        RefreshLiveScanResultsPage(resetPage: true);
    }

    private void LiveMiningReviewFilterOption_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isUpdatingLiveMiningReviewFilterSelection || !string.Equals(e.PropertyName, nameof(ReviewFilterOption.IsSelected), StringComparison.Ordinal))
        {
            return;
        }

        UpdateSelectedLiveMiningReviewFilterSummary();
        RefreshLiveMiningResultsPage(resetPage: true);
    }

    private void SetAllLiveScanReviewFilters(bool isSelected)
    {
        _isUpdatingLiveScanReviewFilterSelection = true;
        try
        {
            foreach (var option in LiveScanReviewFilterOptions)
            {
                option.IsSelected = isSelected;
            }
        }
        finally
        {
            _isUpdatingLiveScanReviewFilterSelection = false;
        }

        UpdateSelectedLiveScanReviewFilterSummary();
        RefreshLiveScanResultsPage(resetPage: true);
    }

    private void SetAllLiveMiningReviewFilters(bool isSelected)
    {
        _isUpdatingLiveMiningReviewFilterSelection = true;
        try
        {
            foreach (var option in LiveMiningReviewFilterOptions)
            {
                option.IsSelected = isSelected;
            }
        }
        finally
        {
            _isUpdatingLiveMiningReviewFilterSelection = false;
        }

        UpdateSelectedLiveMiningReviewFilterSummary();
        RefreshLiveMiningResultsPage(resetPage: true);
    }

    private void UpdateSelectedLiveScanReviewFilterSummary()
    {
        SelectedLiveScanReviewFilterSummary = BuildReviewFilterSummary("Review filter", LiveScanReviewFilterOptions);
    }

    private void UpdateSelectedLiveMiningReviewFilterSummary()
    {
        SelectedLiveMiningReviewFilterSummary = BuildReviewFilterSummary("Review filter", LiveMiningReviewFilterOptions);
    }

    private static string BuildReviewFilterSummary(string prefix, IReadOnlyCollection<ReviewFilterOption> options)
    {
        var selected = options.Where(option => option.IsSelected).Select(option => option.DisplayName).ToList();

        if (selected.Count == 0)
        {
            return $"{prefix}: none";
        }

        if (selected.Count == options.Count)
        {
            return $"{prefix}: all";
        }

        return selected.Count switch
        {
            1 => $"{prefix}: {selected[0]}",
            2 => $"{prefix}: {selected[0]}, {selected[1]}",
            _ => $"{prefix}: {selected[0]}, {selected[1]} +{selected.Count - 2}"
        };
    }

    private static HashSet<string> GetSelectedReviewFilters(IEnumerable<ReviewFilterOption> options)
    {
        return options
            .Where(option => option.IsSelected)
            .Select(option => option.Key)
            .ToHashSet(StringComparer.Ordinal);
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
        UpdateLiveMiningTargetHighlights();
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
        UpdateLiveMiningTargetHighlights();
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

    private void UpdateLiveMiningTargetHighlights()
    {
        var selectedTargets = GetSelectedScanTargets();
        foreach (var resultItem in LiveMiningResults)
        {
            resultItem.ApplySelectedTargetResources(selectedTargets);
        }
    }

    private void SeedGlobalResultsStore()
    {
        if (_globalResultsStore.Exists())
        {
            return;
        }

        var records = _sessionHistoryService.LoadAllDocuments()
            .SelectMany(document => document.Events.Select(entry => CreateGlobalResultRecordFromHistory(document, entry)))
            .Where(record => record is not null)
            .Select(record => record!)
            .GroupBy(record => record.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(record => record.LastUpdatedUtc)
                .ThenByDescending(record => record.Entry.TimestampUtc)
                .First())
            .ToList();

        if (records.Count == 0)
        {
            return;
        }

        _globalResultsStore.ReplaceAll(records);
    }

    private void LoadPersistedLiveResults()
    {
        _isLoadingPersistedLiveResults = true;
        LiveScanResults.Clear();
        LiveMiningResults.Clear();

        var persistedEntries = _globalResultsStore.LoadAll()
            .Where(record => record.Entry is not null)
            .GroupBy(record => record.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(record => record.LastUpdatedUtc)
                .ThenByDescending(record => record.Entry.TimestampUtc)
                .First())
            .OrderByDescending(record => record.Entry.TimestampUtc)
            .ToList();

        foreach (var record in persistedEntries)
        {
            var entry = record.Entry;
            if (string.Equals(entry.ResultKind, "scan", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entry.SourceType, "offline", StringComparison.OrdinalIgnoreCase))
            {
                LiveScanResults.Add(CreateLiveScanResultFromRecord(record));
                continue;
            }

            if (string.Equals(entry.ResultKind, "mining", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entry.WorkflowMode, WorkflowModeCatalog.Offline, StringComparison.OrdinalIgnoreCase))
            {
                LiveMiningResults.Add(CreateLiveMiningResultFromRecord(record));
            }
        }

        UpdateLiveScanTargetHighlights();
        UpdateLiveMiningTargetHighlights();
        UpdateLiveScanQueueSummary();
        UpdateLiveMiningQueueSummary();
        _isLoadingPersistedLiveResults = false;
        RefreshLiveScanResultsPage(resetPage: true);
        RefreshLiveMiningResultsPage(resetPage: true);
    }

    private async Task ResumePendingLiveQueueEntriesAsync()
    {
        var pendingScanItems = LiveScanResults
            .Where(item => ShouldResumeProcessing(item.ProcessingState))
            .ToList();
        var pendingMiningItems = LiveMiningResults
            .Where(item => ShouldResumeProcessing(item.ProcessingState))
            .ToList();

        foreach (var resultItem in pendingScanItems)
        {
            await ResumePendingLiveScanAsync(resultItem);
        }

        foreach (var resultItem in pendingMiningItems)
        {
            await ResumePendingLiveMiningAsync(resultItem);
        }

        if (pendingScanItems.Count > 0 || pendingMiningItems.Count > 0)
        {
            UpdateLiveScanQueueSummary();
            UpdateLiveMiningQueueSummary();
            Status = $"Resumed {pendingScanItems.Count} scan and {pendingMiningItems.Count} mining queue item(s) from session history.";
        }
    }

    private LiveScanResultItem CreateLiveScanResultFromRecord(GlobalResultRecord record)
    {
        var entry = record.Entry;
        var sourceImagePath = entry.SourceImagePath
            ?? entry.ProcessedImagePath
            ?? string.Empty;
        var resultItem = new LiveScanResultItem(
            entry.Id,
            record.SessionId,
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
        resultItem.ApplyReviewMetadata(record.ReviewStatus, record.ExportedAtUtc, record.ExportBatchId);

        return resultItem;
    }

    private OfflineMiningResultItem CreateLiveMiningResultFromRecord(GlobalResultRecord record)
    {
        var entry = record.Entry;
        var sourceImagePath = entry.SourceImagePath
            ?? entry.ProcessedImagePath
            ?? string.Empty;

        var restoredScan = (entry.Mining?.RockScan ?? new RockScan()) with
        {
            Id = entry.Id,
            SessionId = record.SessionId,
            TimestampUtc = entry.TimestampUtc,
            SourceType = entry.SourceType,
            CaptureProfile = entry.CaptureProfile,
            SourceImagePath = sourceImagePath,
            DebugFolderPath = entry.DebugFolderPath,
            SourceText = entry.OcrText
        };
        var restoredSummary = RockScanParser.BuildSummary(restoredScan);

        var resultItem = new OfflineMiningResultItem(
            sourceImagePath,
            entry.ProcessedImagePath ?? sourceImagePath,
            restoredScan,
            restoredSummary,
            null,
            null,
            message => Status = message,
            (item, previousScan, updatedScan) => PersistMiningSessionHistoryFromItem(item, previousScan, updatedScan),
            entry.ProcessingState,
            false,
            true);

        resultItem.UsedOcrModeSummary = entry.OcrMode;
        resultItem.OcrSourceSummary = entry.OcrSourceSummary;
        resultItem.ApplyReviewMetadata(record.ReviewStatus, record.ExportedAtUtc, record.ExportBatchId);
        return resultItem;
    }

    private GlobalResultRecord? CreateGlobalResultRecordFromHistory(SessionHistoryDocument document, SessionHistoryEntry entry)
    {
        if (string.Equals(entry.ResultKind, "scan", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(entry.SourceType, "offline", StringComparison.OrdinalIgnoreCase))
        {
            return new GlobalResultRecord
            {
                Id = entry.Id,
                SessionId = document.SessionId,
                LastUpdatedUtc = document.LastUpdatedUtc,
                ReviewStatus = ScanReviewCatalog.Pending,
                ExportedAtUtc = null,
                ExportBatchId = string.Empty,
                Entry = CloneSessionHistoryEntry(entry)
            };
        }

        if (string.Equals(entry.ResultKind, "mining", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(entry.WorkflowMode, WorkflowModeCatalog.Offline, StringComparison.OrdinalIgnoreCase))
        {
            var sourceImagePath = entry.SourceImagePath
                ?? entry.ProcessedImagePath
                ?? string.Empty;
            var mergedRockScan = MergeRockScanWithSessionMetadata((entry.Mining?.RockScan ?? new RockScan()) with
            {
                Id = entry.Id,
                SessionId = document.SessionId,
                TimestampUtc = entry.TimestampUtc,
                SourceType = entry.SourceType,
                CaptureProfile = entry.CaptureProfile,
                SourceImagePath = sourceImagePath,
                DebugFolderPath = entry.DebugFolderPath,
                SourceText = entry.OcrText
            }, document.Metadata, preferSessionLocation: !RockScanHasSessionMetadata(entry.Mining?.RockScan));

            var clonedEntry = CloneSessionHistoryEntry(entry);
            clonedEntry.SourceImagePath = sourceImagePath;
            clonedEntry.Mining = new MiningSessionPayload
            {
                Summary = RockScanParser.BuildSummary(mergedRockScan),
                RockScan = mergedRockScan
            };

            return new GlobalResultRecord
            {
                Id = entry.Id,
                SessionId = document.SessionId,
                LastUpdatedUtc = document.LastUpdatedUtc,
                ReviewStatus = ScanReviewCatalog.Pending,
                ExportedAtUtc = null,
                ExportBatchId = string.Empty,
                Entry = clonedEntry
            };
        }

        return null;
    }

    private static SessionHistoryEntry CloneSessionHistoryEntry(SessionHistoryEntry entry)
    {
        return new SessionHistoryEntry
        {
            Id = entry.Id,
            ResultKind = entry.ResultKind,
            TimestampUtc = entry.TimestampUtc,
            WorkflowMode = entry.WorkflowMode,
            CaptureProfile = entry.CaptureProfile,
            SourceType = entry.SourceType,
            SourceImagePath = entry.SourceImagePath,
            ProcessedImagePath = entry.ProcessedImagePath,
            DebugFolderPath = entry.DebugFolderPath,
            OcrText = entry.OcrText,
            OcrMode = entry.OcrMode,
            OcrSourceSummary = entry.OcrSourceSummary,
            ProcessingState = entry.ProcessingState,
            Scan = entry.Scan is null
                ? null
                : new ScanSessionPayload
                {
                    Summary = entry.Scan.Summary,
                    CorrectValue = entry.Scan.CorrectValue,
                    MatchedResource = entry.Scan.MatchedResource,
                    IsTargetMatch = entry.Scan.IsTargetMatch,
                    SelectedTargets = [.. entry.Scan.SelectedTargets]
                },
            Mining = entry.Mining is null
                ? null
                : new MiningSessionPayload
                {
                    Summary = entry.Mining.Summary,
                    RockScan = entry.Mining.RockScan
                }
        };
    }

    private async Task ResumePendingLiveScanAsync(LiveScanResultItem resultItem)
    {
        if (string.IsNullOrWhiteSpace(resultItem.SourceImagePath) || !File.Exists(resultItem.SourceImagePath))
        {
            resultItem.ApplyProcessingFailure("Failed: source image not found for resumed scan queue item.");
            PersistLiveScanResultItem(resultItem);
            return;
        }

        resultItem.SetProcessingState("Queued for resume...", true);
        PersistLiveScanResultItem(resultItem);

        await _liveScanQueue.Writer.WriteAsync(
            new QueuedLiveScanCapture(
                resultItem.SourceImagePath,
                resultItem.DebugFolderPath,
                SelectedOcrMode,
                InferCaptureProfileFromPath(resultItem.SourceImagePath) ?? CaptureProfileCatalog.Signature,
                resultItem),
            _liveMiningQueueCancellation.Token);
    }

    private async Task ResumePendingLiveMiningAsync(OfflineMiningResultItem resultItem)
    {
        if (string.IsNullOrWhiteSpace(resultItem.SourceImagePath) || !File.Exists(resultItem.SourceImagePath))
        {
            resultItem.ApplyProcessingFailure("Failed: source image not found for resumed mining queue item.");
            PersistMiningSessionHistoryFromItem(resultItem);
            return;
        }

        resultItem.SetProcessingState("Queued for resume...", true);
        PersistMiningSessionHistoryFromItem(resultItem);

        await _liveMiningQueue.Writer.WriteAsync(
            new QueuedLiveMiningCapture(
                resultItem.SourceImagePath,
                resultItem.ExtractedScan.DebugFolderPath,
                SelectedOcrMode,
                resultItem.ExtractedScan,
                resultItem),
            _liveMiningQueueCancellation.Token);
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
        UpdateOverlaySettingsSummary();
        OnPropertyChanged(nameof(ActiveCaptureButtonLabel));
    }

    partial void OnOverlayOpacityChanged(double value)
    {
        _settings.OverlayOpacity = Math.Clamp(value, AppSettings.MinOverlayOpacity, AppSettings.MaxOverlayOpacity);
        _settingsService.Save(_settings);
        UpdateOverlaySettingsSummary();
    }

    partial void OnCompactModeScaleChanged(double value)
    {
        _settings.CompactModeScale = Math.Clamp(value, AppSettings.MinCompactModeScale, AppSettings.MaxCompactModeScale);
        _settingsService.Save(_settings);
        UpdateOverlaySettingsSummary();
    }

    partial void OnSelectedHotkeyModifiersChanged(uint value)
    {
        _settings.HotkeyModifiers = value;
        _settingsService.Save(_settings);
        UpdateConfiguredHotkeySummary();
        UpdateHotkeyTargetSummary();

        if (!IsRecordingHotkey)
        {
            HotkeyRecorderSummary = $"Shortcut changed to {HotkeyCatalog.Format(SelectedHotkeyModifiers, SelectedHotkeyVirtualKey)}.";
        }
    }

    partial void OnSelectedHotkeyVirtualKeyChanged(uint value)
    {
        _settings.HotkeyVirtualKey = value;
        _settingsService.Save(_settings);
        UpdateConfiguredHotkeySummary();
        UpdateHotkeyTargetSummary();

        if (!IsRecordingHotkey)
        {
            HotkeyRecorderSummary = $"Shortcut changed to {HotkeyCatalog.Format(SelectedHotkeyModifiers, SelectedHotkeyVirtualKey)}.";
        }
    }

    partial void OnIsRecordingHotkeyChanged(bool value)
    {
        HotkeyRecorderButtonLabel = value ? "Cancel Recording" : "Record Shortcut";
        if (value)
        {
            HotkeyRecorderSummary = "Recording shortcut now. Press a supported combination, or press Escape to cancel.";
        }
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
        OnPropertyChanged(nameof(ActiveRegionButtonLabel));
        OnPropertyChanged(nameof(ActiveCaptureButtonLabel));
        OnPropertyChanged(nameof(CompactWorkflowToggleLabel));
        OnPropertyChanged(nameof(CompactModeBadgeLabel));
        OnPropertyChanged(nameof(CompactModeSubtitle));
        OnPropertyChanged(nameof(LiveFooterSummary));
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
                    App.SessionId,
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
                persistCallback: (item, previousScan, updatedScan) => PersistMiningSessionHistoryFromItem(item, previousScan, updatedScan));

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
            var previousScan = SelectedOfflineMiningResult.ExtractedScan;
            var updatedScan = SelectedOfflineMiningResult.BuildUpdatedScan();
            var jsonPath = RockScanJsonStore.Save(updatedScan);
            var csvPath = RockScanCsvStore.Upsert(updatedScan);

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                SelectedOfflineMiningResult.MarkSaved(updatedScan, jsonPath, csvPath);
                OfflineMiningRockSummary = SelectedOfflineMiningResult.Summary;
                PersistMiningSessionHistoryFromItem(SelectedOfflineMiningResult, previousScan, updatedScan);
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
        var hotkeyDisplay = HotkeyCatalog.Format(SelectedHotkeyModifiers, SelectedHotkeyVirtualKey);
        HotkeyTargetSummary = SelectedWorkflowMode switch
        {
            WorkflowModeCatalog.Scanning when CaptureOnlyMode => $"{hotkeyDisplay} target: raw Scan capture",
            WorkflowModeCatalog.Scanning => $"{hotkeyDisplay} target: Scan capture",
            WorkflowModeCatalog.Mining when CaptureOnlyMode => $"{hotkeyDisplay} target: raw Mining capture",
            WorkflowModeCatalog.Mining => $"{hotkeyDisplay} target: Mining queue",
            WorkflowModeCatalog.Offline => $"{hotkeyDisplay} disabled in Offline mode",
            _ => $"{hotkeyDisplay} target: unknown"
        };
    }

    private void UpdateConfiguredHotkeySummary()
    {
        ConfiguredHotkeySummary = $"Configured hotkey: {HotkeyCatalog.Format(SelectedHotkeyModifiers, SelectedHotkeyVirtualKey)}";
        RecommendedHotkeySummary = $"Recommended for Star Citizen overlays: {HotkeyCatalog.RecommendedDisplay}";
    }

    private void UpdateOverlaySettingsSummary()
    {
        OverlaySettingsSummary = $"Capture only: {(CaptureOnlyMode ? "On" : "Off")}  |  Save debug: {(SaveDebugImages ? "On" : "Off")}  |  Transparency: {OverlayOpacity:P0}  |  Compact scale: {CompactModeScale:P0}";
    }

    private void ApplyRecommendedHotkey()
    {
        SelectedHotkeyModifiers = HotkeyCatalog.RecommendedModifiers;
        SelectedHotkeyVirtualKey = HotkeyCatalog.RecommendedVirtualKey;
        HotkeyRecorderSummary = $"Recommended shortcut applied: {HotkeyCatalog.RecommendedDisplay}.";
    }

    public void SetHotkeyRegistrationStatus(string message)
    {
        HotkeyRegistrationStatus = message;
    }

    public void ToggleHotkeyRecording()
    {
        if (IsRecordingHotkey)
        {
            CancelHotkeyRecording("Hotkey recording canceled.");
            return;
        }

        IsRecordingHotkey = true;
        Status = "Press the shortcut you want to record for BobTools.";
    }

    public void CompleteHotkeyRecording(uint modifiers, uint virtualKey)
    {
        SelectedHotkeyModifiers = modifiers;
        SelectedHotkeyVirtualKey = virtualKey;
        IsRecordingHotkey = false;

        var formattedHotkey = HotkeyCatalog.Format(modifiers, virtualKey);
        HotkeyRecorderSummary = $"Recorded shortcut: {formattedHotkey}.";
        Status = $"Hotkey updated to {formattedHotkey}.";
    }

    public void CancelHotkeyRecording(string message)
    {
        IsRecordingHotkey = false;
        HotkeyRecorderSummary = message;
        Status = message;
    }

    public void SetHotkeyRecorderSummary(string message)
    {
        HotkeyRecorderSummary = message;
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
            resultItem.SessionId,
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

        _globalResultsStore.Upsert(CreateGlobalRecordFromLiveScanResult(resultItem));
    }

    private void PersistScanSessionHistory(
        string sessionId,
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
        _sessionHistoryService.UpsertEntry(sessionId, new SessionHistoryEntry
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
        _sessionHistoryService.UpsertEntry(rockScan.SessionId, new SessionHistoryEntry
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

    private void PersistMiningSessionHistoryFromItem(OfflineMiningResultItem resultItem, RockScan? previousScan = null, RockScan? updatedScan = null)
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

        if (!string.Equals(resultItem.ExtractedScan.SourceType, "offline", StringComparison.OrdinalIgnoreCase))
        {
            _globalResultsStore.Upsert(CreateGlobalRecordFromLiveMiningResult(resultItem));
        }

        AdjustMiningStatsForUpdatedScan(previousScan, updatedScan);
    }

    private GlobalResultRecord CreateGlobalRecordFromLiveScanResult(LiveScanResultItem resultItem)
    {
        return new GlobalResultRecord
        {
            Id = resultItem.HistoryEntryId,
            SessionId = resultItem.SessionId,
            ReviewStatus = resultItem.ReviewStatus,
            ExportedAtUtc = resultItem.ExportedAtUtc,
            ExportBatchId = resultItem.ExportBatchId,
            Entry = new SessionHistoryEntry
            {
                Id = resultItem.HistoryEntryId,
                ResultKind = "scan",
                TimestampUtc = resultItem.TimestampUtc,
                WorkflowMode = WorkflowModeCatalog.Scanning,
                CaptureProfile = CaptureProfileCatalog.Signature,
                SourceType = "live",
                SourceImagePath = resultItem.SourceImagePath,
                ProcessedImagePath = resultItem.ProcessedImagePath,
                DebugFolderPath = resultItem.DebugFolderPath,
                OcrText = resultItem.OcrText,
                OcrMode = resultItem.UsedOcrModeSummary,
                OcrSourceSummary = resultItem.OcrSourceSummary,
                ProcessingState = resultItem.ProcessingState,
                Scan = new ScanSessionPayload
                {
                    Summary = resultItem.SignatureResourceSummary,
                    CorrectValue = resultItem.CorrectValue,
                    MatchedResource = resultItem.MatchedResourceName,
                    IsTargetMatch = resultItem.IsTargetMatch,
                    SelectedTargets = [.. resultItem.SelectedTargets]
                }
            }
        };
    }

    private GlobalResultRecord CreateGlobalRecordFromLiveMiningResult(OfflineMiningResultItem resultItem)
    {
        return new GlobalResultRecord
        {
            Id = resultItem.ExtractedScan.Id,
            SessionId = resultItem.ExtractedScan.SessionId,
            ReviewStatus = resultItem.ReviewStatus,
            ExportedAtUtc = resultItem.ExportedAtUtc,
            ExportBatchId = resultItem.ExportBatchId,
            Entry = new SessionHistoryEntry
            {
                Id = resultItem.ExtractedScan.Id,
                ResultKind = "mining",
                TimestampUtc = resultItem.ExtractedScan.TimestampUtc,
                WorkflowMode = WorkflowModeCatalog.Mining,
                CaptureProfile = resultItem.ExtractedScan.CaptureProfile,
                SourceType = resultItem.ExtractedScan.SourceType,
                SourceImagePath = resultItem.ExtractedScan.SourceImagePath,
                ProcessedImagePath = resultItem.ProcessedImagePath,
                DebugFolderPath = resultItem.ExtractedScan.DebugFolderPath,
                OcrText = resultItem.ExtractedScan.SourceText,
                OcrMode = resultItem.UsedOcrModeSummary,
                OcrSourceSummary = resultItem.OcrSourceSummary,
                ProcessingState = resultItem.ProcessingState,
                Mining = new MiningSessionPayload
                {
                    Summary = resultItem.Summary,
                    RockScan = resultItem.ExtractedScan
                }
            }
        };
    }

    private List<string> GetSelectedScanTargets()
    {
        return ScanTargetOptions
            .Where(option => option.IsSelected)
            .Select(option => option.Resource)
            .ToList();
    }

    private void ExportScanTrainingPackage()
    {
        var eligibleRecords = _globalResultsStore.LoadAll()
            .Where(record => string.Equals(record.Entry.ResultKind, "scan", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(record.Entry.SourceType, "offline", StringComparison.OrdinalIgnoreCase)
                && record.Entry.Scan is not null
                && ScanReviewCatalog.IsExportEligible(record.ReviewStatus, record.ExportedAtUtc))
            .OrderByDescending(record => record.Entry.TimestampUtc)
            .ToList();

        if (eligibleRecords.Count == 0)
        {
            Status = "No reviewed and unexported scan records are available to export.";
            return;
        }

        try
        {
            var exportedAtUtc = DateTime.UtcNow;
            var exportBatchId = $"scan_batch_{exportedAtUtc:yyyyMMdd_HHmmss}";
            var rows = eligibleRecords.Select(record => new ScanTrainingExportStore.ScanTrainingRow(
                exportBatchId,
                record.SessionId,
                record.Entry.Id,
                record.Entry.TimestampUtc,
                record.Entry.CaptureProfile,
                record.Entry.SourceImagePath,
                record.Entry.ProcessedImagePath,
                record.Entry.OcrText,
                record.Entry.Scan?.CorrectValue ?? record.Entry.OcrText,
                record.Entry.Scan?.Summary ?? string.Empty,
                record.Entry.Scan?.MatchedResource ?? string.Empty,
                record.Entry.Scan?.IsTargetMatch ?? false,
                record.Entry.OcrMode,
                record.Entry.OcrSourceSummary,
                record.Entry.ProcessingState,
                string.Join(" | ", record.Entry.Scan?.SelectedTargets ?? [])))
                .ToList();

            var zipPath = ScanTrainingExportStore.ExportBatch(exportBatchId, rows);
            _globalResultsStore.MarkExported(eligibleRecords.Select(record => record.Id), exportBatchId, exportedAtUtc);

            foreach (var item in LiveScanResults.Where(item => eligibleRecords.Any(record => string.Equals(record.Id, item.HistoryEntryId, StringComparison.OrdinalIgnoreCase))))
            {
                item.ApplyReviewMetadata(item.ReviewStatus, exportedAtUtc, exportBatchId);
            }

            Status = $"Exported {eligibleRecords.Count} reviewed scan record(s) to {zipPath}";
        }
        catch (Exception ex)
        {
            Status = $"Unable to export scan training package: {ex.Message}";
        }
    }

    private void ClearPersistedResultsData()
    {
        if (LiveScanResults.Any(item => item.IsProcessing) || LiveMiningResults.Any(item => item.IsProcessing))
        {
            Status = "Wait for active live scan and mining jobs to finish before clearing persisted data.";
            return;
        }

        var confirmation = System.Windows.MessageBox.Show(
            "Clear the saved live scan and mining data from the global BobTools store? This does not delete exported CSV, JSON, or session history files.",
            "Clear Persisted Live Data",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);

        if (confirmation != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        _globalResultsStore.Clear();
        LiveScanResults.Clear();
        LiveMiningResults.Clear();
        UpdateLiveScanQueueSummary();
        UpdateLiveMiningQueueSummary();
        Status = "Cleared the global live scan and mining store.";
    }

    private void PurgeRejectedScanRecords()
    {
        if (LiveScanResults.Any(item => item.IsProcessing))
        {
            Status = "Wait for active live scan jobs to finish before purging rejected scan records.";
            return;
        }

        var allRecords = _globalResultsStore.LoadAll();
        var rejectedRecords = allRecords
            .Where(record => string.Equals(record.Entry.ResultKind, "scan", StringComparison.OrdinalIgnoreCase)
                && string.Equals(ScanReviewCatalog.Normalize(record.ReviewStatus), ScanReviewCatalog.Rejected, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (rejectedRecords.Count == 0)
        {
            Status = "No rejected scan records are available to purge.";
            return;
        }

        var confirmation = System.Windows.MessageBox.Show(
            $"Purge {rejectedRecords.Count} rejected scan record(s) from the shared dataset and delete their unreferenced source, processed, and debug files from disk? This cannot be undone.",
            "Purge Rejected Scan Records",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);

        if (confirmation != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        var keptRecords = allRecords
            .Where(record => !rejectedRecords.Any(rejected => string.Equals(rejected.Id, record.Id, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var retainedFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var retainedFolderPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in keptRecords)
        {
            AddPathIfPresent(retainedFilePaths, record.Entry.SourceImagePath);
            AddPathIfPresent(retainedFilePaths, record.Entry.ProcessedImagePath);
            AddPathIfPresent(retainedFolderPaths, record.Entry.DebugFolderPath);
        }

        var deletedFileCount = 0;
        var deletedFolderCount = 0;

        foreach (var record in rejectedRecords)
        {
            if (TryDeleteFileIfUnreferenced(record.Entry.SourceImagePath, retainedFilePaths))
            {
                deletedFileCount++;
            }

            if (TryDeleteFileIfUnreferenced(record.Entry.ProcessedImagePath, retainedFilePaths))
            {
                deletedFileCount++;
            }

            if (TryDeleteDirectoryIfUnreferenced(record.Entry.DebugFolderPath, retainedFolderPaths, retainedFilePaths))
            {
                deletedFolderCount++;
            }
        }

        _globalResultsStore.ReplaceAll(keptRecords);

        var purgedIds = new HashSet<string>(rejectedRecords.Select(record => record.Id), StringComparer.OrdinalIgnoreCase);
        var liveItemsToRemove = LiveScanResults
            .Where(item => purgedIds.Contains(item.HistoryEntryId))
            .ToList();

        foreach (var item in liveItemsToRemove)
        {
            LiveScanResults.Remove(item);
        }

        UpdateLiveScanQueueSummary();
        UpdateScanTrainingDatasetSummary();
        RefreshLiveScanResultsPage();
        Status = $"Purged {rejectedRecords.Count} rejected scan record(s); deleted {deletedFileCount} file(s) and {deletedFolderCount} folder(s).";
    }

    private void PurgeRejectedMiningRecords()
    {
        if (LiveMiningResults.Any(item => item.IsProcessing))
        {
            Status = "Wait for active live mining jobs to finish before purging rejected mining records.";
            return;
        }

        var allRecords = _globalResultsStore.LoadAll();
        var rejectedRecords = allRecords
            .Where(record => string.Equals(record.Entry.ResultKind, "mining", StringComparison.OrdinalIgnoreCase)
                && string.Equals(ScanReviewCatalog.Normalize(record.ReviewStatus), ScanReviewCatalog.Rejected, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (rejectedRecords.Count == 0)
        {
            Status = "No rejected mining records are available to purge.";
            return;
        }

        var confirmation = System.Windows.MessageBox.Show(
            $"Purge {rejectedRecords.Count} rejected mining record(s) from the shared dataset and delete their unreferenced source, processed, and debug files from disk? This cannot be undone.",
            "Purge Rejected Mining Records",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);

        if (confirmation != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        var keptRecords = allRecords
            .Where(record => !rejectedRecords.Any(rejected => string.Equals(rejected.Id, record.Id, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var retainedFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var retainedFolderPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in keptRecords)
        {
            AddPathIfPresent(retainedFilePaths, record.Entry.SourceImagePath);
            AddPathIfPresent(retainedFilePaths, record.Entry.ProcessedImagePath);
            AddPathIfPresent(retainedFolderPaths, record.Entry.DebugFolderPath);
        }

        var deletedFileCount = 0;
        var deletedFolderCount = 0;

        foreach (var record in rejectedRecords)
        {
            if (TryDeleteFileIfUnreferenced(record.Entry.SourceImagePath, retainedFilePaths))
            {
                deletedFileCount++;
            }

            if (TryDeleteFileIfUnreferenced(record.Entry.ProcessedImagePath, retainedFilePaths))
            {
                deletedFileCount++;
            }

            if (TryDeleteDirectoryIfUnreferenced(record.Entry.DebugFolderPath, retainedFolderPaths, retainedFilePaths))
            {
                deletedFolderCount++;
            }
        }

        _globalResultsStore.ReplaceAll(keptRecords);

        var purgedIds = new HashSet<string>(rejectedRecords.Select(record => record.Id), StringComparer.OrdinalIgnoreCase);
        var liveItemsToRemove = LiveMiningResults
            .Where(item => purgedIds.Contains(item.ExtractedScan.Id))
            .ToList();

        foreach (var item in liveItemsToRemove)
        {
            LiveMiningResults.Remove(item);
        }

        UpdateLiveMiningQueueSummary();
        UpdateMiningReviewDatasetSummary();
        RefreshLiveMiningResultsPage();
        Status = $"Purged {rejectedRecords.Count} rejected mining record(s); deleted {deletedFileCount} file(s) and {deletedFolderCount} folder(s).";
    }

    private void RefreshLiveScanResultsPage(bool resetPage = false)
    {
        _liveScanResultsView.Refresh();
        var filteredItems = _liveScanResultsView.Cast<LiveScanResultItem>().ToList();
        var totalPages = Math.Max(1, (int)Math.Ceiling(filteredItems.Count / (double)LiveResultsPageSize));
        var currentPage = resetPage ? 1 : Math.Min(Math.Max(1, LiveScanCurrentPage), totalPages);
        var pageItems = filteredItems
            .Skip((currentPage - 1) * LiveResultsPageSize)
            .Take(LiveResultsPageSize)
            .ToList();

        ReplaceVisibleResults(VisibleLiveScanResults, pageItems);
        UpdateLiveScanPreviewCache(pageItems);

        LiveScanCurrentPage = currentPage;
        LiveScanTotalPages = totalPages;
        LiveScanPageSummary = filteredItems.Count == 0
            ? "Showing 0 of 0"
            : $"Showing {((currentPage - 1) * LiveResultsPageSize) + 1}-{((currentPage - 1) * LiveResultsPageSize) + pageItems.Count} of {filteredItems.Count}";
        NotifyLiveScanPagingChanged();
    }

    private void RefreshLiveMiningResultsPage(bool resetPage = false)
    {
        _liveMiningResultsView.Refresh();
        var filteredItems = _liveMiningResultsView.Cast<OfflineMiningResultItem>().ToList();
        var totalPages = Math.Max(1, (int)Math.Ceiling(filteredItems.Count / (double)LiveResultsPageSize));
        var currentPage = resetPage ? 1 : Math.Min(Math.Max(1, LiveMiningCurrentPage), totalPages);
        var pageItems = filteredItems
            .Skip((currentPage - 1) * LiveResultsPageSize)
            .Take(LiveResultsPageSize)
            .ToList();

        ReplaceVisibleResults(VisibleLiveMiningResults, pageItems);
        UpdateLiveMiningPreviewCache(pageItems);

        LiveMiningCurrentPage = currentPage;
        LiveMiningTotalPages = totalPages;
        LiveMiningPageSummary = filteredItems.Count == 0
            ? "Showing 0 of 0"
            : $"Showing {((currentPage - 1) * LiveResultsPageSize) + 1}-{((currentPage - 1) * LiveResultsPageSize) + pageItems.Count} of {filteredItems.Count}";
        NotifyLiveMiningPagingChanged();
    }

    private static void ReplaceVisibleResults<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    private void UpdateLiveScanPreviewCache(IReadOnlyCollection<LiveScanResultItem> visibleItems)
    {
        var visibleSet = new HashSet<LiveScanResultItem>(visibleItems);
        foreach (var item in LiveScanResults)
        {
            if (visibleSet.Contains(item))
            {
                item.LoadPreviewImages();
            }
            else
            {
                item.ReleasePreviewImages();
            }
        }
    }

    private void UpdateLiveMiningPreviewCache(IReadOnlyCollection<OfflineMiningResultItem> visibleItems)
    {
        var visibleSet = new HashSet<OfflineMiningResultItem>(visibleItems);
        foreach (var item in LiveMiningResults)
        {
            if (visibleSet.Contains(item))
            {
                item.LoadPreviewImages();
            }
            else
            {
                item.ReleasePreviewImages();
            }
        }
    }

    private void NotifyLiveScanPagingChanged()
    {
        OnPropertyChanged(nameof(CanGoToPreviousLiveScanPage));
        OnPropertyChanged(nameof(CanGoToNextLiveScanPage));
        if (PreviousLiveScanPageCommand is not null)
        {
            PreviousLiveScanPageCommand.NotifyCanExecuteChanged();
        }

        if (NextLiveScanPageCommand is not null)
        {
            NextLiveScanPageCommand.NotifyCanExecuteChanged();
        }
    }

    private void NotifyLiveMiningPagingChanged()
    {
        OnPropertyChanged(nameof(CanGoToPreviousLiveMiningPage));
        OnPropertyChanged(nameof(CanGoToNextLiveMiningPage));
        if (PreviousLiveMiningPageCommand is not null)
        {
            PreviousLiveMiningPageCommand.NotifyCanExecuteChanged();
        }

        if (NextLiveMiningPageCommand is not null)
        {
            NextLiveMiningPageCommand.NotifyCanExecuteChanged();
        }
    }

    private static void AddPathIfPresent(ISet<string> paths, string? path)
    {
        var normalizedPath = NormalizePath(path);
        if (normalizedPath is not null)
        {
            paths.Add(normalizedPath);
        }
    }

    private static bool TryDeleteFileIfUnreferenced(string? path, ISet<string> retainedFilePaths)
    {
        var normalizedPath = NormalizePath(path);
        if (normalizedPath is null || retainedFilePaths.Contains(normalizedPath) || !File.Exists(normalizedPath))
        {
            return false;
        }

        File.Delete(normalizedPath);
        return true;
    }

    private static bool TryDeleteDirectoryIfUnreferenced(string? path, ISet<string> retainedFolderPaths, ISet<string> retainedFilePaths)
    {
        var normalizedPath = NormalizePath(path);
        if (normalizedPath is null || retainedFolderPaths.Contains(normalizedPath) || !Directory.Exists(normalizedPath))
        {
            return false;
        }

        foreach (var retainedFilePath in retainedFilePaths)
        {
            if (IsPathInsideDirectory(retainedFilePath, normalizedPath))
            {
                return false;
            }
        }

        Directory.Delete(normalizedPath, recursive: true);
        return true;
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPathInsideDirectory(string path, string directoryPath)
    {
        var normalizedDirectory = directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return path.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
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

    private static bool ShouldResumeProcessing(string? processingState)
    {
        if (string.IsNullOrWhiteSpace(processingState))
        {
            return false;
        }

        return string.Equals(processingState, "Queued", StringComparison.OrdinalIgnoreCase)
            || string.Equals(processingState, "Queued for resume...", StringComparison.OrdinalIgnoreCase)
            || string.Equals(processingState, "Processing OCR...", StringComparison.OrdinalIgnoreCase);
    }

    private RockScan BuildOfflineMiningScan(string normalizedText, string sourceImagePath, string? debugOutputDirectory)
    {
        var baseScan = CreateMiningScanBase("offline", CaptureProfileCatalog.Mining, sourceImagePath, debugOutputDirectory);

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
                    PersistLiveScanResultItem(queuedCapture.ResultItem);
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
                    PersistMiningSessionHistoryFromItem(queuedCapture.ResultItem);
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
                    PersistMiningSessionHistoryFromItem(queuedCapture.ResultItem);
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
            RsiHandle = baseScan.RsiHandle,
            Ship = baseScan.Ship,
            StarSystem = baseScan.StarSystem,
            LocationKey = baseScan.LocationKey,
            LocationName = baseScan.LocationName,
            LocationPath = baseScan.LocationPath,
            Location = baseScan.Location,
            SourceType = baseScan.SourceType,
            CaptureProfile = baseScan.CaptureProfile,
            SourceImagePath = baseScan.SourceImagePath,
            DebugFolderPath = baseScan.DebugFolderPath,
            SourceText = normalizedText
        };
    }

    private RockScan CreateMiningScanBase(string sourceType, string captureProfile, string? sourceImagePath, string? debugOutputDirectory)
    {
        var sessionMetadata = BuildCurrentSessionMetadata();
        return new RockScan
        {
            SessionId = App.SessionId,
            TimestampUtc = DateTime.UtcNow,
            RsiHandle = sessionMetadata.RsiHandle,
            Ship = sessionMetadata.Ship,
            StarSystem = sessionMetadata.StarSystem,
            LocationKey = sessionMetadata.LocationKey,
            LocationName = sessionMetadata.LocationName,
            LocationPath = sessionMetadata.LocationPath,
            Location = ResolveSessionLocation(sessionMetadata),
            SourceType = sourceType,
            CaptureProfile = captureProfile,
            SourceImagePath = sourceImagePath,
            DebugFolderPath = debugOutputDirectory
        };
    }

    private static RockScan MergeRockScanWithSessionMetadata(RockScan rockScan, SessionMetadata? sessionMetadata, bool preferSessionLocation)
    {
        if (sessionMetadata is null)
        {
            return rockScan;
        }

        var resolvedLocation = ResolveSessionLocation(sessionMetadata);
        return rockScan with
        {
            RsiHandle = string.IsNullOrWhiteSpace(rockScan.RsiHandle) ? sessionMetadata.RsiHandle : rockScan.RsiHandle,
            Ship = string.IsNullOrWhiteSpace(rockScan.Ship) ? sessionMetadata.Ship : rockScan.Ship,
            StarSystem = string.IsNullOrWhiteSpace(rockScan.StarSystem) ? sessionMetadata.StarSystem : rockScan.StarSystem,
            LocationKey = string.IsNullOrWhiteSpace(rockScan.LocationKey) ? sessionMetadata.LocationKey : rockScan.LocationKey,
            LocationName = string.IsNullOrWhiteSpace(rockScan.LocationName) ? sessionMetadata.LocationName : rockScan.LocationName,
            LocationPath = string.IsNullOrWhiteSpace(rockScan.LocationPath) ? sessionMetadata.LocationPath : rockScan.LocationPath,
            Location = preferSessionLocation && !string.IsNullOrWhiteSpace(resolvedLocation)
                ? resolvedLocation
                : rockScan.Location
        };
    }

    private static bool RockScanHasSessionMetadata(RockScan? rockScan)
    {
        return rockScan is not null
            && (!string.IsNullOrWhiteSpace(rockScan.RsiHandle)
                || !string.IsNullOrWhiteSpace(rockScan.Ship)
                || !string.IsNullOrWhiteSpace(rockScan.StarSystem)
                || !string.IsNullOrWhiteSpace(rockScan.LocationKey)
                || !string.IsNullOrWhiteSpace(rockScan.LocationName)
                || !string.IsNullOrWhiteSpace(rockScan.LocationPath));
    }

    private static string ResolveSessionLocation(SessionMetadata sessionMetadata)
    {
        if (!string.IsNullOrWhiteSpace(sessionMetadata.LocationPath))
        {
            return sessionMetadata.LocationPath;
        }

        if (!string.IsNullOrWhiteSpace(sessionMetadata.LocationName))
        {
            return sessionMetadata.LocationName;
        }

        return string.Empty;
    }

    private void UpdateLiveMiningQueueSummary()
    {
        var processingCount = LiveMiningResults.Count(item => item.IsProcessing);
        LiveMiningQueueSummary = LiveMiningResults.Count == 0
            ? "No live mining captures yet."
            : $"Results: {LiveMiningResults.Count} | Active jobs: {processingCount} | Current session: {CurrentSessionId}";
        OnPropertyChanged(nameof(LiveFooterSummary));
    }

    private void UpdateLiveScanQueueSummary()
    {
        var processingCount = LiveScanResults.Count(item => item.IsProcessing);
        LiveScanQueueSummary = LiveScanResults.Count == 0
            ? "No live scan captures yet."
            : $"Results: {LiveScanResults.Count} | Active jobs: {processingCount} | Current session: {CurrentSessionId}";
        OnPropertyChanged(nameof(LiveFooterSummary));
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
        var tierCounts = CountQualityTiers(rockScan);

        LifetimeRockCount++;
        LifetimeCommonOreCount += tierCounts.CommonCount;
        LifetimeRareOreCount += tierCounts.RareCount;
        LifetimeLegendaryOreCount += tierCounts.LegendaryCount;
        LifetimeMythicOreCount += tierCounts.MythicCount;
        _settings.LifetimeRockCount = LifetimeRockCount;
        _settings.LifetimeCommonOreCount = LifetimeCommonOreCount;
        _settings.LifetimeRareOreCount = LifetimeRareOreCount;
        _settings.LifetimeLegendaryOreCount = LifetimeLegendaryOreCount;
        _settings.LifetimeMythicOreCount = LifetimeMythicOreCount;
        _settingsService.Save(_settings);
    }

    private void AdjustMiningStatsForUpdatedScan(RockScan? previousScan, RockScan? updatedScan)
    {
        var delta = OreQualityTierCatalog.CalculateLifetimeDelta(previousScan, updatedScan);
        if (delta == default)
        {
            return;
        }

        LifetimeRockCount = Math.Max(0, LifetimeRockCount + delta.RockDelta);
        LifetimeCommonOreCount = Math.Max(0, LifetimeCommonOreCount + delta.CommonDelta);
        LifetimeRareOreCount = Math.Max(0, LifetimeRareOreCount + delta.RareDelta);
        LifetimeLegendaryOreCount = Math.Max(0, LifetimeLegendaryOreCount + delta.LegendaryDelta);
        LifetimeMythicOreCount = Math.Max(0, LifetimeMythicOreCount + delta.MythicDelta);
        _settings.LifetimeRockCount = LifetimeRockCount;
        _settings.LifetimeCommonOreCount = LifetimeCommonOreCount;
        _settings.LifetimeRareOreCount = LifetimeRareOreCount;
        _settings.LifetimeLegendaryOreCount = LifetimeLegendaryOreCount;
        _settings.LifetimeMythicOreCount = LifetimeMythicOreCount;
        _settingsService.Save(_settings);
    }

    private static OreQualityBreakdown CountQualityTiers(RockScan rockScan)
    {
        return OreQualityTierCatalog.CountTiers(rockScan.Elements);
    }

    private (string Summary, string? CsvPath, string? JsonPath, RockScan? CompletedScan) AppendMiningRockScanIfApplicable(string captureProfile, string? normalizedText, string sourceType, string? sourceImagePath, string? debugOutputDirectory)
    {
        if (!string.Equals(captureProfile, CaptureProfileCatalog.Mining, StringComparison.OrdinalIgnoreCase))
        {
            return ("Rock scan: n/a for Scan", null, null, null);
        }

        if (!RockScanParser.TryParse(normalizedText, out var rockScan))
        {
            return ("Rock scan: unable to parse", null, null, null);
        }

        var completedScan = BuildMiningScan(normalizedText ?? string.Empty, CreateMiningScanBase(sourceType, captureProfile, sourceImagePath, debugOutputDirectory));

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
