using BobTools.Models;

namespace BobTools.Utils;

internal static class MiningOcrStrategyCatalog
{
    public const string WholeRegion = "whole-region";
    public const string RowSegmented = "row-segmented";
    public const string Hybrid = "hybrid";

    public static IReadOnlyList<CaptureProfileOption> Options { get; } = new[]
    {
        new CaptureProfileOption(WholeRegion, "Whole Region"),
        new CaptureProfileOption(RowSegmented, "Row Segmented"),
        new CaptureProfileOption(Hybrid, "Hybrid")
    };

    public static bool IsValid(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && Options.Any(option => option.Value == value);
    }

    public static string GetDisplayName(string? value)
    {
        return Options.FirstOrDefault(option => option.Value == value)?.DisplayName ?? "Whole Region";
    }

    public static string GetDescription(string? value)
    {
        return value switch
        {
            RowSegmented => "Split the mining capture into horizontal text rows and OCR each row independently before parsing.",
            Hybrid => "Run both whole-region and row-segmented OCR, then keep the parse with the stronger structured mining result.",
            _ => "OCR the full mining capture in a single pass, matching the original BobTools mining workflow."
        };
    }
}