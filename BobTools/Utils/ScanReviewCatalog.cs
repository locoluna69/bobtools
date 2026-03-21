namespace BobTools.Utils;

internal static class ScanReviewCatalog
{
    public const string Pending = "Pending";
    public const string Reviewed = "Reviewed";
    public const string Rejected = "Rejected";

    public static IReadOnlyList<string> Options { get; } = [Pending, Reviewed, Rejected];

    public static string Normalize(string? value)
    {
        if (string.Equals(value, Reviewed, StringComparison.OrdinalIgnoreCase))
        {
            return Reviewed;
        }

        if (string.Equals(value, Rejected, StringComparison.OrdinalIgnoreCase))
        {
            return Rejected;
        }

        return Pending;
    }

    public static bool IsExportEligible(string? reviewStatus, DateTime? exportedAtUtc)
    {
        return string.Equals(Normalize(reviewStatus), Reviewed, StringComparison.Ordinal)
            && exportedAtUtc is null;
    }
}