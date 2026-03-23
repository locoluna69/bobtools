using System.Drawing;
using BobTools.Models;

namespace BobTools.Utils;

internal sealed record MiningOcrCandidate(
    string Strategy,
    string UsedMode,
    string NormalizedText,
    RockScan ParsedScan,
    int ParseScore,
    int PopulatedFieldCount,
    int NonEmptyLineCount,
    int RowCount);

internal static class MiningOcrSegmentation
{
    private const byte DarkPixelThreshold = 208;
    private const double ActiveRowRatioThreshold = 0.04d;
    private const int MaximumBlankGap = 4;
    private const int MinimumBandHeight = 6;
    private const int VerticalPadding = 2;

    public static IReadOnlyList<Rectangle> DetectRowBounds(Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        if (bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            return [];
        }

        var sampleStride = Math.Max(1, bitmap.Width / 320);
        var activeRows = new bool[bitmap.Height];
        var minimumDarkPixels = Math.Max(3, (int)Math.Ceiling((bitmap.Width / (double)sampleStride) * ActiveRowRatioThreshold));

        for (var y = 0; y < bitmap.Height; y++)
        {
            var darkPixels = 0;
            for (var x = 0; x < bitmap.Width; x += sampleStride)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.R <= DarkPixelThreshold || pixel.G <= DarkPixelThreshold || pixel.B <= DarkPixelThreshold)
                {
                    darkPixels++;
                }
            }

            activeRows[y] = darkPixels >= minimumDarkPixels;
        }

        var rowBounds = new List<Rectangle>();
        int? bandStart = null;
        var blankGap = 0;

        for (var y = 0; y < activeRows.Length; y++)
        {
            if (activeRows[y])
            {
                bandStart ??= y;
                blankGap = 0;
                continue;
            }

            if (bandStart is null)
            {
                continue;
            }

            blankGap++;
            if (blankGap <= MaximumBlankGap)
            {
                continue;
            }

            AddRowBound(rowBounds, bitmap.Width, bitmap.Height, bandStart.Value, y - blankGap);
            bandStart = null;
            blankGap = 0;
        }

        if (bandStart is not null)
        {
            AddRowBound(rowBounds, bitmap.Width, bitmap.Height, bandStart.Value, bitmap.Height - 1);
        }

        return rowBounds;
    }

    public static string CombineRowTexts(IEnumerable<string?> rowTexts)
    {
        ArgumentNullException.ThrowIfNull(rowTexts);

        return string.Join(
            Environment.NewLine,
            rowTexts
                .Select(text => text?.Trim())
                .Where(text => !string.IsNullOrWhiteSpace(text))!);
    }

    public static MiningOcrCandidate CreateCandidate(string strategy, string usedMode, string normalizedText, int rowCount)
    {
        RockScanParser.TryParse(normalizedText, out var parsedScan);
        return new MiningOcrCandidate(
            strategy,
            string.IsNullOrWhiteSpace(usedMode) ? OcrModeCatalog.Auto : usedMode,
            normalizedText ?? string.Empty,
            parsedScan,
            ScoreParsedScan(parsedScan),
            CountPopulatedFields(parsedScan),
            CountNonEmptyLines(normalizedText),
            Math.Max(0, rowCount));
    }

    public static MiningOcrCandidate SelectPreferredCandidate(IEnumerable<MiningOcrCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        return candidates
            .OrderByDescending(candidate => candidate.ParseScore)
            .ThenByDescending(candidate => candidate.PopulatedFieldCount)
            .ThenByDescending(candidate => candidate.NonEmptyLineCount)
            .ThenByDescending(candidate => candidate.NormalizedText.Length)
            .ThenBy(candidate => GetTieBreakPriority(candidate.Strategy))
            .First();
    }

    private static int CountNonEmptyLines(string? normalizedText)
    {
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            return 0;
        }

        return normalizedText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Count(line => !string.IsNullOrWhiteSpace(line));
    }

    private static int CountPopulatedFields(RockScan? rockScan)
    {
        if (rockScan is null)
        {
            return 0;
        }

        var count = 0;
        if (!string.IsNullOrWhiteSpace(rockScan.Material))
        {
            count++;
        }

        if (rockScan.Mass is not null)
        {
            count++;
        }

        if (rockScan.Resistance is not null)
        {
            count++;
        }

        if (rockScan.Instability is not null)
        {
            count++;
        }

        if (rockScan.Composition is not null)
        {
            count++;
        }

        count += rockScan.Elements.Count(entry => !string.IsNullOrWhiteSpace(entry.Element));
        count += rockScan.Elements.Count(entry => entry.Percentage is not null);
        count += rockScan.Elements.Count(entry => entry.Quality is not null);
        return count;
    }

    private static int ScoreParsedScan(RockScan? rockScan)
    {
        if (rockScan is null)
        {
            return 0;
        }

        var score = 0;

        if (!string.IsNullOrWhiteSpace(rockScan.Material))
        {
            score += 5;
        }

        if (rockScan.Mass is not null)
        {
            score += 3;
        }

        if (rockScan.Resistance is not null)
        {
            score += 3;
        }

        if (rockScan.Instability is not null)
        {
            score += 2;
        }

        if (rockScan.Composition is not null)
        {
            score += 3;
        }

        foreach (var entry in rockScan.Elements.Take(6))
        {
            if (!string.IsNullOrWhiteSpace(entry.Element))
            {
                score += 2;
            }

            if (entry.Percentage is not null)
            {
                score += 2;
            }

            if (entry.Quality is not null)
            {
                score += 1;
            }
        }

        score -= rockScan.ParseWarnings.Count;
        return Math.Max(0, score);
    }

    private static int GetTieBreakPriority(string? strategy)
    {
        return strategy switch
        {
            MiningOcrStrategyCatalog.WholeRegion => 0,
            MiningOcrStrategyCatalog.RowSegmented => 1,
            _ => 2
        };
    }

    private static void AddRowBound(ICollection<Rectangle> rowBounds, int width, int height, int startY, int endY)
    {
        if (endY < startY)
        {
            return;
        }

        var top = Math.Max(0, startY - VerticalPadding);
        var bottom = Math.Min(height - 1, endY + VerticalPadding);
        var bandHeight = (bottom - top) + 1;
        if (bandHeight < MinimumBandHeight)
        {
            return;
        }

        rowBounds.Add(new Rectangle(0, top, width, bandHeight));
    }
}