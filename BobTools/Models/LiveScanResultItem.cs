using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BobTools.Models;

public sealed partial class LiveScanResultItem : ObservableObject
{
    private string _lastPersistedCorrectValue = string.Empty;
    private int _autoSaveVersion;
    private readonly Action<LiveScanResultItem>? _persistCallback;
    private readonly Action<string>? _statusCallback;
    private bool _suppressPersistence;

    public LiveScanResultItem(
        string historyEntryId,
        string sessionId,
        DateTime timestampUtc,
        string sourceImagePath,
        string? debugFolderPath,
        string? processedImagePath,
        string processingState = "Queued",
        bool isProcessing = false,
        Action<LiveScanResultItem>? persistCallback = null,
        Action<string>? statusCallback = null)
    {
        HistoryEntryId = historyEntryId;
        SessionId = sessionId;
        TimestampUtc = timestampUtc;
        SourceImagePath = sourceImagePath;
        DebugFolderPath = debugFolderPath;
        DisplayName = Path.GetFileName(sourceImagePath);
        _persistCallback = persistCallback;
        _statusCallback = statusCallback;
        this.processedImagePath = processedImagePath;
        this.processedPreviewImage = LoadImage(processedImagePath);
        this.processingState = processingState;
        this.isProcessing = isProcessing;
    }

    public string HistoryEntryId { get; }

    public string SessionId { get; }

    public DateTime TimestampUtc { get; }

    public string DisplayName { get; }

    public string SourceImagePath { get; }

    public string? DebugFolderPath { get; }

    [ObservableProperty] private string? processedImagePath;
    [ObservableProperty] private ImageSource? processedPreviewImage;
    [ObservableProperty] private string processingState = string.Empty;
    [ObservableProperty] private bool isProcessing;
    [ObservableProperty] private string ocrText = string.Empty;
    [ObservableProperty] private string correctValue = string.Empty;
    [ObservableProperty] private string signatureResourceSummary = "Scan resource: none yet";
    [ObservableProperty] private string usedOcrModeSummary = string.Empty;
    [ObservableProperty] private string ocrSourceSummary = string.Empty;
    [ObservableProperty] private string? matchedResourceName;
    [ObservableProperty] private bool isTargetMatch;
    [ObservableProperty] private List<string> selectedTargets = [];
    [ObservableProperty] private string autoSaveStatus = string.Empty;

    public void SetProcessingState(string state, bool value)
    {
        ProcessingState = state;
        IsProcessing = value;
    }

    public void ApplyProcessedResult(
        string extractedText,
        string signatureSummary,
        string usedModeSummary,
        string sourceSummary,
        string? matchedResource,
        bool isMatchingTarget,
        string? processedImageFilePath,
        string processingState,
        IEnumerable<string>? selectedTargets = null,
        string? correctValue = null)
    {
        _suppressPersistence = true;
        OcrText = extractedText;
        CorrectValue = correctValue ?? extractedText;
        SignatureResourceSummary = signatureSummary;
        UsedOcrModeSummary = usedModeSummary;
        OcrSourceSummary = sourceSummary;
        MatchedResourceName = matchedResource;
        IsTargetMatch = isMatchingTarget;
        SelectedTargets = selectedTargets?.Where(target => !string.IsNullOrWhiteSpace(target)).ToList() ?? [];
        ProcessedImagePath = processedImageFilePath;
        ProcessedPreviewImage = LoadImage(processedImageFilePath);
        ProcessingState = processingState;
        IsProcessing = false;
        _lastPersistedCorrectValue = CorrectValue ?? string.Empty;
        AutoSaveStatus = string.IsNullOrWhiteSpace(CorrectValue) ? string.Empty : "Saved";
        _suppressPersistence = false;
    }

    public void ApplyProcessingFailure(string processingState)
    {
        ProcessingState = processingState;
        IsProcessing = false;
    }

    public void ApplyTargetMatchState(bool isMatchingTarget)
    {
        IsTargetMatch = isMatchingTarget;
    }

    public string TargetAlertSummary => IsTargetMatch && !string.IsNullOrWhiteSpace(MatchedResourceName)
        ? $"Target match: {MatchedResourceName}"
        : string.Empty;

    partial void OnMatchedResourceNameChanged(string? value)
    {
        OnPropertyChanged(nameof(TargetAlertSummary));
    }

    partial void OnIsTargetMatchChanged(bool value)
    {
        OnPropertyChanged(nameof(TargetAlertSummary));
    }

    async partial void OnCorrectValueChanged(string value)
    {
        if (_suppressPersistence)
        {
            return;
        }

        if (IsProcessing)
        {
            return;
        }

        if (string.Equals(_lastPersistedCorrectValue, value ?? string.Empty, StringComparison.Ordinal))
        {
            AutoSaveStatus = string.IsNullOrWhiteSpace(value) ? string.Empty : "Saved";
            return;
        }

        var saveVersion = Interlocked.Increment(ref _autoSaveVersion);
        AutoSaveStatus = "Saving...";

        try
        {
            await Task.Delay(300);

            if (saveVersion != _autoSaveVersion)
            {
                return;
            }

            _persistCallback?.Invoke(this);
            _lastPersistedCorrectValue = value ?? string.Empty;
            AutoSaveStatus = "Saved";
            _statusCallback?.Invoke($"Auto-saved corrected value for {DisplayName}.");
        }
        catch (Exception ex)
        {
            AutoSaveStatus = "Save failed";
            _statusCallback?.Invoke($"Failed to auto-save corrected value for {DisplayName}: {ex.Message}");
        }
    }

    private static ImageSource? LoadImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}