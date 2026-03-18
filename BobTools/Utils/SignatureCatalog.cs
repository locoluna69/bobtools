using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace BobTools.Utils;

internal static class SignatureCatalog
{
    private static readonly Regex NumericValuePattern = new(@"(?<!\d)(?:\d{1,3}(?:,\d{3})+|\d+)(?!\d)", RegexOptions.Compiled);
    private static readonly Regex NumericTokenPattern = new(@"\d[\d,\.]*", RegexOptions.Compiled);
    private const int MinimumSignatureDigits = 4;
    private static readonly Lazy<IReadOnlyList<SignatureDefinition>> Definitions = new(LoadDefinitions);

    public static IReadOnlyList<string> GetAvailableResources()
    {
        return Definitions.Value
            .Select(definition => definition.Resource)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(resource => resource, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string? FindMatchedResource(string? normalizedText)
    {
        if (!TryParseSignatureValue(normalizedText, out var observedValue))
        {
            return FindApproximateMatch(normalizedText)?.Resource;
        }

        return FindObservedValueMatch(observedValue)?.Resource
            ?? FindApproximateMatch(normalizedText)?.Resource;
    }

    public static string BuildResourceSummary(string? normalizedText)
    {
        if (!TryParseSignatureValue(normalizedText, out var observedValue))
        {
            var approximateOnlyMatch = FindApproximateMatch(normalizedText);
            return approximateOnlyMatch is null
                ? "Signature resource: no numeric value detected"
                : $"Signature resource: probable {approximateOnlyMatch.Resource} ({approximateOnlyMatch.Signature:N0} from OCR '{approximateOnlyMatch.ObservedText}')";
        }

        var bestMatch = FindObservedValueMatch(observedValue);
        if (bestMatch is null)
        {
            var approximateMatch = FindApproximateMatch(normalizedText);
            return approximateMatch is null
                ? $"Signature resource: no match for {observedValue:N0}"
                : $"Signature resource: probable {approximateMatch.Resource} ({approximateMatch.Signature:N0} from OCR '{approximateMatch.ObservedText}')";
        }

        if (bestMatch.IsCorrected)
        {
            return $"Signature resource: probable {bestMatch.Resource} ({bestMatch.Signature:N0} × {bestMatch.Multiplier}; OCR {observedValue:N0} -> {bestMatch.CorrectedValue:N0})";
        }

        var multiplier = bestMatch.Multiplier;

        return multiplier == 1
            ? $"Signature resource: {bestMatch.Resource} ({bestMatch.Signature:N0})"
            : $"Signature resource: {bestMatch.Resource} ({bestMatch.Signature:N0} × {multiplier})";
    }

    public static int GetMatchStrength(string? normalizedText)
    {
        if (TryParseSignatureValue(normalizedText, out var observedValue))
        {
            var observedMatch = FindObservedValueMatch(observedValue);
            if (observedMatch is not null)
            {
                return observedMatch.IsCorrected ? 2 : 3;
            }
        }

        var approximateMatch = FindApproximateMatch(normalizedText);
        if (approximateMatch is null)
        {
            return 0;
        }

        return approximateMatch.Distance switch
        {
            <= 0 => 3,
            1 => 2,
            _ => 1
        };
    }

    public static bool TryGetPreferredObservedValue(string? normalizedText, out int observedValue, out string formattedValue)
    {
        formattedValue = string.Empty;
        if (!TryParseSignatureValue(normalizedText, out observedValue))
        {
            return false;
        }

        formattedValue = observedValue.ToString("N0", CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryParseSignatureValue(string? normalizedText, out int value)
    {
        value = 0;

        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            return false;
        }

        var candidates = ExtractObservedValues(normalizedText);
        if (candidates.Count == 0)
        {
            return false;
        }

        value = candidates
            .OrderByDescending(GetObservedValuePriority)
            .ThenByDescending(candidate => candidate)
            .First();
        return true;
    }

    private static SignatureDefinition? FindBestMatch(int observedValue)
    {
        var matches = Definitions.Value
            .Where(definition => observedValue % definition.Signature == 0)
            .OrderByDescending(definition => definition.Signature)
            .ToList();

        if (matches.Count == 0)
        {
            return null;
        }

        var exactMatch = matches.FirstOrDefault(definition => definition.Signature == observedValue);
        return exactMatch ?? matches[0];
    }

    private static ObservedSignatureMatch? FindObservedValueMatch(int observedValue)
    {
        var exactMatch = FindBestMatch(observedValue);
        if (exactMatch is not null)
        {
            return new ObservedSignatureMatch(
                exactMatch.Resource,
                exactMatch.Signature,
                observedValue,
                observedValue / exactMatch.Signature,
                false);
        }

        return FindConfusableDigitObservedMatch(observedValue);
    }

    private static ObservedSignatureMatch? FindConfusableDigitObservedMatch(int observedValue)
    {
        if (Definitions.Value.Count == 0)
        {
            return null;
        }

        var observedDigits = observedValue.ToString(CultureInfo.InvariantCulture);
        var minimumSignature = Definitions.Value.Min(definition => definition.Signature);
        var maximumMultiplier = Math.Max(1, Math.Min(12, (observedValue / minimumSignature) + 2));
        ObservedSignatureMatch? bestMatch = null;
        var bestDistance = int.MaxValue;
        var secondBestDistance = int.MaxValue;

        foreach (var definition in Definitions.Value)
        {
            for (var multiplier = 1; multiplier <= maximumMultiplier; multiplier++)
            {
                var candidateValue = definition.Signature * multiplier;
                var candidateDigits = candidateValue.ToString(CultureInfo.InvariantCulture);
                if (candidateDigits.Length != observedDigits.Length)
                {
                    continue;
                }

                if (!TryGetConfusableDigitDistance(observedDigits, candidateDigits, out var distance))
                {
                    continue;
                }

                if (distance < bestDistance)
                {
                    secondBestDistance = bestDistance;
                    bestDistance = distance;
                    bestMatch = new ObservedSignatureMatch(definition.Resource, definition.Signature, candidateValue, multiplier, true);
                }
                else if (distance < secondBestDistance)
                {
                    secondBestDistance = distance;
                }
            }
        }

        if (bestMatch is null || bestDistance == int.MaxValue)
        {
            return null;
        }

        return secondBestDistance == bestDistance ? null : bestMatch;
    }

    private static bool TryGetConfusableDigitDistance(string observedDigits, string candidateDigits, out int distance)
    {
        distance = 0;

        if (observedDigits.Length != candidateDigits.Length)
        {
            return false;
        }

        var mismatchCount = 0;
        for (var index = 0; index < observedDigits.Length; index++)
        {
            var observedDigit = observedDigits[index];
            var candidateDigit = candidateDigits[index];
            if (observedDigit == candidateDigit)
            {
                continue;
            }

            if (!AreConfusableDigits(observedDigit, candidateDigit))
            {
                return false;
            }

            mismatchCount++;
            distance += GetConfusableDigitPenalty(observedDigit, candidateDigit);
            if (mismatchCount > 2 || distance > 2)
            {
                return false;
            }
        }

        return mismatchCount is >= 1 and <= 2;
    }

    private static IReadOnlyList<int> ExtractObservedValues(string normalizedText)
    {
        return NumericTokenPattern.Matches(normalizedText)
            .Select(match => match.Value)
            .Select(token => new string(token.Where(char.IsDigit).ToArray()))
            .Where(digits => digits.Length >= MinimumSignatureDigits)
            .Select(digits => int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedValue)
                ? parsedValue
                : (int?)null)
            .Where(parsedValue => parsedValue is not null)
            .Select(parsedValue => parsedValue!.Value)
            .Distinct()
            .ToArray();
    }

    private static int GetObservedValuePriority(int observedValue)
    {
        var match = FindObservedValueMatch(observedValue);
        if (match is not null)
        {
            return match.IsCorrected ? 2 : 3;
        }

        return FindBestMatch(observedValue) is null ? 0 : 1;
    }

    private static bool AreConfusableDigits(char observedDigit, char candidateDigit)
    {
        return (observedDigit, candidateDigit) switch
        {
            ('5', '6') or ('6', '5') => true,
            ('6', '8') or ('8', '6') => true,
            ('5', '8') or ('8', '5') => true,
            _ => false
        };
    }

    private static int GetConfusableDigitPenalty(char observedDigit, char candidateDigit)
    {
        return (observedDigit, candidateDigit) switch
        {
            ('5', '6') or ('6', '5') => 1,
            ('6', '8') or ('8', '6') => 1,
            ('5', '8') or ('8', '5') => 2,
            _ => 10
        };
    }

    private static ApproximateSignatureMatch? FindApproximateMatch(string? normalizedText)
    {
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            return null;
        }

        var candidates = NumericTokenPattern.Matches(normalizedText)
            .Select(match => match.Value)
            .Select(token => new
            {
                ObservedText = token,
                Digits = new string(token.Where(char.IsDigit).ToArray())
            })
            .Where(candidate => candidate.Digits.Length >= 3)
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        ApproximateSignatureMatch? bestMatch = null;
        var bestRank = int.MaxValue;
        var secondBestRank = int.MaxValue;

        foreach (var candidate in candidates)
        {
            foreach (var definition in Definitions.Value)
            {
                var signatureDigits = definition.Signature.ToString(CultureInfo.InvariantCulture);
                var distance = CalculateApproximateDistance(candidate.Digits, signatureDigits);
                if (distance > 1)
                {
                    continue;
                }

                var rank = (distance * 100)
                    + Math.Abs(signatureDigits.Length - candidate.Digits.Length) * 10
                    - candidate.Digits.Length;

                if (rank < bestRank)
                {
                    secondBestRank = bestRank;
                    bestRank = rank;
                    bestMatch = new ApproximateSignatureMatch(definition.Resource, definition.Signature, candidate.ObservedText, distance);
                }
                else if (rank < secondBestRank)
                {
                    secondBestRank = rank;
                }
            }
        }

        if (bestMatch is null)
        {
            return null;
        }

        return secondBestRank == bestRank ? null : bestMatch;
    }

    private static int CalculateApproximateDistance(string observedDigits, string signatureDigits)
    {
        if (string.Equals(observedDigits, signatureDigits, StringComparison.Ordinal))
        {
            return 0;
        }

        if (signatureDigits.Length == observedDigits.Length + 1
            && signatureDigits.EndsWith(observedDigits, StringComparison.Ordinal))
        {
            return 1;
        }

        if (observedDigits.Length == signatureDigits.Length + 1
            && observedDigits.EndsWith(signatureDigits, StringComparison.Ordinal))
        {
            return 1;
        }

        return CalculateLevenshteinDistance(observedDigits, signatureDigits);
    }

    private static int CalculateLevenshteinDistance(string left, string right)
    {
        var distances = new int[left.Length + 1, right.Length + 1];

        for (var index = 0; index <= left.Length; index++)
        {
            distances[index, 0] = index;
        }

        for (var index = 0; index <= right.Length; index++)
        {
            distances[0, index] = index;
        }

        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var cost = left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1;
                distances[leftIndex, rightIndex] = Math.Min(
                    Math.Min(
                        distances[leftIndex - 1, rightIndex] + 1,
                        distances[leftIndex, rightIndex - 1] + 1),
                    distances[leftIndex - 1, rightIndex - 1] + cost);
            }
        }

        return distances[left.Length, right.Length];
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
    private sealed record ObservedSignatureMatch(string Resource, int Signature, int CorrectedValue, int Multiplier, bool IsCorrected);
    private sealed record ApproximateSignatureMatch(string Resource, int Signature, string ObservedText, int Distance);
}