using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using BobTools.Utils;

namespace BobTools.Models;

public sealed partial class LiveScanResultItem : ObservableObject
{
    private string _lastPersistedCorrectValue = string.Empty;
    private int _autoSaveVersion;
    private readonly Action<LiveScanResultItem>? _persistCallback;
    private readonly Action<string>? _statusCallback;
    private bool _suppressPersistence;

    [ObservableProperty] private ImageSource? sourcePreviewImage;
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
    [ObservableProperty] private double imageZoom = 1d;
    [ObservableProperty] private string reviewStatus = ScanReviewCatalog.Pending;
    [ObservableProperty] private DateTime? exportedAtUtc;
    [ObservableProperty] private string exportBatchId = string.Empty;

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
        this.processingState = processingState;
        this.isProcessing = isProcessing;
    }

    public string HistoryEntryId { get; }

    public string SessionId { get; }

    public DateTime TimestampUtc { get; }

    public string DisplayName { get; }

    public string SourceImagePath { get; }

    public string? DebugFolderPath { get; }

    public ImageSource? DisplayPreviewImage => SourcePreviewImage ?? ProcessedPreviewImage;

    public string ImageZoomLabel => $"Zoom {Math.Round(ImageZoom * 100):0}%";
    public IReadOnlyList<string> ReviewStatusOptions => ScanReviewCatalog.Options;

    public string SingleLineOcrText => NormalizeSingleLine(OcrText);

    public string SingleLineCorrectValue => NormalizeSingleLine(CorrectValue);

    public string ReviewStateSummary => ExportedAtUtc is not null
        ? $"Exported {ExportedAtUtc:yyyy-MM-dd HH:mm}"
        : string.Equals(ReviewStatus, ScanReviewCatalog.Reviewed, StringComparison.Ordinal)
            ? "Ready for export"
            : string.Equals(ReviewStatus, ScanReviewCatalog.Rejected, StringComparison.Ordinal)
                ? "Rejected from export"
                : "Pending review";

    public string CompactPrimaryValue => !string.IsNullOrWhiteSpace(CorrectValue)
        ? SingleLineCorrectValue
        : !string.IsNullOrWhiteSpace(OcrText)
            ? SingleLineOcrText
            : "Waiting for scan result";

    public string CompactStatusSummary => IsTargetMatch && !string.IsNullOrWhiteSpace(TargetAlertSummary)
        ? TargetAlertSummary
        : ProcessingState;

    public string CompactListSignature => !string.IsNullOrWhiteSpace(CorrectValue)
        ? SingleLineCorrectValue
        : !string.IsNullOrWhiteSpace(OcrText)
            ? SingleLineOcrText
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

    public void LoadPreviewImages()
    {
        if (SourcePreviewImage is null)
        {
            SourcePreviewImage = LoadImage(SourceImagePath);
        }

        if (ProcessedPreviewImage is null && !string.IsNullOrWhiteSpace(ProcessedImagePath))
        {
            ProcessedPreviewImage = LoadImage(ProcessedImagePath);
        }

        OnPropertyChanged(nameof(DisplayPreviewImage));
    }

    public void ReleasePreviewImages()
    {
        if (SourcePreviewImage is null && ProcessedPreviewImage is null)
        {
            return;
        }

        SourcePreviewImage = null;
        ProcessedPreviewImage = null;
        OnPropertyChanged(nameof(DisplayPreviewImage));
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
        ProcessedPreviewImage = null;
        ProcessingState = processingState;
        IsProcessing = false;
        _lastPersistedCorrectValue = CorrectValue ?? string.Empty;
        AutoSaveStatus = string.IsNullOrWhiteSpace(CorrectValue) ? string.Empty : "Saved";
        _suppressPersistence = false;

        if (SourcePreviewImage is not null || ProcessedPreviewImage is not null)
        {
            LoadPreviewImages();
        }

        OnPropertyChanged(nameof(DisplayPreviewImage));
        OnCompactDisplayChanged();
    }

    public void ApplyProcessingFailure(string processingState)
    {
        ProcessingState = processingState;
        IsProcessing = false;
        OnCompactDisplayChanged();
    }

    public void ApplyReviewMetadata(string? reviewStatusValue, DateTime? exportedAtUtcValue, string? exportBatchIdValue)
    {
        _suppressPersistence = true;
        ReviewStatus = ScanReviewCatalog.Normalize(reviewStatusValue);
        ExportedAtUtc = exportedAtUtcValue;
        ExportBatchId = exportBatchIdValue ?? string.Empty;
        _suppressPersistence = false;
        OnPropertyChanged(nameof(ReviewStateSummary));
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

    partial void OnImageZoomChanged(double value)
    {
        OnPropertyChanged(nameof(ImageZoomLabel));
    }

    partial void OnProcessedImagePathChanged(string? value)
    {
        ProcessedPreviewImage = null;
        OnPropertyChanged(nameof(DisplayPreviewImage));
    }

    partial void OnReviewStatusChanged(string value)
    {
        var normalizedValue = ScanReviewCatalog.Normalize(value);
        if (!string.Equals(value, normalizedValue, StringComparison.Ordinal))
        {
            ReviewStatus = normalizedValue;
            return;
        }

        if (!_suppressPersistence)
        {
            ClearExportMetadata();
            _ = AutoSaveAsync($"Auto-saved review state for {DisplayName}.");
        }

        OnPropertyChanged(nameof(ReviewStateSummary));
    }

    partial void OnExportedAtUtcChanged(DateTime? value)
    {
        OnPropertyChanged(nameof(ReviewStateSummary));
    }

    partial void OnExportBatchIdChanged(string value)
    {
        OnPropertyChanged(nameof(ReviewStateSummary));
    }

    partial void OnOcrTextChanged(string value)
    {
        OnPropertyChanged(nameof(SingleLineOcrText));
        OnCompactDisplayChanged();
    }

    async partial void OnCorrectValueChanged(string value)
    {
        var singleLineValue = NormalizeSingleLine(value);
        if (!string.Equals(value, singleLineValue, StringComparison.Ordinal))
        {
            CorrectValue = singleLineValue;
            return;
        }

        OnPropertyChanged(nameof(SingleLineCorrectValue));
        if (!_suppressPersistence && !string.Equals(_lastPersistedCorrectValue, value ?? string.Empty, StringComparison.Ordinal))
        {
            MarkPendingReview();
        }

        RecomputeCorrectedResultState(value);
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

        await AutoSaveAsync($"Auto-saved corrected value for {DisplayName}.");
        _lastPersistedCorrectValue = value ?? string.Empty;
    }

    private void RecomputeCorrectedResultState(string? value)
    {
        var normalizedValue = value?.Trim() ?? string.Empty;
        if (IsInvalidScanValue(normalizedValue))
        {
            SignatureResourceSummary = "Scan resource: Invalid Scan";
            MatchedResourceName = null;
            IsTargetMatch = false;
            return;
        }

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            SignatureResourceSummary = "Scan resource: none yet";
            MatchedResourceName = null;
            IsTargetMatch = false;
            return;
        }

        var matchedResource = SignatureCatalog.FindMatchedResource(normalizedValue);
        SignatureResourceSummary = SignatureCatalog.BuildResourceSummary(normalizedValue)
            .Replace("Signature resource:", "Scan resource:", StringComparison.Ordinal);
        MatchedResourceName = matchedResource;
        IsTargetMatch = !string.IsNullOrWhiteSpace(matchedResource)
            && SelectedTargets.Any(target => string.Equals(target, matchedResource, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsInvalidScanValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var digitsOnly = new string(value.Where(char.IsDigit).ToArray());
        return digitsOnly.Length > 0 && digitsOnly.All(digit => digit == '0');
    }

    private static string NormalizeSingleLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value ?? string.Empty;
        }

        return string.Join(" ", value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private void MarkPendingReview()
    {
        _suppressPersistence = true;
        ReviewStatus = ScanReviewCatalog.Pending;
        ExportedAtUtc = null;
        ExportBatchId = string.Empty;
        _suppressPersistence = false;
        OnPropertyChanged(nameof(ReviewStateSummary));
    }

    private void ClearExportMetadata()
    {
        _suppressPersistence = true;
        ExportedAtUtc = null;
        ExportBatchId = string.Empty;
        _suppressPersistence = false;
        OnPropertyChanged(nameof(ReviewStateSummary));
    }

    private async Task AutoSaveAsync(string successMessage)
    {
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
            AutoSaveStatus = "Saved";
            _statusCallback?.Invoke(successMessage);
        }
        catch (Exception ex)
        {
            AutoSaveStatus = "Save failed";
            _statusCallback?.Invoke($"Failed to auto-save scan data for {DisplayName}: {ex.Message}");
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