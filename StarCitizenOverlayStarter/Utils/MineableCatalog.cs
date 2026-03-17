using System.IO;
using System.Text.RegularExpressions;

namespace StarCitizenOverlayStarter.Utils;

internal static class MineableCatalog
{
    private static readonly Lazy<IReadOnlyList<MineableDefinition>> Definitions = new(LoadDefinitions);
    private static readonly Dictionary<string, string> AliasMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ALUMINIUM"] = "Aluminum",
        ["ALUMINUM"] = "Aluminum",
        ["HEPHESTANITE"] = "Hephaestanite",
        ["HEPHAESTANITE"] = "Hephaestanite",
        ["HEPHAESTENITE"] = "Hephaestanite",
        ["INERT MATERIALS"] = "Inert Material",
        ["INERT MATERIAL"] = "Inert Material"
    };

    public static string Normalize(string? value, out string? warning)
    {
        warning = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleanedValue = Regex.Replace(value.Trim(), "\\s+", " ");
        var normalizedKey = NormalizeKey(cleanedValue);
        if (string.IsNullOrWhiteSpace(normalizedKey))
        {
            return cleanedValue;
        }

        if (TryResolveAlias(normalizedKey, out var aliasMatch))
        {
            return aliasMatch;
        }

        var exactMatch = Definitions.Value.FirstOrDefault(definition => definition.Key == normalizedKey);
        if (exactMatch is not null)
        {
            return exactMatch.Material;
        }

        var fuzzyMatch = FindBestFuzzyMatch(normalizedKey);
        if (fuzzyMatch is null)
        {
            return cleanedValue;
        }

        warning = $"Material '{cleanedValue}' was normalized to '{fuzzyMatch.Material}' using mineables.csv.";
        return fuzzyMatch.Material;
    }

    private static MineableDefinition? FindBestFuzzyMatch(string normalizedKey)
    {
        MineableDefinition? bestMatch = null;
        var bestDistance = int.MaxValue;
        var secondBestDistance = int.MaxValue;

        foreach (var definition in Definitions.Value)
        {
            var distance = ComputeLevenshteinDistance(normalizedKey, definition.Key);
            if (distance < bestDistance)
            {
                secondBestDistance = bestDistance;
                bestDistance = distance;
                bestMatch = definition;
            }
            else if (distance < secondBestDistance)
            {
                secondBestDistance = distance;
            }
        }

        if (bestMatch is null)
        {
            return null;
        }

        if (bestDistance > GetMaximumDistance(normalizedKey.Length))
        {
            return null;
        }

        return bestDistance == secondBestDistance
            ? null
            : bestMatch;
    }

    private static bool TryResolveAlias(string normalizedKey, out string canonicalMaterial)
    {
        foreach (var alias in AliasMap)
        {
            if (NormalizeKey(alias.Key) == normalizedKey)
            {
                canonicalMaterial = alias.Value;
                return true;
            }
        }

        canonicalMaterial = string.Empty;
        return false;
    }

    private static IReadOnlyList<MineableDefinition> LoadDefinitions()
    {
        var dataPath = Path.Combine(AppContext.BaseDirectory, "Assets", "data", "mineables.csv");
        if (!File.Exists(dataPath))
        {
            return Array.Empty<MineableDefinition>();
        }

        return File.ReadAllLines(dataPath)
            .Skip(1)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(ParseDefinition)
            .Where(definition => definition is not null)
            .Cast<MineableDefinition>()
            .ToArray();
    }

    private static MineableDefinition? ParseDefinition(string line)
    {
        var parts = line.Split(',', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]))
        {
            return null;
        }

        var material = parts[0].Trim();
        var category = parts[1].Trim();
        return new MineableDefinition(material, category, NormalizeKey(material));
    }

    private static string NormalizeKey(string value)
    {
        return value
            .ToUpperInvariant()
            .Replace('0', 'O')
            .Replace('1', 'I')
            .Replace('5', 'S')
            .Replace('8', 'B')
            .Replace("MATERlAL", "MATERIAL", StringComparison.Ordinal)
            .Replace("MATER1AL", "MATERIAL", StringComparison.Ordinal)
            .Replace("HEPHASTANITE", "HEPHAESTANITE", StringComparison.Ordinal)
            .Replace("HEPHESTANITE", "HEPHAESTANITE", StringComparison.Ordinal)
            .Replace("HEPHAESTENITE", "HEPHAESTANITE", StringComparison.Ordinal)
            .Replace("ALUMINIUM", "ALUMINUM", StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("(", string.Empty, StringComparison.Ordinal)
            .Replace(")", string.Empty, StringComparison.Ordinal);
    }

    private static int GetMaximumDistance(int length)
    {
        return length switch
        {
            <= 5 => 1,
            <= 10 => 2,
            _ => 3
        };
    }

    private static int ComputeLevenshteinDistance(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return 0;
        }

        if (left.Length == 0)
        {
            return right.Length;
        }

        if (right.Length == 0)
        {
            return left.Length;
        }

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var column = 0; column <= right.Length; column++)
        {
            previous[column] = column;
        }

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var cost = left[row - 1] == right[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private sealed record MineableDefinition(string Material, string Category, string Key);
}