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

    public string CompactPrimaryValue => !string.IsNullOrWhiteSpace(CorrectValue)
        ? CorrectValue
        : !string.IsNullOrWhiteSpace(OcrText)
            ? OcrText
            : "Waiting for scan result";

    public string CompactStatusSummary => IsTargetMatch && !string.IsNullOrWhiteSpace(TargetAlertSummary)
        ? TargetAlertSummary
        : ProcessingState;

    public string CompactListSignature => !string.IsNullOrWhiteSpace(CorrectValue)
        ? CorrectValue
        : !string.IsNullOrWhiteSpace(OcrText)
            ? OcrText
            : "pending";

    public string CompactListResource => !string.IsNullOrWhiteSpace(MatchedResourceName)
        ? MatchedResourceName
        : "resource pending";

    public bool CompactListResourceIsHighlighted => IsTargetMatch;

    public bool CompactListIsValid => !CompactListSignature.Contains('\n') && !CompactListSignature.Contains('\r');

    public string CompactMetaSummary
    {
        get
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(SignatureResourceSummary))
            {
                parts.Add(SignatureResourceSummary.Replace("Signature resource: ", string.Empty, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(UsedOcrModeSummary))
            {
                parts.Add($"OCR {UsedOcrModeSummary}");
            }

            if (!string.IsNullOrWhiteSpace(OcrSourceSummary))
            {
                parts.Add(OcrSourceSummary);
            }

            return parts.Count == 0 ? "Awaiting OCR details" : string.Join("  •  ", parts);
        }
    }

    public void SetProcessingState(string state, bool value)
    {
        ProcessingState = state;
        IsProcessing = value;
        OnCompactDisplayChanged();
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
        OnCompactDisplayChanged();
    }

    public void ApplyProcessingFailure(string processingState)
    {
        ProcessingState = processingState;
        IsProcessing = false;
        OnCompactDisplayChanged();
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
        OnCompactDisplayChanged();
    }

    partial void OnIsTargetMatchChanged(bool value)
    {
        OnPropertyChanged(nameof(TargetAlertSummary));
        OnCompactDisplayChanged();
    }

    async partial void OnCorrectValueChanged(string value)
    {
        OnCompactDisplayChanged();

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
    private void OnCompactDisplayChanged()
    {
        OnPropertyChanged(nameof(CompactPrimaryValue));
        OnPropertyChanged(nameof(CompactStatusSummary));
        OnPropertyChanged(nameof(CompactMetaSummary));
        OnPropertyChanged(nameof(CompactListSignature));
        OnPropertyChanged(nameof(CompactListResource));
        OnPropertyChanged(nameof(CompactListResourceIsHighlighted));
        OnPropertyChanged(nameof(CompactListIsValid));
    }
}