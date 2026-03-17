using StarCitizenOverlayStarter.Models;

namespace StarCitizenOverlayStarter.Utils;

internal static class CaptureProfileCatalog
{
    public const string Signature = "signature";
    public const string Mining = "mining";

    public static IReadOnlyList<CaptureProfileOption> Options { get; } = new[]
    {
        new CaptureProfileOption(Signature, "Signature"),
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
}