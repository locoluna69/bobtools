using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.RegularExpressions;
using System.Text;
using StarCitizenOverlayStarter.Models;
using StarCitizenOverlayStarter.Utils;
using Tesseract;

namespace StarCitizenOverlayStarter.Services;

public sealed class OcrService : IOcrService
{
    private const int UpscaleFactor = 2;
    private static readonly Regex StrictNumericValuePattern = new(@"(?<!\d)\d{1,3}(?:,\d{3})+(?!\d)", RegexOptions.Compiled);
    private static readonly Regex SignatureNumericValuePattern = new(@"(?<!\d)(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d+)?(?!\d)", RegexOptions.Compiled);
    private readonly string _tessDataPath;
    private readonly string _signatureIconPath;

    public OcrService(string tessDataPath)
    {
        _tessDataPath = tessDataPath;
        _signatureIconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "images", "signature_icon.png");
    }

    public Task<OcrResult> ExtractTextAsync(Bitmap bitmap, string? debugOutputDirectory = null, string? preferredMode = null, string? captureProfile = null)
    {
        return Task.Run(() =>
        {
            if (ShouldUseSignatureIconExtraction(captureProfile, preferredMode)
                && TryExtractSignatureValue(bitmap, debugOutputDirectory, out var signatureResult))
            {
                return signatureResult;
            }

            using var engine = CreateEngine();
            using var upscaled = Upscale(bitmap, UpscaleFactor);
            using var grayscale = ToGrayscale(upscaled);
            using var enhancedGrayscale = StretchContrast(grayscale);
            var thresholdVariants = CreateThresholdVariants(grayscale, enhancedGrayscale);
            try
            {
                if (!string.IsNullOrWhiteSpace(debugOutputDirectory))
                {
                    SaveBitmap(grayscale, Path.Combine(debugOutputDirectory, "02_grayscale.png"));
                    SaveBitmap(enhancedGrayscale, Path.Combine(debugOutputDirectory, "03_grayscale_contrast.png"));

                    for (var index = 0; index < thresholdVariants.Count; index++)
                    {
                        SaveBitmap(
                            thresholdVariants[index].Bitmap,
                            Path.Combine(debugOutputDirectory, $"{index + 4:D2}_{thresholdVariants[index].Name}.png"));
                    }
                }

                var attempts = new List<OcrAttempt>
                {
                    RunAttempt(engine, grayscale, PageSegMode.Auto, "grayscale-auto"),
                    RunAttempt(engine, grayscale, PageSegMode.SingleBlock, "grayscale-block"),
                    RunAttempt(engine, grayscale, PageSegMode.SparseText, "grayscale-sparse"),
                    RunAttempt(engine, enhancedGrayscale, PageSegMode.Auto, "grayscale-contrast-auto"),
                    RunAttempt(engine, enhancedGrayscale, PageSegMode.SingleBlock, "grayscale-contrast-block"),
                    RunAttempt(engine, enhancedGrayscale, PageSegMode.SparseText, "grayscale-contrast-sparse")
                };

                foreach (var variant in thresholdVariants)
                {
                    attempts.Add(RunAttempt(engine, variant.Bitmap, PageSegMode.Auto, $"{variant.Name}-auto"));
                    attempts.Add(RunAttempt(engine, variant.Bitmap, PageSegMode.SingleBlock, $"{variant.Name}-block"));
                    attempts.Add(RunAttempt(engine, variant.Bitmap, PageSegMode.SparseText, $"{variant.Name}-sparse"));
                }

                var scoredAttempts = attempts
                    .Select(attempt => EvaluateAttempt(attempt, captureProfile))
                    .ToList();

                var best = SelectBestAttempt(scoredAttempts, preferredMode);
                var result = BuildResult(best, preferredMode);

                if (!string.IsNullOrWhiteSpace(debugOutputDirectory))
                {
                    SaveDebugSummary(debugOutputDirectory, scoredAttempts, result, preferredMode);
                }

                return result;
            }
            finally
            {
                foreach (var variant in thresholdVariants)
                {
                    variant.Bitmap.Dispose();
                }
            }
        });
    }

    private TesseractEngine CreateEngine(string? characterWhitelist = null)
    {
        var engine = new TesseractEngine(_tessDataPath, "eng", EngineMode.LstmOnly);
        engine.SetVariable("preserve_interword_spaces", "1");
        engine.SetVariable("user_defined_dpi", "300");

        if (!string.IsNullOrWhiteSpace(characterWhitelist))
        {
            engine.SetVariable("tessedit_char_whitelist", characterWhitelist);
        }

        return engine;
    }

    private bool ShouldUseSignatureIconExtraction(string? captureProfile, string? preferredMode)
    {
        if (!string.Equals(captureProfile, CaptureProfileCatalog.Signature, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(preferredMode)
            || string.Equals(preferredMode, OcrModeCatalog.Auto, StringComparison.OrdinalIgnoreCase)
            || string.Equals(preferredMode, OcrModeCatalog.RightmostNumericValue, StringComparison.OrdinalIgnoreCase);
    }

    private bool TryExtractSignatureValue(Bitmap bitmap, string? debugOutputDirectory, out OcrResult result)
    {
        result = new OcrResult();

        if (!File.Exists(_signatureIconPath))
        {
            return false;
        }

        using var rawIconBitmap = new Bitmap(_signatureIconPath);
        using var iconBitmap = TrimTransparentBorders(rawIconBitmap);
        if (!TryLocateTemplate(bitmap, iconBitmap, out var iconBounds, out var matchScore))
        {
            return false;
        }

        using var numericEngine = CreateEngine("0123456789,.");
        var candidateAttempts = new List<SignatureNumericAttempt>();
        var candidateRegions = BuildSignatureValueRegions(bitmap.Size, iconBounds);

        for (var regionIndex = 0; regionIndex < candidateRegions.Count; regionIndex++)
        {
            var valueRegion = candidateRegions[regionIndex];
            if (valueRegion.Width < 8 || valueRegion.Height < 8)
            {
                continue;
            }

            using var valueBitmap = CropBitmap(bitmap, valueRegion);
            using var upscaled = Upscale(valueBitmap, UpscaleFactor);
            using var grayscale = ToGrayscale(upscaled);
            using var enhancedGrayscale = StretchContrast(grayscale);
            var thresholdVariants = CreateThresholdVariants(grayscale, enhancedGrayscale);

            try
            {
                if (!string.IsNullOrWhiteSpace(debugOutputDirectory) && regionIndex == 0)
                {
                    SaveBitmap(valueBitmap, Path.Combine(debugOutputDirectory, "00_signature_value_region.png"));
                    SaveBitmap(grayscale, Path.Combine(debugOutputDirectory, "00_signature_value_grayscale.png"));
                    SaveBitmap(enhancedGrayscale, Path.Combine(debugOutputDirectory, "00_signature_value_contrast.png"));
                }

                var attempts = new List<OcrAttempt>
                {
                    RunAttempt(numericEngine, grayscale, PageSegMode.SingleLine, $"signature-icon-r{regionIndex}-grayscale-line"),
                    RunAttempt(numericEngine, grayscale, PageSegMode.SingleWord, $"signature-icon-r{regionIndex}-grayscale-word"),
                    RunAttempt(numericEngine, enhancedGrayscale, PageSegMode.SingleLine, $"signature-icon-r{regionIndex}-contrast-line"),
                    RunAttempt(numericEngine, enhancedGrayscale, PageSegMode.SingleWord, $"signature-icon-r{regionIndex}-contrast-word")
                };

                foreach (var variant in thresholdVariants)
                {
                    attempts.Add(RunAttempt(numericEngine, variant.Bitmap, PageSegMode.SingleLine, $"signature-icon-r{regionIndex}-{variant.Name}-line"));
                    attempts.Add(RunAttempt(numericEngine, variant.Bitmap, PageSegMode.SingleWord, $"signature-icon-r{regionIndex}-{variant.Name}-word"));
                }

                candidateAttempts.AddRange(attempts
                    .Select(attempt => new SignatureNumericAttempt(attempt, ExtractSignatureNumericValue(attempt.NormalizedText), valueRegion))
                    .Where(candidate => !string.IsNullOrWhiteSpace(candidate.ExtractedValue)));
            }
            finally
            {
                foreach (var variant in thresholdVariants)
                {
                    variant.Bitmap.Dispose();
                }
            }
        }

        var bestAttempt = SelectBestSignatureNumericAttempt(candidateAttempts);
        if (bestAttempt is null)
        {
            return false;
        }

        result = new OcrResult
        {
            RawText = bestAttempt.Attempt.RawText,
            NormalizedText = bestAttempt.ExtractedValue,
            Confidence = bestAttempt.Attempt.Confidence,
            UsedMode = $"signature-icon-value|{bestAttempt.Attempt.Name}"
        };

        if (!string.IsNullOrWhiteSpace(debugOutputDirectory))
        {
            var candidateSummary = string.Join(
                Environment.NewLine,
                candidateAttempts
                    .OrderByDescending(ScoreSignatureNumericAttempt)
                    .Take(20)
                    .Select(candidate => $"{candidate.ExtractedValue} | score={ScoreSignatureNumericAttempt(candidate):F2} | confidence={candidate.Attempt.Confidence:F4} | region={candidate.Region} | mode={candidate.Attempt.Name}"));

            File.WriteAllText(
                Path.Combine(debugOutputDirectory, "00_signature_icon_match.txt"),
                $"Icon bounds: {iconBounds}\nMatch score: {matchScore:F4}\nSelected value region: {bestAttempt.Region}\nSelected value: {result.NormalizedText}\nUsed mode: {result.UsedMode}\nCandidate count: {candidateAttempts.Count}\n\nTop candidates:\n{candidateSummary}");
        }

        return true;
    }

    private static OcrAttempt RunAttempt(TesseractEngine engine, Bitmap bitmap, PageSegMode pageSegMode, string name)
    {
        using var pix = Pix.LoadFromMemory(BitmapToBytes(bitmap));
        using var page = engine.Process(pix, pageSegMode);

        var raw = page.GetText() ?? string.Empty;
        var normalized = raw.Replace("\r", string.Empty).Trim();

        return new OcrAttempt(
            name,
            raw,
            normalized,
            page.GetMeanConfidence());
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

    private static ScoredOcrAttempt EvaluateAttempt(OcrAttempt attempt, string? captureProfile)
    {
        var baseScore = ScoreAttempt(attempt);
        var miningAnalysis = AnalyzeMiningAttempt(attempt, captureProfile);

        return new ScoredOcrAttempt(
            attempt,
            baseScore + miningAnalysis.ScoreAdjustment,
            baseScore,
            miningAnalysis);
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

    private static OcrAttempt SelectBestAttempt(IEnumerable<ScoredOcrAttempt> attempts, string? preferredMode)
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

    private static OcrResult BuildResult(OcrAttempt best, string? preferredMode)
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

        return new OcrResult
        {
            RawText = best.RawText,
            NormalizedText = best.NormalizedText,
            Confidence = best.Confidence,
            UsedMode = best.Name
        };
    }

    private static string ExtractSignatureNumericValue(string text)
    {
        var values = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => SignatureNumericValuePattern.Matches(line)
                .Select(match => match.Value)
                .ToArray())
            .Where(matches => matches.Length > 0)
            .Select(matches => matches[^1])
            .ToArray();

        if (values.Length == 0)
        {
            return string.Empty;
        }

        return string.Join(Environment.NewLine, values);
    }

    private static double ScoreSignatureNumericAttempt(SignatureNumericAttempt attempt)
    {
        var score = attempt.Attempt.Confidence * 100d;
        score += attempt.ExtractedValue.Length * 18d;

        if (attempt.ExtractedValue.Contains(',', StringComparison.Ordinal))
        {
            score += 40d;
        }

        if (StrictNumericValuePattern.IsMatch(attempt.ExtractedValue))
        {
            score += 35d;
        }

        if (attempt.ExtractedValue.Length == 1)
        {
            score -= 65d;
        }

        if (attempt.Attempt.Name.Contains("line", StringComparison.OrdinalIgnoreCase))
        {
            score += 12d;
        }

        return score;
    }

    private static SignatureNumericAttempt? SelectBestSignatureNumericAttempt(IReadOnlyCollection<SignatureNumericAttempt> attempts)
    {
        if (attempts.Count == 0)
        {
            return null;
        }

        var candidates = attempts.AsEnumerable();
        if (candidates.Any(candidate => StrictNumericValuePattern.IsMatch(candidate.ExtractedValue)))
        {
            candidates = candidates.Where(candidate => StrictNumericValuePattern.IsMatch(candidate.ExtractedValue));
        }
        else if (candidates.Any(candidate => candidate.ExtractedValue.Contains(',', StringComparison.Ordinal)))
        {
            candidates = candidates.Where(candidate => candidate.ExtractedValue.Contains(',', StringComparison.Ordinal));
        }

        var groupedCandidates = candidates
            .GroupBy(candidate => candidate.ExtractedValue, StringComparer.Ordinal)
            .Select(group => new SignatureNumericGroup(
                group.Key,
                group.Count(),
                group.Max(ScoreSignatureNumericAttempt),
                group.Sum(ScoreSignatureNumericAttempt),
                group.OrderByDescending(ScoreSignatureNumericAttempt).First()))
            .OrderByDescending(group => group.VoteCount)
            .ThenByDescending(group => group.BestScore)
            .ThenByDescending(group => group.TotalScore)
            .ThenByDescending(group => ParseNumericValue(group.Value))
            .ToList();

        return groupedCandidates.FirstOrDefault()?.BestAttempt;
    }

    private static List<Rectangle> BuildSignatureValueRegions(Size imageSize, Rectangle iconBounds)
    {
        var regions = new List<Rectangle>();
        var startOffsets = new[]
        {
            -iconBounds.Width,
            -iconBounds.Width * 3 / 4,
            -iconBounds.Width / 2,
            -iconBounds.Width / 4,
            -iconBounds.Width / 10,
            0,
            iconBounds.Width / 10,
            iconBounds.Width / 5
        };
        var topOffsets = new[] { -iconBounds.Height / 4, -iconBounds.Height / 8, 0 };
        var heights = new[]
        {
            iconBounds.Height + Math.Max(4, iconBounds.Height / 2),
            iconBounds.Height + Math.Max(8, iconBounds.Height * 3 / 4),
            iconBounds.Height * 2
        };
        var widths = new[]
        {
            Math.Max(iconBounds.Width * 2, 48),
            Math.Max(iconBounds.Width * 3, 72),
            Math.Max(iconBounds.Width * 4, 96),
            Math.Max(imageSize.Width - iconBounds.Right, 0)
        };

        foreach (var startOffset in startOffsets)
        {
            foreach (var topOffset in topOffsets)
            {
                foreach (var height in heights)
                {
                    foreach (var width in widths)
                    {
                        var x = Math.Max(0, Math.Min(imageSize.Width - 1, iconBounds.Right + startOffset));
                        var y = Math.Max(0, iconBounds.Top + topOffset);
                        var clampedWidth = Math.Min(width, imageSize.Width - x);
                        var clampedHeight = Math.Min(height, imageSize.Height - y);

                        if (clampedWidth < 8 || clampedHeight < 8)
                        {
                            continue;
                        }

                        var region = new Rectangle(x, y, clampedWidth, clampedHeight);
                        if (!regions.Contains(region))
                        {
                            regions.Add(region);
                        }
                    }
                }
            }
        }

        return regions;
    }

    private static decimal ParseNumericValue(string value)
    {
        var sanitized = value.Replace(",", string.Empty, StringComparison.Ordinal).Trim();
        return decimal.TryParse(sanitized, out var parsed) ? parsed : decimal.MinValue;
    }

    private static Bitmap CropBitmap(Bitmap source, Rectangle bounds)
    {
        var output = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(output);
        graphics.DrawImage(source, new Rectangle(0, 0, bounds.Width, bounds.Height), bounds, GraphicsUnit.Pixel);
        return output;
    }

    private static bool TryLocateTemplate(Bitmap source, Bitmap template, out Rectangle bestBounds, out double bestScore)
    {
        bestBounds = Rectangle.Empty;
        bestScore = double.MinValue;

        var sourceGray = ToGrayMatrix(source);
        var scales = BuildTemplateScales(source.Size, template.Size);

        foreach (var scale in scales)
        {
            using var scaledTemplate = ResizeTemplate(template, scale);
            if (scaledTemplate.Width >= source.Width || scaledTemplate.Height >= source.Height)
            {
                continue;
            }

            var scaledTemplateGray = ToGrayMatrix(scaledTemplate);
            var scaledTemplateMask = ToAlphaMask(scaledTemplate);
            var step = Math.Max(1, Math.Min(scaledTemplate.Width, scaledTemplate.Height) / 18);

            for (var y = 0; y <= source.Height - scaledTemplate.Height; y += Math.Max(1, step / 2))
            {
                for (var x = 0; x <= source.Width - scaledTemplate.Width; x += Math.Max(1, step / 2))
                {
                    var score = CalculateTemplateScore(sourceGray, scaledTemplateGray, scaledTemplateMask, x, y, step);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestBounds = new Rectangle(x, y, scaledTemplate.Width, scaledTemplate.Height);
                    }
                }
            }
        }

        return bestBounds != Rectangle.Empty;
    }

    private static IReadOnlyList<double> BuildTemplateScales(Size sourceSize, Size templateSize)
    {
        var maxWidthScale = (double)sourceSize.Width / Math.Max(1, templateSize.Width);
        var maxHeightScale = (double)sourceSize.Height / Math.Max(1, templateSize.Height);
        var maxScale = Math.Min(Math.Min(maxWidthScale, maxHeightScale), 3.25d);
        var coarseUpperBound = Math.Max(1.50d, Math.Min(maxScale, 2.50d));
        var scales = new HashSet<double>();

        for (double scale = 0.45d; scale <= coarseUpperBound; scale += 0.20d)
        {
            scales.Add(Math.Round(scale, 2));
        }

        if (maxScale > coarseUpperBound)
        {
            for (double scale = coarseUpperBound + 0.15d; scale <= maxScale; scale += 0.15d)
            {
                scales.Add(Math.Round(scale, 2));
            }
        }

        scales.Add(1.00d);

        return scales
            .OrderBy(scale => scale)
            .ToArray();
    }

    private static Bitmap ResizeTemplate(Bitmap source, double scale)
    {
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var resized = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(resized);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
        graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        return resized;
    }

    private static byte[,] ToGrayMatrix(Bitmap bitmap)
    {
        var matrix = new byte[bitmap.Height, bitmap.Width];

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var color = bitmap.GetPixel(x, y);
                matrix[y, x] = (byte)((color.R * 299 + color.G * 587 + color.B * 114) / 1000);
            }
        }

        return matrix;
    }

    private static bool[,] ToAlphaMask(Bitmap bitmap)
    {
        var mask = new bool[bitmap.Height, bitmap.Width];

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                mask[y, x] = bitmap.GetPixel(x, y).A > 32;
            }
        }

        return mask;
    }

    private static Bitmap TrimTransparentBorders(Bitmap bitmap)
    {
        var minX = bitmap.Width;
        var minY = bitmap.Height;
        var maxX = -1;
        var maxY = -1;

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).A <= 32)
                {
                    continue;
                }

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        if (maxX < minX || maxY < minY)
        {
            return new Bitmap(bitmap);
        }

        return CropBitmap(bitmap, Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1));
    }

    private static double CalculateTemplateScore(byte[,] source, byte[,] template, bool[,] mask, int offsetX, int offsetY, int step)
    {
        double totalDifference = 0;
        var sampleCount = 0;

        for (var y = 0; y < template.GetLength(0); y += step)
        {
            for (var x = 0; x < template.GetLength(1); x += step)
            {
                if (!mask[y, x])
                {
                    continue;
                }

                totalDifference += Math.Abs(source[offsetY + y, offsetX + x] - template[y, x]);
                sampleCount++;
            }
        }

        if (sampleCount == 0)
        {
            return double.MinValue;
        }

        var averageDifference = totalDifference / sampleCount;
        return 1d - (averageDifference / 255d);
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

    private static Bitmap Upscale(Bitmap input, int factor)
    {
        var resized = new Bitmap(input.Width * factor, input.Height * factor, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(resized);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
        graphics.DrawImage(input, new Rectangle(0, 0, resized.Width, resized.Height));
        return resized;
    }

    private static Bitmap ToGrayscale(Bitmap input)
    {
        var clone = new Bitmap(input.Width, input.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(clone);
        var colorMatrix = new ColorMatrix(new[]
        {
            new[] {0.299f, 0.299f, 0.299f, 0f, 0f},
            new[] {0.587f, 0.587f, 0.587f, 0f, 0f},
            new[] {0.114f, 0.114f, 0.114f, 0f, 0f},
            new[] {0f, 0f, 0f, 1f, 0f},
            new[] {0f, 0f, 0f, 0f, 1f}
        });

        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(colorMatrix);
        g.DrawImage(input, new Rectangle(0, 0, clone.Width, clone.Height), 0, 0, input.Width, input.Height, GraphicsUnit.Pixel, attributes);
        return clone;
    }

    private static Bitmap StretchContrast(Bitmap input)
    {
        var histogram = new int[256];
        var totalPixels = input.Width * input.Height;

        for (var y = 0; y < input.Height; y++)
        {
            for (var x = 0; x < input.Width; x++)
            {
                histogram[input.GetPixel(x, y).R]++;
            }
        }

        var lowClip = (int)(totalPixels * 0.02);
        var highClip = (int)(totalPixels * 0.98);
        var cumulative = 0;
        var min = 0;
        var max = 255;

        for (var i = 0; i < histogram.Length; i++)
        {
            cumulative += histogram[i];
            if (cumulative >= lowClip)
            {
                min = i;
                break;
            }
        }

        cumulative = 0;
        for (var i = 0; i < histogram.Length; i++)
        {
            cumulative += histogram[i];
            if (cumulative >= highClip)
            {
                max = i;
                break;
            }
        }

        if (max <= min)
        {
            return new Bitmap(input);
        }

        var output = new Bitmap(input.Width, input.Height, PixelFormat.Format24bppRgb);
        for (var y = 0; y < input.Height; y++)
        {
            for (var x = 0; x < input.Width; x++)
            {
                var value = input.GetPixel(x, y).R;
                var stretched = ClampThreshold((value - min) * 255 / (max - min));
                output.SetPixel(x, y, Color.FromArgb(stretched, stretched, stretched));
            }
        }

        return output;
    }

    private static List<ThresholdVariant> CreateThresholdVariants(Bitmap grayscale, Bitmap enhancedGrayscale)
    {
        var variants = new List<ThresholdVariant>();
        AddThresholdVariants(variants, grayscale, "base");
        AddThresholdVariants(variants, enhancedGrayscale, "contrast");
        return variants;
    }

    private static void AddThresholdVariants(List<ThresholdVariant> variants, Bitmap source, string sourceName)
    {
        var otsuThreshold = CalculateOtsuThreshold(source);
        var thresholdValues = new[]
        {
            (Name: $"{sourceName}_threshold_otsu_minus30", Threshold: ClampThreshold(otsuThreshold - 30), Invert: false),
            (Name: $"{sourceName}_threshold_otsu_minus25", Threshold: ClampThreshold(otsuThreshold - 25), Invert: false),
            (Name: $"{sourceName}_threshold_otsu_minus20", Threshold: ClampThreshold(otsuThreshold - 20), Invert: false),
            (Name: $"{sourceName}_threshold_otsu_minus15", Threshold: ClampThreshold(otsuThreshold - 15), Invert: false),
            (Name: $"{sourceName}_threshold_otsu_minus10", Threshold: ClampThreshold(otsuThreshold - 10), Invert: false),
            (Name: $"{sourceName}_threshold_otsu", Threshold: otsuThreshold, Invert: false),
            (Name: $"{sourceName}_threshold_otsu_invert", Threshold: otsuThreshold, Invert: true)
        };

        foreach (var candidate in thresholdValues)
        {
            variants.Add(new ThresholdVariant(
                candidate.Name,
                ApplyThreshold(source, candidate.Threshold, candidate.Invert),
                candidate.Threshold,
                candidate.Invert));
        }
    }

    private static Bitmap ApplyThreshold(Bitmap input, int threshold, bool invert)
    {
        var output = new Bitmap(input.Width, input.Height, PixelFormat.Format24bppRgb);

        for (var y = 0; y < input.Height; y++)
        {
            for (var x = 0; x < input.Width; x++)
            {
                var color = input.GetPixel(x, y);
                var isLight = color.R >= threshold;
                var value = invert
                    ? (isLight ? 0 : 255)
                    : (isLight ? 255 : 0);
                output.SetPixel(x, y, Color.FromArgb(value, value, value));
            }
        }

        return output;
    }

    private static int CalculateOtsuThreshold(Bitmap input)
    {
        var histogram = new int[256];
        var totalPixels = input.Width * input.Height;

        for (var y = 0; y < input.Height; y++)
        {
            for (var x = 0; x < input.Width; x++)
            {
                histogram[input.GetPixel(x, y).R]++;
            }
        }

        double sum = 0;
        for (var i = 0; i < histogram.Length; i++)
        {
            sum += i * histogram[i];
        }

        double sumBackground = 0;
        var weightBackground = 0;
        var bestThreshold = 0;
        var maxVariance = 0d;

        for (var threshold = 0; threshold < histogram.Length; threshold++)
        {
            weightBackground += histogram[threshold];
            if (weightBackground == 0)
            {
                continue;
            }

            var weightForeground = totalPixels - weightBackground;
            if (weightForeground == 0)
            {
                break;
            }

            sumBackground += threshold * histogram[threshold];

            var meanBackground = sumBackground / weightBackground;
            var meanForeground = (sum - sumBackground) / weightForeground;
            var varianceBetween = weightBackground * weightForeground * Math.Pow(meanBackground - meanForeground, 2);

            if (varianceBetween > maxVariance)
            {
                maxVariance = varianceBetween;
                bestThreshold = threshold;
            }
        }

        return bestThreshold;
    }

    private static int ClampThreshold(int threshold)
    {
        return Math.Max(0, Math.Min(255, threshold));
    }

    private static byte[] BitmapToBytes(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }

    private static void SaveBitmap(Bitmap bitmap, string filePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        bitmap.Save(filePath, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void SaveDebugSummary(string debugOutputDirectory, IReadOnlyCollection<ScoredOcrAttempt> attempts, OcrResult result, string? preferredMode)
    {
        Directory.CreateDirectory(debugOutputDirectory);

        var summaryBuilder = new StringBuilder();
        summaryBuilder.AppendLine($"Preferred mode: {preferredMode ?? OcrModeCatalog.Auto}");
        summaryBuilder.AppendLine($"Selected attempt: {result.UsedMode}");
        summaryBuilder.AppendLine($"Selected confidence: {result.Confidence:F4}");
        summaryBuilder.AppendLine($"Selected text: {result.NormalizedText}");
        summaryBuilder.AppendLine();

        foreach (var scoredAttempt in attempts
                     .OrderByDescending(attempt => attempt.TotalScore)
                     .ThenByDescending(attempt => attempt.Attempt.Confidence))
        {
            summaryBuilder.AppendLine($"[{scoredAttempt.Attempt.Name}] total={scoredAttempt.TotalScore:F2} base={scoredAttempt.BaseScore:F2} mining={scoredAttempt.MiningAnalysis.ScoreAdjustment:F2} confidence={scoredAttempt.Attempt.Confidence:F4}");

            if (!string.IsNullOrWhiteSpace(scoredAttempt.MiningAnalysis.Notes))
            {
                summaryBuilder.AppendLine($"Signals: {scoredAttempt.MiningAnalysis.Notes}");
            }

            if (scoredAttempt.MiningAnalysis.ParsedScan is not null)
            {
                summaryBuilder.AppendLine($"Parsed summary: {RockScanParser.BuildSummary(scoredAttempt.MiningAnalysis.ParsedScan)}");

                if (scoredAttempt.MiningAnalysis.ParsedScan.ParseWarnings.Count > 0)
                {
                    summaryBuilder.AppendLine($"Warnings: {string.Join(" | ", scoredAttempt.MiningAnalysis.ParsedScan.ParseWarnings)}");
                }
            }

            summaryBuilder.AppendLine(scoredAttempt.Attempt.NormalizedText);
            summaryBuilder.AppendLine();
        }

        File.WriteAllText(Path.Combine(debugOutputDirectory, "ocr_attempts.txt"), summaryBuilder.ToString());
        File.WriteAllText(Path.Combine(debugOutputDirectory, "ocr_selected.txt"), result.NormalizedText);
    }

    private sealed record OcrAttempt(string Name, string RawText, string NormalizedText, float Confidence);
    private sealed record ScoredOcrAttempt(OcrAttempt Attempt, double TotalScore, double BaseScore, MiningAttemptAnalysis MiningAnalysis);
    private sealed record MiningAttemptAnalysis(double ScoreAdjustment, string Notes, RockScan? ParsedScan);
    private sealed record SignatureNumericGroup(string Value, int VoteCount, double BestScore, double TotalScore, SignatureNumericAttempt BestAttempt);
    private sealed record SignatureNumericAttempt(OcrAttempt Attempt, string ExtractedValue, Rectangle Region);
    private sealed record ThresholdVariant(string Name, Bitmap Bitmap, int Threshold, bool Invert);
}
