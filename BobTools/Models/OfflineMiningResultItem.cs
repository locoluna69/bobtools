using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BobTools.Utils;

namespace BobTools.Models;

public sealed partial class OfflineMiningResultItem : ObservableObject
{
    private string _savedSnapshot = string.Empty;
    private int _autoSaveVersion;
    private readonly Action<string>? _statusCallback;
    private readonly Action<OfflineMiningResultItem>? _persistCallback;
    private readonly bool _enableAutoSave;
    private bool _suppressDirtyTracking;
    private bool _suppressAutoSave;

    public OfflineMiningResultItem(
        string sourceImagePath,
        string? processedImagePath,
        RockScan extractedScan,
        string summary,
        string? jsonPath,
        string? csvPath,
        Action<string>? statusCallback = null,
        Action<OfflineMiningResultItem>? persistCallback = null,
        string processingState = "Ready",
        bool isProcessing = false,
        bool enableAutoSave = false)
    {
        SourceImagePath = sourceImagePath;
        DisplayName = Path.GetFileName(sourceImagePath);
        SourcePreviewImage = LoadImage(sourceImagePath);
        _statusCallback = statusCallback;
        _persistCallback = persistCallback;
        _enableAutoSave = enableAutoSave;
        ExtractedScan = extractedScan;
        this.processedImagePath = processedImagePath;
        this.processedPreviewImage = LoadImage(processedImagePath);
        LoadEditableValues(extractedScan);
        this.summary = summary;
        this.jsonPath = jsonPath;
        this.csvPath = csvPath;
        this.processingState = processingState;
        this.isProcessing = isProcessing;
        SaveCommand = new AsyncRelayCommand(SaveAsync, CanSave);
        AddRowCommand = new RelayCommand(AddRow, CanAddRow);
        DeleteRowCommand = new RelayCommand<OfflineMiningElementEditRow>(DeleteRow);
        _savedSnapshot = BuildSnapshot();
    }

    public string DisplayName { get; }

    public string SourceImagePath { get; }

    public ImageSource? SourcePreviewImage { get; }

    public ImageSource? DisplayPreviewImage => SourcePreviewImage ?? ProcessedPreviewImage;

    public IAsyncRelayCommand SaveCommand { get; }

    public IRelayCommand AddRowCommand { get; }

    public IRelayCommand<OfflineMiningElementEditRow> DeleteRowCommand { get; }

    [ObservableProperty] private string? processedImagePath;
    [ObservableProperty] private ImageSource? processedPreviewImage;

    public RockScan ExtractedScan { get; private set; }

    [ObservableProperty] private string editableMaterial = string.Empty;
    [ObservableProperty] private string editableMass = string.Empty;
    [ObservableProperty] private string editableResistance = string.Empty;
    [ObservableProperty] private string editableInstability = string.Empty;
    [ObservableProperty] private string editableComposition = string.Empty;
    [ObservableProperty] private string editableLocation = string.Empty;
    [ObservableProperty] private ObservableCollection<OfflineMiningElementEditRow> editableElements = new();
    [ObservableProperty] private string summary = string.Empty;
    [ObservableProperty] private string? jsonPath;
    [ObservableProperty] private string? csvPath;
    [ObservableProperty] private bool isDirty;
    [ObservableProperty] private string processingState = string.Empty;
    [ObservableProperty] private bool isProcessing;
    [ObservableProperty] private string usedOcrModeSummary = string.Empty;
    [ObservableProperty] private string ocrSourceSummary = string.Empty;
    [ObservableProperty] private IReadOnlyList<string> selectedTargetResources = Array.Empty<string>();
    [ObservableProperty] private double imageZoom = 0.5d;
    [ObservableProperty] private string autoSaveStatus = string.Empty;
    [ObservableProperty] private string reviewStatus = ScanReviewCatalog.Pending;
    [ObservableProperty] private DateTime? exportedAtUtc;
    [ObservableProperty] private string exportBatchId = string.Empty;

    public string ImageZoomLabel => $"Zoom {Math.Round(ImageZoom * 100):0}%";
    public IReadOnlyList<string> ReviewStatusOptions => ScanReviewCatalog.Options;
    public string ReviewStateSummary => ExportedAtUtc is not null
        ? $"Exported {ExportedAtUtc:yyyy-MM-dd HH:mm}"
        : string.Equals(ReviewStatus, ScanReviewCatalog.Reviewed, StringComparison.Ordinal)
            ? "Ready for export"
            : string.Equals(ReviewStatus, ScanReviewCatalog.Rejected, StringComparison.Ordinal)
                ? "Rejected from export"
                : "Pending review";

    public string CompactHeadline => !string.IsNullOrWhiteSpace(ExtractedScan.Material)
        ? ExtractedScan.Material
        : "Mining capture queued";

    public bool CompactHeadlineIsHighlighted => SelectedTargetResources.Any(resource => string.Equals(resource, ExtractedScan.Material, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<CompactMiningOreItem> CompactOreItems
    {
        get
        {
            if (ExtractedScan.Elements.Count == 0)
            {
                return [];
            }

            var maxQuality = ExtractedScan.Elements
                .Where(entry => entry.Quality is not null)
                .Select(entry => entry.Quality)
                .DefaultIfEmpty()
                .Max();

            return ExtractedScan.Elements
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Element))
                .OrderByDescending(entry => entry.Quality ?? int.MinValue)
                .ThenByDescending(entry => entry.Percentage ?? decimal.MinValue)
                .Select(entry => new CompactMiningOreItem(
                    BuildCompactOreText(entry),
                    (maxQuality is not null && entry.Quality == maxQuality)
                    || SelectedTargetResources.Any(resource => string.Equals(resource, entry.Element, StringComparison.OrdinalIgnoreCase))))
                .ToArray();
        }
    }

    public string CompactMetricsSummary
    {
        get
        {
            var parts = new List<string>();

            if (ExtractedScan.Mass is not null)
            {
                parts.Add($"Mass {ExtractedScan.Mass:N0}");
            }

            if (ExtractedScan.Resistance is not null)
            {
                parts.Add($"Res {ExtractedScan.Resistance:0.##}%");
            }

            if (ExtractedScan.Instability is not null)
            {
                parts.Add($"Inst {ExtractedScan.Instability:0.##}");
            }

            if (ExtractedScan.Composition is not null)
            {
                parts.Add($"Comp {ExtractedScan.Composition:0.##}%");
            }

            return parts.Count == 0 ? "Metrics pending" : string.Join("  •  ", parts);
        }
    }

    public string CompactOreSummary
    {
        get
        {
            var bestEntry = ExtractedScan.Elements
                .OrderByDescending(entry => entry.Quality ?? int.MinValue)
                .ThenByDescending(entry => entry.Percentage ?? decimal.MinValue)
                .FirstOrDefault(entry => !string.IsNullOrWhiteSpace(entry.Element));

            if (bestEntry is null)
            {
                return string.IsNullOrWhiteSpace(ExtractedScan.Location)
                    ? "Composition pending"
                    : $"Location: {ExtractedScan.Location}";
            }

            var qualitySuffix = bestEntry.Quality is null ? string.Empty : $" Q{bestEntry.Quality}";
            var percentageSuffix = bestEntry.Percentage is null ? string.Empty : $"  •  {bestEntry.Percentage:0.##}%";
            return $"Best ore: {bestEntry.Element}{qualitySuffix}{percentageSuffix}";
        }
    }

    public string CompactStatusSummary
    {
        get
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(ProcessingState))
            {
                parts.Add(ProcessingState);
            }

            if (!string.IsNullOrWhiteSpace(UsedOcrModeSummary))
            {
                parts.Add($"OCR {UsedOcrModeSummary}");
            }

            if (!string.IsNullOrWhiteSpace(OcrSourceSummary))
            {
                parts.Add(OcrSourceSummary);
            }

            return parts.Count == 0 ? "Awaiting mining result" : string.Join("  •  ", parts);
        }
    }

    public bool HasCompactOreItems => CompactOreItems.Count > 0;

    public void ApplySelectedTargetResources(IEnumerable<string>? resources)
    {
        SelectedTargetResources = resources?
            .Where(resource => !string.IsNullOrWhiteSpace(resource))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? Array.Empty<string>();

        OnCompactDisplayChanged();
    }

    public RockScan BuildUpdatedScan()
    {
        return ExtractedScan with
        {
            Material = EditableMaterial.Trim(),
            Mass = TryParseInteger(EditableMass),
            Resistance = TryParseDecimal(EditableResistance),
            Instability = TryParseDecimal(EditableInstability),
            Composition = TryParseDecimal(EditableComposition),
            Location = EditableLocation.Trim(),
            Elements = EditableElements
                .Select(row => new RockCompositionEntry(
                    row.Element.Trim(),
                    TryParseDecimal(row.PercentageText),
                    TryParseInteger(row.QualityText)))
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Element)
                    || entry.Percentage is not null
                    || entry.Quality is not null)
                .ToArray()
        };
    }

    public void MarkSaved(RockScan savedScan, string? savedJsonPath, string? savedCsvPath)
    {
        ExtractedScan = savedScan;
        JsonPath = savedJsonPath;
        CsvPath = savedCsvPath;
        Summary = Utils.RockScanParser.BuildSummary(savedScan);
        _savedSnapshot = BuildSnapshot();
        IsDirty = false;
        if (_enableAutoSave)
        {
            AutoSaveStatus = "Saved";
        }
        OnCompactDisplayChanged();
    }

    public void ApplyReviewMetadata(string? reviewStatusValue, DateTime? exportedAtUtcValue, string? exportBatchIdValue)
    {
        _suppressAutoSave = true;
        ReviewStatus = ScanReviewCatalog.Normalize(reviewStatusValue);
        ExportedAtUtc = exportedAtUtcValue;
        ExportBatchId = exportBatchIdValue ?? string.Empty;
        _suppressAutoSave = false;
        OnPropertyChanged(nameof(ReviewStateSummary));
    }

    public void SetProcessingState(string state, bool value)
    {
        ProcessingState = state;
        IsProcessing = value;
        OnCompactDisplayChanged();
    }

    public void ApplyProcessedResult(RockScan processedScan, string processedSummary, string usedOcrModeSummary, string ocrSourceSummary, string? processedImageFilePath, string? savedJsonPath, string? savedCsvPath, string processingState)
    {
        ExtractedScan = processedScan;
        ProcessedImagePath = processedImageFilePath;
        ProcessedPreviewImage = LoadImage(processedImageFilePath);
        LoadEditableValues(processedScan);
        Summary = processedSummary;
        UsedOcrModeSummary = usedOcrModeSummary;
        OcrSourceSummary = ocrSourceSummary;
        JsonPath = savedJsonPath;
        CsvPath = savedCsvPath;
        ProcessingState = processingState;
        IsProcessing = false;
        _savedSnapshot = BuildSnapshot();
        IsDirty = false;
        if (_enableAutoSave)
        {
            AutoSaveStatus = "Saved";
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

    partial void OnEditableMaterialChanged(string value)
    {
        RefreshDirtyState();
    }

    partial void OnEditableMassChanged(string value)
    {
        RefreshDirtyState();
    }

    partial void OnEditableResistanceChanged(string value)
    {
        RefreshDirtyState();
    }

    partial void OnEditableInstabilityChanged(string value)
    {
        RefreshDirtyState();
    }

    partial void OnEditableCompositionChanged(string value)
    {
        RefreshDirtyState();
    }

    partial void OnEditableLocationChanged(string value)
    {
        RefreshDirtyState();
    }

    partial void OnEditableElementsChanged(ObservableCollection<OfflineMiningElementEditRow> value)
    {
        SubscribeToEditableElements(value);
        RefreshDirtyState();
    }

    partial void OnIsProcessingChanged(bool value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        AddRowCommand.NotifyCanExecuteChanged();
    }

    partial void OnReviewStatusChanged(string value)
    {
        var normalizedValue = ScanReviewCatalog.Normalize(value);
        if (!string.Equals(value, normalizedValue, StringComparison.Ordinal))
        {
            ReviewStatus = normalizedValue;
            return;
        }

        if (_enableAutoSave && !_suppressAutoSave)
        {
            ClearExportMetadata();
            _ = AutoSaveAsync($"Auto-saved mining review state for {DisplayName}.", forceMetadataPersist: true);
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

    partial void OnImageZoomChanged(double value)
    {
        OnPropertyChanged(nameof(ImageZoomLabel));
    }

    private void SubscribeToEditableElements(ObservableCollection<OfflineMiningElementEditRow> rows)
    {
        rows.CollectionChanged -= EditableElements_CollectionChanged;
        rows.CollectionChanged += EditableElements_CollectionChanged;

        foreach (var row in rows)
        {
            row.PropertyChanged -= EditableElementRow_PropertyChanged;
            row.PropertyChanged += EditableElementRow_PropertyChanged;
        }
    }

    private void EditableElements_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (OfflineMiningElementEditRow row in e.OldItems)
            {
                row.PropertyChanged -= EditableElementRow_PropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (OfflineMiningElementEditRow row in e.NewItems)
            {
                row.PropertyChanged -= EditableElementRow_PropertyChanged;
                row.PropertyChanged += EditableElementRow_PropertyChanged;
            }
        }

        RefreshDirtyState();
    }

    private void EditableElementRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshDirtyState();
    }

    private void RefreshDirtyState()
    {
        if (_suppressDirtyTracking)
        {
            return;
        }

        IsDirty = !string.Equals(_savedSnapshot, BuildSnapshot(), StringComparison.Ordinal);

        if (_enableAutoSave && IsDirty)
        {
            MarkPendingReview();

            if (!IsProcessing && !HasPendingBlankElementRow())
            {
                _ = AutoSaveAsync($"Auto-saved mining data for {DisplayName}.");
            }
        }
    }

    private void LoadEditableValues(RockScan rockScan)
    {
        _suppressDirtyTracking = true;

        EditableMaterial = rockScan.Material;
        EditableMass = rockScan.Mass?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        EditableResistance = rockScan.Resistance?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty;
        EditableInstability = rockScan.Instability?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty;
        EditableComposition = rockScan.Composition?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty;
        EditableLocation = rockScan.Location;
        EditableElements = new ObservableCollection<OfflineMiningElementEditRow>(
            rockScan.Elements.Select(entry => new OfflineMiningElementEditRow(
                entry.Element,
                entry.Percentage?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty,
                entry.Quality?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)));

        _suppressDirtyTracking = false;
        SubscribeToEditableElements(EditableElements);
        OnCompactDisplayChanged();
    }

    private bool CanSave()
    {
        return !IsProcessing;
    }

    private bool CanAddRow()
    {
        return !IsProcessing;
    }

    private async Task SaveAsync()
    {
        await SaveAsyncInternal();
    }

    private void AddRow()
    {
        EditableElements.Add(new OfflineMiningElementEditRow(string.Empty, string.Empty, string.Empty));
    }

    private void DeleteRow(OfflineMiningElementEditRow? row)
    {
        if (row is null)
        {
            return;
        }

        EditableElements.Remove(row);
    }

    private async Task AutoSaveAsync(string successMessage, bool forceMetadataPersist = false)
    {
        if (!_enableAutoSave)
        {
            return;
        }

        var saveVersion = Interlocked.Increment(ref _autoSaveVersion);
        AutoSaveStatus = "Saving...";

        try
        {
            await Task.Delay(350);

            if (saveVersion != _autoSaveVersion || IsProcessing)
            {
                return;
            }

            if (forceMetadataPersist && !IsDirty)
            {
                _persistCallback?.Invoke(this);
                AutoSaveStatus = "Saved";
                _statusCallback?.Invoke(successMessage);
                return;
            }

            if (!IsDirty)
            {
                AutoSaveStatus = "Saved";
                return;
            }

            await SaveAsyncInternal(successMessage);
        }
        catch (Exception ex)
        {
            AutoSaveStatus = "Save failed";
            _statusCallback?.Invoke($"Unable to auto-save mining data for {DisplayName}: {ex.Message}");
        }
    }

    private async Task SaveAsyncInternal(string? successMessage = null)
    {
        if (IsProcessing)
        {
            return;
        }

        SetProcessingState("Saving edits...", true);

        try
        {
            var updatedScan = await Task.Run(() =>
            {
                var scan = BuildUpdatedScan();
                var savedJsonPath = RockScanJsonStore.Save(scan);
                var savedCsvPath = RockScanCsvStore.Upsert(scan);
                return (Scan: scan, JsonPath: savedJsonPath, CsvPath: savedCsvPath);
            });

            MarkSaved(updatedScan.Scan, updatedScan.JsonPath, updatedScan.CsvPath);
            ProcessingState = "Saved";
            _persistCallback?.Invoke(this);
            _statusCallback?.Invoke(successMessage ?? $"Saved updated mining data to {updatedScan.JsonPath} and synchronized {updatedScan.CsvPath}.");
        }
        catch (Exception ex)
        {
            ProcessingState = "Save failed";
            if (_enableAutoSave)
            {
                AutoSaveStatus = "Save failed";
            }
            _statusCallback?.Invoke($"Unable to save mining data for {DisplayName}: {ex.Message}");
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private bool HasPendingBlankElementRow()
    {
        return EditableElements.Any(row => string.IsNullOrWhiteSpace(row.Element)
            && string.IsNullOrWhiteSpace(row.PercentageText)
            && string.IsNullOrWhiteSpace(row.QualityText));
    }

    private void MarkPendingReview()
    {
        if (!_enableAutoSave)
        {
            return;
        }

        if (string.Equals(ReviewStatus, ScanReviewCatalog.Pending, StringComparison.Ordinal)
            && ExportedAtUtc is null
            && string.IsNullOrWhiteSpace(ExportBatchId))
        {
            return;
        }

        _suppressAutoSave = true;
        ReviewStatus = ScanReviewCatalog.Pending;
        ExportedAtUtc = null;
        ExportBatchId = string.Empty;
        _suppressAutoSave = false;
        OnPropertyChanged(nameof(ReviewStateSummary));
    }

    private void ClearExportMetadata()
    {
        _suppressAutoSave = true;
        ExportedAtUtc = null;
        ExportBatchId = string.Empty;
        _suppressAutoSave = false;
        OnPropertyChanged(nameof(ReviewStateSummary));
    }

    private string BuildSnapshot()
    {
        var elementSnapshot = string.Join("|", EditableElements.Select(row => $"{row.Element}\u001F{row.PercentageText}\u001F{row.QualityText}"));
        return string.Join("\u001E",
            EditableLocation,
            EditableMaterial,
            EditableMass,
            EditableResistance,
            EditableInstability,
            EditableComposition,
            elementSnapshot);
    }

    private static int? TryParseInteger(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static decimal? TryParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().Replace(',', '.');
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
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
        OnPropertyChanged(nameof(CompactHeadline));
        OnPropertyChanged(nameof(CompactHeadlineIsHighlighted));
        OnPropertyChanged(nameof(CompactMetricsSummary));
        OnPropertyChanged(nameof(CompactOreSummary));
        OnPropertyChanged(nameof(CompactStatusSummary));
        OnPropertyChanged(nameof(CompactOreItems));
        OnPropertyChanged(nameof(HasCompactOreItems));
    }

    private static string BuildCompactOreText(RockCompositionEntry entry)
    {
        var parts = new List<string> { entry.Element };

        if (entry.Quality is not null)
        {
            parts.Add($"Q{entry.Quality}");
        }

        if (entry.Percentage is not null)
        {
            parts.Add($"{entry.Percentage:0.##}%");
        }

        return string.Join(" ", parts);
    }
}

public sealed record CompactMiningOreItem(string DisplayText, bool IsHighlighted);

public sealed partial class OfflineMiningElementEditRow : ObservableObject
{
    public OfflineMiningElementEditRow(string element, string percentageText, string qualityText)
    {
        this.element = element;
        this.percentageText = percentageText;
        this.qualityText = qualityText;
    }

    [ObservableProperty] private string element = string.Empty;
    [ObservableProperty] private string percentageText = string.Empty;
    [ObservableProperty] private string qualityText = string.Empty;
}