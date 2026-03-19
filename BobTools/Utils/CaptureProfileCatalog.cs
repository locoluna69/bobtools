using BobTools.Models;

namespace BobTools.Utils;

internal static class CaptureProfileCatalog
{
    public const string Signature = "signature";
    public const string Mining = "mining";
    public const int SignatureFixedWidth = 250;
    public const int SignatureFixedHeight = 120;

    public static IReadOnlyList<CaptureProfileOption> Options { get; } = new[]
    {
        new CaptureProfileOption(Signature, "Scan"),
        new CaptureProfileOption(Mining, "Mining")
    };

    public static bool IsValid(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && Options.Any(option => option.Value == value);
    }

    public static string GetDisplayName(string? value)
    {
        return Options.FirstOrDefault(option => option.Value == value)?.DisplayName ?? "Unknown";
    }

    public static bool UsesFixedRegion(string? value)
    {
        return string.Equals(value, Signature, StringComparison.OrdinalIgnoreCase);
    }

    public static (int Width, int Height)? GetFixedRegionSize(string? value)
    {
        return UsesFixedRegion(value)
            ? (SignatureFixedWidth, SignatureFixedHeight)
            : null;
    }
}