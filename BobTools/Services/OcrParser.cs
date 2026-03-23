using System.Text.RegularExpressions;
using BobTools.Models;
using BobTools.Utils;

namespace BobTools.Services;

public sealed record OcrAttempt(string Name, string RawText, string NormalizedText, float Confidence);
public sealed record ScoredOcrAttempt(OcrAttempt Attempt, double TotalScore, double BaseScore, MiningAttemptAnalysis MiningAnalysis);
public sealed record MiningAttemptAnalysis(double ScoreAdjustment, string Notes, RockScan? ParsedScan);

public static class OcrParser
{
    private static readonly Regex StrictNumericValuePattern = new(@"(?<!\d)\d{1,3}(?:,\d{3})+(?!\d)", RegexOptions.Compiled);

    public static OcrAttempt SelectBestAttempt(IEnumerable<ScoredOcrAttempt> attempts, string? preferredMode)
    {
        if (!string.IsNullOrWhiteSpace(preferredMode)
            && !string.Equals(preferredMode, OcrModeCatalog.Auto, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(preferredMode, OcrModeCatalog.RightmostNumericValue, StringComparison.OrdinalIgnoreCase))
        {
            var forced = attempts.FirstOrDefault(attempt => string.Equals(attempt.Attempt.Name, preferredMode, StringComparison.OrdinalIgnoreCase));
            if (forced is not null)
            {
                return forced.Attempt;
            }
        }

        return attempts
            .OrderByDescending(attempt => attempt.TotalScore)
            .ThenByDescending(attempt => attempt.Attempt.Confidence)
            .First()
            .Attempt;
    }

    public static ScoredOcrAttempt EvaluateAttempt(OcrAttempt attempt, string? captureProfile)
    {
        var baseScore = ScoreAttempt(attempt);
        var signatureAdjustment = AnalyzeSignatureAttempt(attempt, captureProfile);
        var miningAnalysis = AnalyzeMiningAttempt(attempt, captureProfile);

        return new ScoredOcrAttempt(
            attempt,
            baseScore + signatureAdjustment + miningAnalysis.ScoreAdjustment,
            baseScore,
            miningAnalysis);
    }

    private static double ScoreAttempt(OcrAttempt attempt)
    {
        var nonWhitespaceCharacters = attempt.NormalizedText.Count(character => !char.IsWhiteSpace(character));
        var lineCount = attempt.NormalizedText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Length;

        var grayscaleBonus = attempt.Name.StartsWith("grayscale", StringComparison.OrdinalIgnoreCase) ? 8d : 0d;
        var minusTwentyBias = attempt.Name.Contains("minus20", StringComparison.OrdinalIgnoreCase) ? 10d : 0d;
        var nearMinusTwentyBias = attempt.Name.Contains("minus15", StringComparison.OrdinalIgnoreCase) || attempt.Name.Contains("minus25", StringComparison.OrdinalIgnoreCase) ? 6d : 0d;
        return attempt.Confidence * 100d + nonWhitespaceCharacters + (lineCount * 25d) + grayscaleBonus + minusTwentyBias + nearMinusTwentyBias;
    }

    private static double AnalyzeSignatureAttempt(OcrAttempt attempt, string? captureProfile)
    {
        if (!string.Equals(captureProfile, CaptureProfileCatalog.Signature, StringComparison.OrdinalIgnoreCase))
        {
            return 0d;
        }

        var scoreAdjustment = 0d;
        var lines = attempt.NormalizedText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var digitCount = attempt.NormalizedText.Count(char.IsDigit);
        var letterCount = attempt.NormalizedText.Count(char.IsLetter);

        if (SignatureCatalog.TryGetPreferredObservedValue(attempt.NormalizedText, out _, out var formattedValue))
        {
            scoreAdjustment += SignatureCatalog.GetMatchStrength(attempt.NormalizedText) switch
            {
                3 => 170d,
                2 => 120d,
                1 => 70d,
                _ => 45d
            };

            scoreAdjustment += formattedValue.Contains(',', StringComparison.Ordinal) ? 30d : 0d;
            scoreAdjustment += formattedValue.Length >= 5 ? 25d : 0d;
        }

        if (lines.Length > 2)
        {
            scoreAdjustment -= Math.Min(120d, (lines.Length - 2) * 18d);
        }

        if (letterCount > digitCount)
        {
            scoreAdjustment -= Math.Min(90d, (letterCount - digitCount) * 4d);
        }

        if (digitCount == 0)
        {
            scoreAdjustment -= 60d;
        }

        return scoreAdjustment;
    }

    private static MiningAttemptAnalysis AnalyzeMiningAttempt(OcrAttempt attempt, string? captureProfile)
    {
        if (!string.Equals(captureProfile, CaptureProfileCatalog.Mining, StringComparison.OrdinalIgnoreCase))
        {
            return new MiningAttemptAnalysis(0d, string.Empty, null);
        }

        var scoreAdjustment = 0d;
        var notes = new List<string>();
        var uppercaseText = attempt.NormalizedText.ToUpperInvariant();

        if (RockScanParser.TryParse(attempt.NormalizedText, out var rockScan))
        {
            scoreAdjustment += 30d;
            notes.Add("parsed");

            if (!string.IsNullOrWhiteSpace(rockScan.Material))
            {
                scoreAdjustment += 60d;
                notes.Add("material");
            }

            if (rockScan.Mass is not null)
            {
                scoreAdjustment += 45d;
                notes.Add("mass");
            }

            if (rockScan.Resistance is not null)
            {
                scoreAdjustment += 30d;
                notes.Add("resistance");
            }

            if (rockScan.Instability is not null)
            {
                scoreAdjustment += 20d;
                notes.Add("instability");
            }

            if (rockScan.Composition is not null)
            {
                scoreAdjustment += 40d;
                notes.Add("composition");
            }

            if (rockScan.Elements.Count > 0)
            {
                scoreAdjustment += 40d + (Math.Min(rockScan.Elements.Count, 6) * 20d);
                notes.Add($"elements={rockScan.Elements.Count}");
            }

            var completeElementCount = rockScan.Elements.Count(entry =>
                !string.IsNullOrWhiteSpace(entry.Element)
                && entry.Percentage is not null
                && entry.Quality is not null);

            if (completeElementCount > 0)
            {
                scoreAdjustment += completeElementCount * 12d;
                notes.Add($"complete-elements={completeElementCount}");
            }

            var incompleteElementCount = rockScan.Elements.Count - completeElementCount;
            if (incompleteElementCount > 0)
            {
                scoreAdjustment -= Math.Min(36d, incompleteElementCount * 8d);
                notes.Add($"incomplete-elements={incompleteElementCount}");
            }

            var parsedElementPercentages = rockScan.Elements
                .Where(entry => entry.Percentage is not null)
                .Select(entry => entry.Percentage!.Value)
                .ToArray();

            if (rockScan.Composition is not null && parsedElementPercentages.Length > 0)
            {
                var totalElementPercentage = parsedElementPercentages.Sum();
                var delta = Math.Abs(totalElementPercentage - rockScan.Composition.Value);

                if (delta <= 1.5m)
                {
                    scoreAdjustment += 35d;
                    notes.Add("composition-match");
                }
                else if (delta <= 5m)
                {
                    scoreAdjustment += 15d;
                    notes.Add("composition-near");
                }
                else if (delta >= 20m)
                {
                    scoreAdjustment -= 20d;
                    notes.Add("composition-mismatch");
                }
            }

            if (rockScan.ParseWarnings.Count > 0)
            {
                scoreAdjustment -= Math.Min(75d, rockScan.ParseWarnings.Count * 15d);
                notes.Add($"warnings={rockScan.ParseWarnings.Count}");
            }

            return new MiningAttemptAnalysis(scoreAdjustment, string.Join(", ", notes), rockScan);
        }

        if (uppercaseText.Contains("MASS", StringComparison.Ordinal))
        {
            scoreAdjustment += 10d;
            notes.Add("mass-label");
        }

        if (uppercaseText.Contains("RESISTANCE", StringComparison.Ordinal))
        {
            scoreAdjustment += 10d;
            notes.Add("resistance-label");
        }

        if (uppercaseText.Contains("INSTABILITY", StringComparison.Ordinal))
        {
            scoreAdjustment += 10d;
            notes.Add("instability-label");
        }

        if (uppercaseText.Contains("COMPOSITION", StringComparison.Ordinal))
        {
            scoreAdjustment += 14d;
            notes.Add("composition-label");
        }

        var percentCount = attempt.NormalizedText.Count(character => character == '%');
        if (percentCount >= 2)
        {
            scoreAdjustment += Math.Min(24d, percentCount * 4d);
            notes.Add($"percent-signs={percentCount}");
        }

        return new MiningAttemptAnalysis(scoreAdjustment, string.Join(", ", notes), null);
    }

    public static OcrResult BuildResult(OcrAttempt best, string? preferredMode, string? captureProfile)
    {
        if (string.Equals(preferredMode, OcrModeCatalog.RightmostNumericValue, StringComparison.OrdinalIgnoreCase))
        {
            var extractedNumericValue = ExtractRightmostNumericValue(best.NormalizedText);
            return new OcrResult
            {
                RawText = best.RawText,
                NormalizedText = extractedNumericValue,
                Confidence = best.Confidence,
                UsedMode = $"{OcrModeCatalog.RightmostNumericValue}|{best.Name}"
            };
        }

        if (string.Equals(captureProfile, CaptureProfileCatalog.Signature, StringComparison.OrdinalIgnoreCase)
            && SignatureCatalog.TryGetPreferredObservedValue(best.NormalizedText, out _, out var formattedObservedValue))
        {
            return new OcrResult
            {
                RawText = best.RawText,
                NormalizedText = formattedObservedValue,
                Confidence = best.Confidence,
                UsedMode = best.Name
            };
        }

        return new OcrResult
        {
            RawText = best.RawText,
            NormalizedText = best.NormalizedText,
            Confidence = best.Confidence,
            UsedMode = best.Name
        };
    }

    private static string ExtractRightmostNumericValue(string text)
    {
        var filteredLines = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => StrictNumericValuePattern.Matches(line)
                .Select(match => match.Value)
                .ToArray())
            .Where(matches => matches.Length > 0)
            .Select(matches => matches[^1])
            .ToArray();

        if (filteredLines.Length == 0)
        {
            return string.Empty;
        }

        return string.Join(Environment.NewLine, filteredLines);
    }
}
