using StarCitizenOverlayStarter.Models;

namespace StarCitizenOverlayStarter.Utils;

internal static class OcrModeCatalog
{
    public const string Auto = "auto";
    public const string RightmostNumericValue = "rightmost-numeric-value";
    public const string GrayscaleBlock = "grayscale-block";
    public const string GrayscaleContrastBlock = "grayscale-contrast-block";
    public const string BaseMinus20Block = "base_threshold_otsu_minus20-block";
    public const string BaseMinus15Block = "base_threshold_otsu_minus15-block";
    public const string BaseMinus25Block = "base_threshold_otsu_minus25-block";
    public const string ContrastMinus20Block = "contrast_threshold_otsu_minus20-block";
    public const string ContrastMinus15Block = "contrast_threshold_otsu_minus15-block";
    public const string ContrastMinus25Block = "contrast_threshold_otsu_minus25-block";
    public const string ContrastMinus30Block = "contrast_threshold_otsu_minus30-block";

    public static IReadOnlyList<OcrModeOption> Options { get; } = new[]
    {
        new OcrModeOption(Auto, "Auto"),
        new OcrModeOption(RightmostNumericValue, "Rightmost Numeric Value"),
        new OcrModeOption(GrayscaleBlock, "Grayscale Block"),
        new OcrModeOption(GrayscaleContrastBlock, "Contrast Grayscale Block"),
        new OcrModeOption(BaseMinus25Block, "Base Otsu -25 Block"),
        new OcrModeOption(BaseMinus20Block, "Base Otsu -20 Block"),
        new OcrModeOption(BaseMinus15Block, "Base Otsu -15 Block"),
        new OcrModeOption(ContrastMinus30Block, "Contrast Otsu -30 Block"),
        new OcrModeOption(ContrastMinus25Block, "Contrast Otsu -25 Block"),
        new OcrModeOption(ContrastMinus20Block, "Contrast Otsu -20 Block"),
        new OcrModeOption(ContrastMinus15Block, "Contrast Otsu -15 Block")
    };

    public static bool IsValid(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && Options.Any(option => option.Value == value);
    }

    public static string GetDisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unknown";
        }

        if (value.Contains('|'))
        {
            var parts = value.Split('|', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
            {
                return $"{GetDisplayName(parts[0])} (from {GetDisplayName(parts[1])})";
            }
        }

        var known = Options.FirstOrDefault(option => option.Value == value);
        if (known is not null)
        {
            return known.DisplayName;
        }

        return value
            .Replace('_', ' ')
            .Replace('-', ' ');
    }
}