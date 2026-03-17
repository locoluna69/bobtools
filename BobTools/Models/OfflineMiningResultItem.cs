using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BobTools.Utils;

namespace BobTools.Models;

public sealed partial class OfflineMiningResultItem : ObservableObject
{
    private string _savedSnapshot = string.Empty;
    private readonly Action<string>? _statusCallback;
    private bool _suppressDirtyTracking;

    public OfflineMiningResultItem(
        string sourceImagePath,
        string? processedImagePath,
        RockScan extractedScan,
        string summary,
        string? jsonPath,
        string? csvPath,
        Action<string>? statusCallback = null,
        string processingState = "Ready",
        bool isProcessing = false)
    {
        SourceImagePath = sourceImagePath;
        DisplayName = Path.GetFileName(sourceImagePath);
        _statusCallback = statusCallback;
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
    }

    public void SetProcessingState(string state, bool value)
    {
        ProcessingState = state;
        IsProcessing = value;
    }

    public void ApplyProcessedResult(RockScan processedScan, string processedSummary, string? processedImageFilePath, string? savedJsonPath, string? savedCsvPath, string processingState)
    {
        ExtractedScan = processedScan;
        ProcessedImagePath = processedImageFilePath;
        ProcessedPreviewImage = LoadImage(processedImageFilePath);
        LoadEditableValues(processedScan);
        Summary = processedSummary;
        JsonPath = savedJsonPath;
        CsvPath = savedCsvPath;
        ProcessingState = processingState;
        IsProcessing = false;
        _savedSnapshot = BuildSnapshot();
        IsDirty = false;
    }

    public void ApplyProcessingFailure(string processingState)
    {
        ProcessingState = processingState;
        IsProcessing = false;
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
            _statusCallback?.Invoke($"Saved updated mining data to {updatedScan.JsonPath} and synchronized {updatedScan.CsvPath}.");
        }
        catch (Exception ex)
        {
            ProcessingState = "Save failed";
            _statusCallback?.Invoke($"Unable to save mining data for {DisplayName}: {ex.Message}");
        }
        finally
        {
            IsProcessing = false;
        }
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
}

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