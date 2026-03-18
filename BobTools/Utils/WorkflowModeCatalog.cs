using BobTools.Models;

namespace BobTools.Utils;

internal static class WorkflowModeCatalog
{
    public const string Scanning = "scanning";
    public const string Mining = "mining";
    public const string Offline = "offline";

    public static IReadOnlyList<CaptureProfileOption> Options { get; } = new[]
    {
        new CaptureProfileOption(Scanning, "Scanning"),
        new CaptureProfileOption(Mining, "Mining"),
        new CaptureProfileOption(Offline, "Offline")
    };

    public static bool IsValid(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && Options.Any(option => option.Value == value);
    }

    public static string GetDisplayName(string? value)
    {
        return Options.FirstOrDefault(option => option.Value == value)?.DisplayName ?? "Unknown";
    }

    public static string? GetCaptureProfile(string? value)
    {
        return value switch
        {
            Scanning => CaptureProfileCatalog.Signature,
            Mining => CaptureProfileCatalog.Mining,
            _ => null
        };
    }
}