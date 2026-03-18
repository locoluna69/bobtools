using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BobTools.Models;

public sealed partial class LiveScanResultItem : ObservableObject
{
    public LiveScanResultItem(string sourceImagePath, string? processedImagePath, string processingState = "Queued", bool isProcessing = false)
    {
        SourceImagePath = sourceImagePath;
        DisplayName = Path.GetFileName(sourceImagePath);
        this.processedImagePath = processedImagePath;
        this.processedPreviewImage = LoadImage(processedImagePath);
        this.processingState = processingState;
        this.isProcessing = isProcessing;
    }

    public string HistoryEntryId { get; } = Guid.NewGuid().ToString();

    public string DisplayName { get; }

    public string SourceImagePath { get; }

    [ObservableProperty] private string? processedImagePath;
    [ObservableProperty] private ImageSource? processedPreviewImage;
    [ObservableProperty] private string processingState = string.Empty;
    [ObservableProperty] private bool isProcessing;
    [ObservableProperty] private string ocrText = string.Empty;
    [ObservableProperty] private string signatureResourceSummary = "Scan resource: none yet";
    [ObservableProperty] private string usedOcrModeSummary = string.Empty;
    [ObservableProperty] private string ocrSourceSummary = string.Empty;
    [ObservableProperty] private string? matchedResourceName;
    [ObservableProperty] private bool isTargetMatch;

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
        string processingState)
    {
        OcrText = extractedText;
        SignatureResourceSummary = signatureSummary;
        UsedOcrModeSummary = usedModeSummary;
        OcrSourceSummary = sourceSummary;
        MatchedResourceName = matchedResource;
        IsTargetMatch = isMatchingTarget;
        ProcessedImagePath = processedImageFilePath;
        ProcessedPreviewImage = LoadImage(processedImageFilePath);
        ProcessingState = processingState;
        IsProcessing = false;
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