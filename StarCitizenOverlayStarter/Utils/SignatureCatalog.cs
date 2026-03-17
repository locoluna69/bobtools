using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace StarCitizenOverlayStarter.Utils;

internal static class SignatureCatalog
{
    private static readonly Regex NumericValuePattern = new(@"(?<!\d)(?:\d{1,3}(?:,\d{3})+|\d+)(?!\d)", RegexOptions.Compiled);
    private static readonly Lazy<IReadOnlyList<SignatureDefinition>> Definitions = new(LoadDefinitions);

    public static string BuildResourceSummary(string? normalizedText)
    {
        if (!TryParseSignatureValue(normalizedText, out var observedValue))
        {
            return "Signature resource: no numeric value detected";
        }

        var matches = Definitions.Value
            .Where(definition => observedValue % definition.Signature == 0)
            .OrderByDescending(definition => definition.Signature)
            .ToList();

        if (matches.Count == 0)
        {
            return $"Signature resource: no match for {observedValue:N0}";
        }

        var exactMatch = matches.FirstOrDefault(definition => definition.Signature == observedValue);
        var bestMatch = exactMatch ?? matches[0];
        var multiplier = observedValue / bestMatch.Signature;

        return multiplier == 1
            ? $"Signature resource: {bestMatch.Resource} ({bestMatch.Signature:N0})"
            : $"Signature resource: {bestMatch.Resource} ({bestMatch.Signature:N0} × {multiplier})";
    }

    private static bool TryParseSignatureValue(string? normalizedText, out int value)
    {
        value = 0;

        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            return false;
        }

        var match = NumericValuePattern.Match(normalizedText);
        if (!match.Success)
        {
            return false;
        }

        var digits = match.Value.Replace(",", string.Empty, StringComparison.Ordinal);
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static IReadOnlyList<SignatureDefinition> LoadDefinitions()
    {
        var dataPath = Path.Combine(AppContext.BaseDirectory, "Assets", "data", "signatures.csv");
        if (!File.Exists(dataPath))
        {
            return Array.Empty<SignatureDefinition>();
        }

        return File.ReadAllLines(dataPath)
            .Skip(1)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(ParseDefinition)
            .Where(definition => definition is not null)
            .Cast<SignatureDefinition>()
            .ToArray();
    }

    private static SignatureDefinition? ParseDefinition(string line)
    {
        var parts = line.Split(',', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return null;
        }

        return int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var signature)
            ? new SignatureDefinition(parts[0], signature)
            : null;
    }

    private sealed record SignatureDefinition(string Resource, int Signature);
}