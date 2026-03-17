using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace StarCitizenOverlayStarter.Models;

public sealed partial class OfflineMiningResultItem : ObservableObject
{
    private string _savedSnapshot = string.Empty;

    public OfflineMiningResultItem(
        string sourceImagePath,
        string? processedImagePath,
        RockScan extractedScan,
        string summary,
        string? jsonPath,
        string? csvPath)
    {
        SourceImagePath = sourceImagePath;
        DisplayName = Path.GetFileName(sourceImagePath);
        ProcessedImagePath = processedImagePath;
        ProcessedPreviewImage = LoadImage(processedImagePath);
        ExtractedScan = extractedScan;
        editableMaterial = extractedScan.Material;
        editableMass = extractedScan.Mass?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        editableResistance = extractedScan.Resistance?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty;
        editableInstability = extractedScan.Instability?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty;
        editableComposition = extractedScan.Composition?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty;
        editableLocation = extractedScan.Location;
        editableElements = new ObservableCollection<OfflineMiningElementEditRow>(
            extractedScan.Elements.Select(entry => new OfflineMiningElementEditRow(
                entry.Element,
                entry.Percentage?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty,
                entry.Quality?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)));
        SubscribeToEditableElements(editableElements);
        this.summary = summary;
        this.jsonPath = jsonPath;
        this.csvPath = csvPath;
        _savedSnapshot = BuildSnapshot();
    }

    public string DisplayName { get; }

    public string SourceImagePath { get; }

    public string? ProcessedImagePath { get; }

    public ImageSource? ProcessedPreviewImage { get; }

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
        IsDirty = !string.Equals(_savedSnapshot, BuildSnapshot(), StringComparison.Ordinal);
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