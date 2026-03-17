using System.Globalization;
using System.Text.RegularExpressions;
using StarCitizenOverlayStarter.Models;

namespace StarCitizenOverlayStarter.Utils;

internal static class RockScanParser
{
    private static readonly Regex MassPattern = new(@"MASS:\s*(?<value>\d{2,6})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ResistancePattern = new(@"RESISTANCE:\s*(?<value>\d{1,3}(?:[\.,]\d{1,2})?)\s*%", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex InstabilityPattern = new(@"INSTABILITY:\s*(?<value>\d{1,3}(?:[\.,]\d{1,2})?)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CompositionPattern = new(@"COMPOSITION:\s*(?<value>\d{1,3}(?:[\.,]\d{1,2})?)\s*%?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CompositionLinePattern = new(@"^\s*(?<pct>\d{1,3}(?:[\.,]\d{1,2})?)%\s+(?<material>[A-Z][A-Z0-9\-\s]+?(?:\s*\([A-Z\-\s]+\))?)\s+(?<qty>\d{1,4})\s*[:;'\]\[]*$", RegexOptions.Compiled);
    private static readonly Regex PercentageMaterialLinePattern = new(@"^\s*(?<pct>[A-Z0-9]{1,3}(?:[\.,]\d{1,2})?)%\s+(?<material>[A-Z][A-Z0-9\-\s]+?(?:\s*\([A-Z\-\s]+\))?)\s*[:;'\]\[]*$", RegexOptions.Compiled);
    private static readonly Regex MaterialLinePattern = new(@"^[A-Z][A-Z\-\s]+(?:\([A-Z\-\s]+\))?$", RegexOptions.Compiled);
    private static readonly Regex NumericLinePattern = new(@"^\d{2,6}$", RegexOptions.Compiled);
    private static readonly Regex PercentageOnlyPattern = new(@"^(?<value>[A-Z0-9]{1,3}(?:[\.,]\d{1,2})?)\s*%[:;\]\[]*$", RegexOptions.Compiled);
    private static readonly Regex QuantityOnlyPattern = new(@"^(?<value>[\[\]I|£l\d]{1,5})[:;'\]\[]*$", RegexOptions.Compiled);
    private static readonly HashSet<string> IgnoredLines = new(StringComparer.OrdinalIgnoreCase)
    {
        "SCAN RESULTS",
        "COMPOSITION",
        "IMPOSSIBLE",
        "MASS:",
        "MASS",
        "RESISTANCE:",
        "INSTABILITY:",
        "30.75 SCU",
        "RO aainy",
        "aainy",
        "Resto",
        "Resto",
        "I",
        "l",
        "]",
        "("
    };

    public static bool TryParse(string? text, out RockScan rockScan)
    {
        rockScan = new RockScan();

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalizedText = text.Replace('\r', '\n');
        var warnings = new List<string>();
        var lines = normalizedText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => NormalizeLine(line.Trim()))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        var materialName = ExtractMaterialName(lines, warnings);
        var mass = ExtractMass(normalizedText, lines, warnings);
        var resistance = TryParseDecimal(ResistancePattern.Match(normalizedText).Groups["value"].Value);
        var instability = TryParseDecimal(InstabilityPattern.Match(normalizedText).Groups["value"].Value);
        var compositionValue = ExtractCompositionValue(normalizedText, lines, warnings);
        var elements = ExtractElements(lines, warnings);

        if (string.IsNullOrWhiteSpace(materialName)
            && mass is null
            && resistance is null
            && instability is null
            && compositionValue is null
            && elements.Count == 0)
        {
            return false;
        }

        rockScan = new RockScan
        {
            Material = materialName,
            Mass = mass,
            Resistance = resistance,
            Instability = instability,
            Composition = compositionValue,
            Elements = elements,
            ParseWarnings = warnings,
            SourceText = text
        };

        return true;
    }

    public static string BuildSummary(RockScan? rockScan)
    {
        if (rockScan is null)
        {
            return "Rock scan: unable to parse";
        }

        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(rockScan.Material))
        {
            parts.Add(rockScan.Material);
        }

        if (rockScan.Mass is not null)
        {
            parts.Add($"Mass {rockScan.Mass:N0}");
        }

        if (rockScan.Resistance is not null)
        {
            parts.Add($"Resistance {rockScan.Resistance:0.##}%");
        }

        if (rockScan.Instability is not null)
        {
            parts.Add($"Instability {rockScan.Instability:0.##}");
        }

        if (rockScan.Composition is not null)
        {
            parts.Add($"Composition {rockScan.Composition:0.##}%");
        }

        if (rockScan.Elements.Count > 0)
        {
            parts.Add($"{rockScan.Elements.Count} materials");
        }

        return parts.Count == 0
            ? "Rock scan: unable to parse"
            : $"Rock scan: {string.Join(" | ", parts)}";
    }

    private static string ExtractMaterialName(IEnumerable<string> lines, ICollection<string> warnings)
    {
        foreach (var line in lines)
        {
            var candidate = line.Trim();
            if (candidate.Length < 4
                || candidate.Contains(':', StringComparison.Ordinal)
                || candidate.Contains('%', StringComparison.Ordinal)
                || IgnoredLines.Contains(candidate)
                || string.Equals(candidate, "INERT MATERIALS", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (MaterialLinePattern.IsMatch(candidate))
            {
                return NormalizeMaterialName(candidate, warnings);
            }
        }

        return string.Empty;
    }

    private static IReadOnlyList<RockCompositionEntry> ExtractElements(IEnumerable<string> lines, ICollection<string> warnings)
    {
        var entries = new List<RockCompositionEntry>();
        var lineList = lines.ToList();
        var compositionIndex = lineList.FindIndex(line => string.Equals(line, "COMPOSITION", StringComparison.OrdinalIgnoreCase));
        if (compositionIndex < 0)
        {
            compositionIndex = 0;
        }

        decimal? stagedPercentage = null;
        PendingCompositionEntry? currentEntry = null;

        foreach (var line in lineList.Skip(compositionIndex + 1))
        {
            if (IgnoredLines.Contains(line))
            {
                continue;
            }

            var match = CompositionLinePattern.Match(line);
            if (match.Success)
            {
                var percentage = TryParseDecimal(NormalizeNumericToken(match.Groups["pct"].Value));
                if (percentage is null)
                {
                    continue;
                }

                entries.Add(new RockCompositionEntry(
                    NormalizeMaterialName(match.Groups["material"].Value, warnings),
                    percentage.Value,
                    TryParseInteger(NormalizeQuantityToken(match.Groups["qty"].Value))));

                stagedPercentage = null;
                currentEntry = null;
                continue;
            }

            if (TryParsePercentageLine(line, out var percentageOnly))
            {
                if (currentEntry is not null && currentEntry.Percentage is null)
                {
                    currentEntry = currentEntry with { Percentage = percentageOnly };

                    if (currentEntry.Quality is not null)
                    {
                        entries.Add(new RockCompositionEntry(currentEntry.Material, currentEntry.Percentage, currentEntry.Quality));
                        currentEntry = null;
                    }
                }
                else
                {
                    stagedPercentage = percentageOnly;
                }

                continue;
            }

            if (TryParsePercentageMaterialLine(line, warnings, out var percentageMaterialEntry))
            {
                if (currentEntry is not null)
                {
                    entries.Add(new RockCompositionEntry(currentEntry.Material, currentEntry.Percentage, currentEntry.Quality));
                    warnings.Add(BuildPartialCompositionWarning(currentEntry));
                }

                currentEntry = percentageMaterialEntry;
                stagedPercentage = null;
                continue;
            }

            if (IsLikelyCompositionMaterial(line))
            {
                if (currentEntry is not null)
                {
                    entries.Add(new RockCompositionEntry(currentEntry.Material, currentEntry.Percentage, currentEntry.Quality));
                    warnings.Add(BuildPartialCompositionWarning(currentEntry));
                }

                currentEntry = new PendingCompositionEntry(NormalizeMaterialName(line, warnings), stagedPercentage, null);
                stagedPercentage = null;
                continue;
            }

            if (currentEntry is not null && TryParseQuantityLine(line, out var quantity))
            {
                currentEntry = currentEntry with { Quality = quantity };

                if (currentEntry.Percentage is not null)
                {
                    entries.Add(new RockCompositionEntry(currentEntry.Material, currentEntry.Percentage, currentEntry.Quality));
                    currentEntry = null;
                }
            }
        }

        if (currentEntry is not null)
        {
            entries.Add(new RockCompositionEntry(currentEntry.Material, currentEntry.Percentage, currentEntry.Quality));
            warnings.Add(BuildPartialCompositionWarning(currentEntry));
        }

        if (stagedPercentage is not null)
        {
            warnings.Add($"Unmatched composition percentage '{stagedPercentage:0.##}%'.");
        }

        if (compositionIndex >= 0 && entries.Count == 0)
        {
            warnings.Add("Composition section detected, but no composition elements were parsed.");
        }

        return entries;
    }

    private static decimal? ExtractCompositionValue(string normalizedText, IReadOnlyList<string> lines, ICollection<string> warnings)
    {
        var direct = TryParseDecimal(CompositionPattern.Match(normalizedText).Groups["value"].Value);
        if (direct is not null)
        {
            return direct;
        }

        for (var index = 0; index < lines.Count; index++)
        {
            if (!lines[index].StartsWith("COMPOSITION", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            for (var lookahead = 1; lookahead <= 2; lookahead++)
            {
                var nextIndex = index + lookahead;
                if (nextIndex < lines.Count && TryParsePercentageLine(lines[nextIndex], out var value))
                {
                    warnings.Add("Composition was reconstructed from a nearby OCR line instead of the same line as COMPOSITION:.");
                    return value;
                }

                if (nextIndex < lines.Count && TryParsePercentageMaterialLine(lines[nextIndex], warnings, out var percentageMaterialEntry))
                {
                    warnings.Add("Composition was reconstructed from a nearby OCR line instead of the same line as COMPOSITION:.");
                    return percentageMaterialEntry.Percentage;
                }
            }

            break;
        }

        return null;
    }

    private static int? ExtractMass(string normalizedText, IReadOnlyList<string> lines, ICollection<string> warnings)
    {
        var direct = TryParseInteger(MassPattern.Match(normalizedText).Groups["value"].Value);
        if (direct is not null)
        {
            return direct;
        }

        for (var index = 0; index < lines.Count; index++)
        {
            if (!lines[index].StartsWith("MASS", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            for (var lookback = 1; lookback <= 2; lookback++)
            {
                var previousIndex = index - lookback;
                if (previousIndex >= 0 && NumericLinePattern.IsMatch(lines[previousIndex]))
                {
                    warnings.Add("Mass was reconstructed from a nearby OCR line instead of the same line as MASS:.");
                    return TryParseInteger(lines[previousIndex]);
                }
            }

            for (var lookahead = 1; lookahead <= 2; lookahead++)
            {
                var nextIndex = index + lookahead;
                if (nextIndex < lines.Count && NumericLinePattern.IsMatch(lines[nextIndex]))
                {
                    warnings.Add("Mass was reconstructed from a nearby OCR line instead of the same line as MASS:.");
                    return TryParseInteger(lines[nextIndex]);
                }
            }
        }

        return null;
    }

    private static bool TryParsePercentageLine(string line, out decimal percentage)
    {
        percentage = 0;
        var match = PercentageOnlyPattern.Match(line);
        if (!match.Success)
        {
            return false;
        }

        var normalized = NormalizeNumericToken(match.Groups["value"].Value);
        var parsed = TryParseDecimal(normalized);
        if (parsed is null)
        {
            return false;
        }

        percentage = parsed.Value;
        return true;
    }

    private static bool TryParsePercentageMaterialLine(string line, ICollection<string> warnings, out PendingCompositionEntry entry)
    {
        entry = default!;

        var match = PercentageMaterialLinePattern.Match(line);
        if (!match.Success)
        {
            return false;
        }

        var percentage = TryParseDecimal(NormalizeNumericToken(match.Groups["pct"].Value));
        if (percentage is null)
        {
            return false;
        }

        entry = new PendingCompositionEntry(
            NormalizeMaterialName(match.Groups["material"].Value, warnings),
            percentage,
            null);

        return true;
    }

    private static bool TryParseQuantityLine(string line, out int? quantity)
    {
        quantity = null;
        var match = QuantityOnlyPattern.Match(line);
        if (!match.Success)
        {
            return false;
        }

        quantity = TryParseInteger(NormalizeQuantityToken(match.Groups["value"].Value));
        return quantity is not null;
    }

    private static bool IsLikelyCompositionMaterial(string line)
    {
        return !IgnoredLines.Contains(line)
            && !line.Contains(':', StringComparison.Ordinal)
            && !line.Contains('%', StringComparison.Ordinal)
            && !NumericLinePattern.IsMatch(line)
            && MaterialLinePattern.IsMatch(line);
    }

    private static string NormalizeMaterialName(string value, ICollection<string>? warnings = null)
    {
        var normalized = Regex.Replace(value.Trim(), "\\s+", " ");
        normalized = normalized.Replace("ORF", "ORE", StringComparison.OrdinalIgnoreCase);
        var catalogValue = MineableCatalog.Normalize(normalized, out var warning);
        if (!string.IsNullOrWhiteSpace(warning) && warnings is not null)
        {
            warnings.Add(warning);
        }

        return catalogValue;
    }

    private static string NormalizeLine(string value)
    {
        var normalized = value
            .Replace('‘', ' ')
            .Replace('’', ' ')
            .Replace('“', ' ')
            .Replace('”', ' ')
            .Replace('\t', ' ')
            .Trim();

        normalized = Regex.Replace(normalized, "\\s+", " ");
        if (PercentageOnlyPattern.IsMatch(normalized))
        {
            var match = PercentageOnlyPattern.Match(normalized);
            normalized = $"{NormalizeNumericToken(match.Groups["value"].Value)}%";
        }

        return normalized;
    }

    private static string NormalizeNumericToken(string value)
    {
        return value
            .Trim()
            .Replace('B', '8')
            .Replace('b', '6')
            .Replace('O', '0')
            .Replace('o', '0')
            .Replace(',', '.');
    }

    private static string NormalizeQuantityToken(string value)
    {
        return value
            .Trim()
            .Replace("[", string.Empty, StringComparison.Ordinal)
            .Replace("]", string.Empty, StringComparison.Ordinal)
            .Replace("|", string.Empty, StringComparison.Ordinal)
            .Replace("I", "1", StringComparison.Ordinal)
            .Replace("l", "1", StringComparison.Ordinal)
            .Replace("£", "5", StringComparison.Ordinal);
    }

    private sealed record PendingCompositionEntry(string Material, decimal? Percentage, int? Quality);

    private static string BuildPartialCompositionWarning(PendingCompositionEntry entry)
    {
        if (entry.Percentage is null && entry.Quality is null)
        {
            return $"Composition element '{entry.Material}' was detected without percentage or quality.";
        }

        if (entry.Percentage is null)
        {
            return $"Missing percentage for composition element '{entry.Material}'.";
        }

        if (entry.Quality is null)
        {
            return $"Missing quality for composition element '{entry.Material}'.";
        }

        return $"Composition element '{entry.Material}' was reconstructed from fragmented OCR lines.";
    }

    private static int? TryParseInteger(string value)
    {
        return int.TryParse(value.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static decimal? TryParseDecimal(string value)
    {
        var normalized = value.Replace(',', '.');
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}