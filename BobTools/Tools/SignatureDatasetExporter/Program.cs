using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BobTools.Services;
using Tesseract;

return ProgramMain.Run(args);

internal static class ProgramMain
{
    private const int ExportCropWidth = 160;
    private const int ExportCropHeight = 64;
    private const int ExportCropMargin = 4;
    private const int InkThreshold = 235;
    private const double SignatureTemplateScoreThreshold = 0.52d;
    private const string NumericWhitelist = "0123456789,.";
    private static readonly Regex SignatureNumericValuePattern = new(@"(?<!\d)(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d+)?(?!\d)", RegexOptions.Compiled);
    private static readonly Regex StrictNumericValuePattern = new(@"(?<!\d)\d{1,3}(?:,\d{3})+(?!\d)", RegexOptions.Compiled);
    private static readonly Regex SelectedRegionPattern = new(@"Selected value region:\s*\{X=(?<x>-?\d+),Y=(?<y>-?\d+),Width=(?<width>\d+),Height=(?<height>\d+)\}", RegexOptions.Compiled);

    public static int Run(string[] args)
    {
        var options = ExportOptions.Parse(args);
        if (options.ShowHelp)
        {
            ExportOptions.WriteHelp();
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(options.IngestCsvPath))
        {
            var ingestCount = PromoteFixturesFromIngestCsv(options);
            Console.WriteLine($"Staged {ingestCount} labeled capture(s) into '{options.FixturesRoot}'.");

            if (options.IngestOnly)
            {
                return 0;
            }
        }

        Directory.CreateDirectory(options.OutputRoot);
        Directory.CreateDirectory(options.RawImagesDirectory);
        Directory.CreateDirectory(options.CropImagesDirectory);
        Directory.CreateDirectory(options.ManifestsDirectory);
        Directory.CreateDirectory(options.ReportsDirectory);

        var discoveredPngFiles = Directory.Exists(options.FixturesRoot)
            ? Directory.GetFiles(options.FixturesRoot, "*.png", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : Array.Empty<string>();

        var fixtureFiles = discoveredPngFiles
            .Where(path => ShouldIncludeFixtureFile(Path.GetFileName(path)))
            .ToArray();

        if (fixtureFiles.Length == 0)
        {
            Console.Error.WriteLine($"No PNG fixtures were found under '{options.FixturesRoot}'.");
            return 1;
        }

        if (!File.Exists(options.SignatureIconPath))
        {
            Console.Error.WriteLine($"Signature icon was not found at '{options.SignatureIconPath}'.");
            return 1;
        }

        var records = new List<ManifestRecord>();
        var baselineRecords = new List<BaselineRecord>();

        using var rawIcon = new Bitmap(options.SignatureIconPath);
        using var signatureIcon = TrimTransparentBorders(rawIcon);
        using var engine = CreateNumericEngine(options.TessDataPath);

        foreach (var (path, index) in fixtureFiles.Select((path, index) => (path, index + 1)))
        {
            var draftRecord = ManifestRecord.Create(path, index);
            var rawOutputPath = Path.Combine(options.OutputRoot, draftRecord.RawImageRelativePath);
            CopyFile(path, rawOutputPath, options.Overwrite);

            using var bitmap = new Bitmap(path);
            var cropOutputPath = Path.Combine(options.OutputRoot, draftRecord.CropImageRelativePath);
            var cropExport = ExportNormalizedCrop(engine, options.TessDataPath, bitmap, signatureIcon, draftRecord.ExpectedText, cropOutputPath, options.Overwrite);
            var baseline = RunBaseline(engine, cropOutputPath);
            var notes = CombineNotes(draftRecord.Notes, cropExport.Note, baseline.Error is null ? null : $"baseline-error: {baseline.Error}");
            var record = draftRecord with
            {
                CropStatus = cropExport.Status,
                CropStrategy = cropExport.Strategy,
                CropBounds = cropExport.Bounds,
                Notes = notes
            };

            records.Add(record);
            baselineRecords.Add(BaselineRecord.Create(record, baseline));
        }

        WriteManifestCsv(Path.Combine(options.ManifestsDirectory, "labels.csv"), records);
        WriteManifestCsv(Path.Combine(options.ManifestsDirectory, "train.csv"), records.Where(record => record.Split == "train"));
        WriteManifestCsv(Path.Combine(options.ManifestsDirectory, "val.csv"), records.Where(record => record.Split == "val"));
        WriteManifestCsv(Path.Combine(options.ManifestsDirectory, "test.csv"), records.Where(record => record.Split == "test"));
        WriteBaselineCsv(Path.Combine(options.ReportsDirectory, "current_ocr_baseline.csv"), baselineRecords);
        WriteBaselineSummary(Path.Combine(options.ReportsDirectory, "current_ocr_baseline_summary.txt"), baselineRecords);

        Console.WriteLine($"Exported {records.Count} fixture record(s) to '{options.OutputRoot}'.");
        Console.WriteLine($"Labels: {Path.Combine(options.ManifestsDirectory, "labels.csv")}");
        Console.WriteLine($"Baseline report: {Path.Combine(options.ReportsDirectory, "current_ocr_baseline.csv")}");
        Console.WriteLine("Crops were generated into data/crops using icon-guided template matching with a deterministic fallback region when the icon could not be located.");
        return 0;
    }

    private static bool ShouldIncludeFixtureFile(string fileName)
    {
        if (fileName.StartsWith("01_", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(fileName, "signature_full.png", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteManifestCsv(string path, IEnumerable<ManifestRecord> records)
    {
        var builder = new StringBuilder();
        builder.AppendLine("id,source_image_path,raw_image_path,crop_image_path,expected_text,expected_resource,is_hard_case,split,label_source,crop_status,crop_strategy,crop_bounds,notes");

        foreach (var record in records)
        {
            builder.AppendLine(string.Join(',',
                Escape(record.Id),
                Escape(record.SourceImagePath),
                Escape(ToPortablePath(record.RawImageRelativePath)),
                Escape(ToPortablePath(record.CropImageRelativePath)),
                Escape(record.ExpectedText ?? string.Empty),
                Escape(record.ExpectedResource ?? string.Empty),
                Escape(record.IsHardCase ? "true" : "false"),
                Escape(record.Split),
                Escape(record.LabelSource),
                Escape(record.CropStatus),
                Escape(record.CropStrategy),
                Escape(record.CropBounds),
                Escape(record.Notes ?? string.Empty)));
        }

        File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
    }

    private static void WriteBaselineCsv(string path, IEnumerable<BaselineRecord> records)
    {
        var builder = new StringBuilder();
        builder.AppendLine("id,source_image_path,crop_image_path,expected_text,normalized_text,raw_text,confidence,used_mode,matched_expected,status,notes");

        foreach (var record in records)
        {
            builder.AppendLine(string.Join(',',
                Escape(record.Id),
                Escape(record.SourceImagePath),
                Escape(ToPortablePath(record.CropImageRelativePath)),
                Escape(record.ExpectedText ?? string.Empty),
                Escape(record.NormalizedText ?? string.Empty),
                Escape(record.RawText ?? string.Empty),
                Escape(record.Confidence?.ToString("F4", CultureInfo.InvariantCulture) ?? string.Empty),
                Escape(record.UsedMode ?? string.Empty),
                Escape(record.MatchedExpected switch
                {
                    true => "true",
                    false => "false",
                    _ => string.Empty
                }),
                Escape(record.Status),
                Escape(record.Notes ?? string.Empty)));
        }

        File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
    }

    private static void WriteBaselineSummary(string path, IReadOnlyCollection<BaselineRecord> records)
    {
        var labeled = records.Where(record => !string.IsNullOrWhiteSpace(record.ExpectedText)).ToArray();
        var exactMatches = labeled.Count(record => record.MatchedExpected == true);
        var failures = records.Count(record => !string.Equals(record.Status, "ok", StringComparison.OrdinalIgnoreCase));

        var builder = new StringBuilder();
        builder.AppendLine("BobTools current OCR baseline");
        builder.AppendLine();
        builder.AppendLine($"Total fixtures: {records.Count}");
        builder.AppendLine($"Labeled fixtures: {labeled.Length}");
        builder.AppendLine($"Exact labeled matches: {exactMatches}");
        builder.AppendLine($"Exact labeled accuracy: {(labeled.Length == 0 ? 0d : exactMatches * 100d / labeled.Length):F2}%");
        builder.AppendLine($"Baseline errors: {failures}");

        var mismatches = labeled
            .Where(record => record.MatchedExpected == false)
            .OrderBy(record => record.Id, StringComparer.Ordinal)
            .ToArray();

        if (mismatches.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Mismatches:");
            foreach (var mismatch in mismatches)
            {
                builder.AppendLine($"- {mismatch.Id}: expected '{mismatch.ExpectedText}', got '{mismatch.NormalizedText ?? string.Empty}' ({mismatch.UsedMode ?? "n/a"})");
            }
        }

        File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
    }

    private static CropExportResult ExportNormalizedCrop(TesseractEngine engine, string tessDataPath, Bitmap source, Bitmap signatureIcon, string? expectedText, string outputPath, bool overwrite)
    {
        var appCandidate = TrySelectCropRegionFromAppOcr(tessDataPath, source, expectedText);

        CropRegionSelection? iconCandidate = null;
        if (TryLocateTemplate(source, signatureIcon, out var iconBounds, out var matchScore)
            && IsPlausibleSignatureIconLocation(source.Size, iconBounds, matchScore))
        {
            iconCandidate = SelectBestCropRegion(
                engine,
                source,
                BuildSignatureValueRegions(source.Size, iconBounds),
                expectedText,
                $"icon-ocr-search:{matchScore:F4}");
        }

            var primaryCandidate = appCandidate ?? iconCandidate;

        CropRegionSelection? broadCandidate = null;
            if (primaryCandidate is null || ShouldRunBroadSearch(expectedText, primaryCandidate))
        {
            var secondaryRegions = !string.IsNullOrWhiteSpace(expectedText)
                ? BuildExpectedValueSearchRegions(source.Size, expectedText).Concat(BuildBroadSearchRegions(source.Size))
                : BuildBroadSearchRegions(source.Size);

            broadCandidate = SelectBestCropRegion(
                engine,
                source,
                secondaryRegions,
                expectedText,
                string.IsNullOrWhiteSpace(expectedText) ? "broad-ocr-search" : "expected-ocr-search");
        }

            var selectedCandidate = ChoosePreferredCropCandidate(expectedText, primaryCandidate, broadCandidate);

            if (!string.IsNullOrWhiteSpace(expectedText)
                && source.Width >= 400
                && (selectedCandidate is null || selectedCandidate.MatchQuality != CropMatchQuality.Exact))
            {
                var wideLayoutCandidate = SelectBestCropRegion(
                    engine,
                    source,
                    BuildWideLayoutAnchorRegions(source.Size),
                    expectedText,
                    "wide-layout-anchor");

                selectedCandidate = ChoosePreferredCropCandidate(expectedText, selectedCandidate, wideLayoutCandidate);
            }

        if (selectedCandidate is not null)
        {
            var expandedRegion = ExpandCropRegion(source.Size, selectedCandidate.Region);
            using var rawCrop = CropBitmap(source, expandedRegion);
            using var normalized = BuildTrainingCrop(rawCrop);
            SavePng(normalized, outputPath, overwrite);
            return new CropExportResult("ok", selectedCandidate.Strategy, FormatBounds(expandedRegion), selectedCandidate.Note);
        }

        var fallbackRegion = BuildFallbackRegion(source.Size);
        using var fallbackCrop = CropBitmap(source, fallbackRegion);
        using var fallbackNormalized = BuildTrainingCrop(fallbackCrop);
        SavePng(fallbackNormalized, outputPath, overwrite);
        return new CropExportResult("fallback", "top-right-window", FormatBounds(fallbackRegion), "Template match was unavailable, so a coarse fallback crop was exported.");
    }

    private static bool ShouldRunBroadSearch(string? expectedText, CropRegionSelection iconCandidate)
    {
        if (iconCandidate.MatchQuality == CropMatchQuality.None)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(expectedText) && iconCandidate.MatchQuality != CropMatchQuality.Exact)
        {
            return true;
        }

        return NormalizeNumericText(iconCandidate.BestValue).Length < 4;
    }

    private static CropRegionSelection? TrySelectCropRegionFromAppOcr(string tessDataPath, Bitmap source, string? expectedText)
    {
        var debugDirectory = Path.Combine(Path.GetTempPath(), "BobTools", "SignatureDatasetExporter", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(debugDirectory);

        try
        {
            using var ocrService = new OcrService(tessDataPath);
            var result = ocrService.ExtractTextAsync(source, debugDirectory, captureProfile: "signature").GetAwaiter().GetResult();
            var matchFilePath = Path.Combine(debugDirectory, "00_signature_icon_match.txt");

            if (!File.Exists(matchFilePath))
            {
                return null;
            }

            if (!TryParseSelectedRegion(File.ReadAllText(matchFilePath), out var region))
            {
                return null;
            }

            var bestValue = result.NormalizedText ?? string.Empty;
            var matchQuality = DetermineMatchQuality(bestValue, expectedText);
            var similarityScore = CalculateSimilarityScore(bestValue, expectedText);
            var regionScore = (result.Confidence * 100d) + similarityScore + 220d;
            if (matchQuality == CropMatchQuality.Exact)
            {
                regionScore += 300d;
            }

            var note = string.IsNullOrWhiteSpace(bestValue)
                ? "App OCR selected the signature value region."
                : $"App OCR selected the signature value region with candidate {bestValue}.";

            return new CropRegionSelection(region, regionScore, matchQuality, similarityScore, bestValue, "app-signature-fast-path", note);
        }
        catch
        {
            return null;
        }
        finally
        {
            try
            {
                if (Directory.Exists(debugDirectory))
                {
                    Directory.Delete(debugDirectory, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private static bool TryParseSelectedRegion(string contents, out Rectangle region)
    {
        var match = SelectedRegionPattern.Match(contents);
        if (!match.Success)
        {
            region = Rectangle.Empty;
            return false;
        }

        region = new Rectangle(
            int.Parse(match.Groups["x"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["width"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["height"].Value, CultureInfo.InvariantCulture));
        return true;
    }

    private static CropRegionSelection? ChoosePreferredCropCandidate(
        string? expectedText,
        CropRegionSelection? iconCandidate,
        CropRegionSelection? broadCandidate)
    {
        if (iconCandidate is null)
        {
            return broadCandidate;
        }

        if (broadCandidate is null)
        {
            return iconCandidate;
        }

        if (!string.IsNullOrWhiteSpace(expectedText))
        {
            if (broadCandidate.MatchQuality != iconCandidate.MatchQuality)
            {
                return broadCandidate.MatchQuality > iconCandidate.MatchQuality ? broadCandidate : iconCandidate;
            }

            if (broadCandidate.SimilarityScore != iconCandidate.SimilarityScore)
            {
                return broadCandidate.SimilarityScore > iconCandidate.SimilarityScore ? broadCandidate : iconCandidate;
            }
        }

        if (broadCandidate.RegionScore != iconCandidate.RegionScore)
        {
            return broadCandidate.RegionScore > iconCandidate.RegionScore ? broadCandidate : iconCandidate;
        }

        return (broadCandidate.Region.Width * broadCandidate.Region.Height) < (iconCandidate.Region.Width * iconCandidate.Region.Height)
            ? broadCandidate
            : iconCandidate;
    }

    private static CropRegionSelection? SelectBestCropRegion(
        TesseractEngine engine,
        Bitmap source,
        IEnumerable<Rectangle> candidateRegions,
        string? expectedText,
        string strategyPrefix)
    {
        var scoredRegions = candidateRegions
            .Distinct()
            .Select(region => EvaluateCropRegion(engine, source, region, expectedText, strategyPrefix))
            .Where(result => result is not null)
            .Cast<CropRegionSelection>()
            .OrderByDescending(result => result.RegionScore)
            .ThenBy(result => result.Region.Width * result.Region.Height)
            .ToList();

        if (scoredRegions.Count == 0)
        {
            return null;
        }

        var best = scoredRegions[0];
        if (best.MatchQuality == CropMatchQuality.None && best.RegionScore < 55d)
        {
            return null;
        }

        return best;
    }

    private static CropRegionSelection? EvaluateCropRegion(
        TesseractEngine engine,
        Bitmap source,
        Rectangle region,
        string? expectedText,
        string strategyPrefix)
    {
        if (region.Width < 12 || region.Height < 12)
        {
            return null;
        }

        using var valueBitmap = CropBitmap(source, region);
        using var upscaled = Upscale(valueBitmap, 2);
        using var grayscale = ToGrayscale(upscaled);
        using var contrast = StretchContrast(grayscale);
        using var binary190 = ApplyThreshold(contrast, 190);
        using var binary170 = ApplyThreshold(contrast, 170);

        var attempts = new[]
        {
            RunAttempt(engine, grayscale, PageSegMode.SingleLine, $"{strategyPrefix}-grayscale-line"),
            RunAttempt(engine, grayscale, PageSegMode.SingleWord, $"{strategyPrefix}-grayscale-word"),
            RunAttempt(engine, contrast, PageSegMode.SingleLine, $"{strategyPrefix}-contrast-line"),
            RunAttempt(engine, binary190, PageSegMode.SingleLine, $"{strategyPrefix}-binary190-line"),
            RunAttempt(engine, binary170, PageSegMode.SingleLine, $"{strategyPrefix}-binary170-line")
        };

        var candidates = attempts
            .Select(attempt => CropAttemptCandidate.Create(attempt, expectedText))
            .ToList();

        var nonEmptyGroups = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.ExtractedValue))
            .GroupBy(candidate => candidate.ExtractedValue!, StringComparer.Ordinal)
            .Select(group =>
            {
                var bestAttempt = group.OrderByDescending(candidate => candidate.BaseScore).First();
                var totalScore = group.Sum(candidate => candidate.BaseScore);
                var voteCount = group.Count();
                var boostedScore = bestAttempt.BaseScore + (totalScore * 0.25d) + (voteCount * 18d);
                return new
                {
                    Value = group.Key,
                    BestAttempt = bestAttempt,
                    VoteCount = voteCount,
                    Score = boostedScore
                };
            })
            .OrderByDescending(group => group.Score)
            .ThenByDescending(group => group.VoteCount)
            .ThenBy(group => region.Width * region.Height)
            .ToList();

        var aspectRatio = (double)region.Width / Math.Max(1, region.Height);
        var aspectPenalty = Math.Abs(aspectRatio - 2.6d) * 6d;
        var areaPenalty = (region.Width * region.Height) / 260d;

        if (nonEmptyGroups.Count == 0)
        {
            var emptyScore = candidates.Max(candidate => candidate.BaseScore) - aspectPenalty - areaPenalty - 80d;
            return new CropRegionSelection(region, emptyScore, CropMatchQuality.None, 0, string.Empty, strategyPrefix, "No numeric OCR candidate was found for this region.");
        }

        var bestGroup = nonEmptyGroups[0];
        var layoutAffinityScore = CalculateLayoutAffinityScore(source.Size, region);
        var regionScore = bestGroup.Score - aspectPenalty - areaPenalty + layoutAffinityScore;
        var matchQuality = DetermineMatchQuality(bestGroup.Value, expectedText);
        var similarityScore = CalculateSimilarityScore(bestGroup.Value, expectedText);
        var note = matchQuality == CropMatchQuality.Exact
            ? $"Matched expected value {bestGroup.Value} while choosing crop region."
            : $"Best OCR candidate for crop region was {bestGroup.Value}.";

        return new CropRegionSelection(region, regionScore, matchQuality, similarityScore, bestGroup.Value, bestGroup.BestAttempt.Attempt.Name, note);
    }

    private static double CalculateLayoutAffinityScore(Size imageSize, Rectangle region)
    {
        var centerXRatio = (region.Left + (region.Width / 2d)) / imageSize.Width;
        var centerYRatio = (region.Top + (region.Height / 2d)) / imageSize.Height;
        var widthRatio = (double)region.Width / imageSize.Width;
        var heightRatio = (double)region.Height / imageSize.Height;

        const double targetCenterXRatio = 0.50d;
        const double targetCenterYRatio = 0.36d;
        const double targetWidthRatio = 0.24d;
        const double targetHeightRatio = 0.32d;

        var centerXPenalty = Math.Abs(centerXRatio - targetCenterXRatio) * 220d;
        var centerYPenalty = Math.Abs(centerYRatio - targetCenterYRatio) * 260d;
        var widthPenalty = Math.Abs(widthRatio - targetWidthRatio) * 140d;
        var heightPenalty = Math.Abs(heightRatio - targetHeightRatio) * 140d;

        return 90d - centerXPenalty - centerYPenalty - widthPenalty - heightPenalty;
    }

    private static CropMatchQuality DetermineMatchQuality(string extractedValue, string? expectedText)
    {
        if (string.IsNullOrWhiteSpace(extractedValue))
        {
            return CropMatchQuality.None;
        }

        if (!string.IsNullOrWhiteSpace(expectedText) && string.Equals(NormalizeNumericText(extractedValue), NormalizeNumericText(expectedText), StringComparison.Ordinal))
        {
            return CropMatchQuality.Exact;
        }

        return StrictNumericValuePattern.IsMatch(extractedValue)
            ? CropMatchQuality.StrictNumeric
            : CropMatchQuality.LooseNumeric;
    }

    private static int CalculateSimilarityScore(string extractedValue, string? expectedText)
    {
        var extractedDigits = NormalizeNumericText(extractedValue);
        var expectedDigits = NormalizeNumericText(expectedText);

        if (string.IsNullOrWhiteSpace(extractedDigits) || string.IsNullOrWhiteSpace(expectedDigits))
        {
            return 0;
        }

        if (string.Equals(extractedDigits, expectedDigits, StringComparison.Ordinal))
        {
            return 100;
        }

        var maxLength = Math.Max(extractedDigits.Length, expectedDigits.Length);
        var samePositionCount = 0;
        for (var index = 0; index < Math.Min(extractedDigits.Length, expectedDigits.Length); index++)
        {
            if (extractedDigits[index] == expectedDigits[index])
            {
                samePositionCount++;
            }
        }

        return (int)Math.Round((double)samePositionCount / maxLength * 100d, MidpointRounding.AwayFromZero);
    }

    private static List<Rectangle> BuildBroadSearchRegions(Size imageSize)
    {
        var regions = new List<Rectangle>();
        var xStarts = new[]
        {
            (int)Math.Round(imageSize.Width * 0.28d),
            (int)Math.Round(imageSize.Width * 0.42d),
            (int)Math.Round(imageSize.Width * 0.56d),
            (int)Math.Round(imageSize.Width * 0.68d)
        };
        var yStarts = new[]
        {
            0,
            (int)Math.Round(imageSize.Height * 0.05d),
            (int)Math.Round(imageSize.Height * 0.12d),
            (int)Math.Round(imageSize.Height * 0.20d)
        };
        var widths = new[]
        {
            (int)Math.Round(imageSize.Width * 0.14d),
            (int)Math.Round(imageSize.Width * 0.20d),
            (int)Math.Round(imageSize.Width * 0.28d)
        };
        var heights = new[]
        {
            (int)Math.Round(imageSize.Height * 0.08d),
            (int)Math.Round(imageSize.Height * 0.12d),
            (int)Math.Round(imageSize.Height * 0.16d)
        };

        foreach (var xStart in xStarts)
        {
            foreach (var yStart in yStarts)
            {
                foreach (var width in widths)
                {
                    foreach (var height in heights)
                    {
                        var x = Math.Max(0, Math.Min(imageSize.Width - 1, xStart));
                        var y = Math.Max(0, Math.Min(imageSize.Height - 1, yStart));
                        var clampedWidth = Math.Min(Math.Max(24, width), imageSize.Width - x);
                        var clampedHeight = Math.Min(Math.Max(18, height), imageSize.Height - y);

                        if (clampedWidth < 12 || clampedHeight < 12)
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

    private static List<Rectangle> BuildExpectedValueSearchRegions(Size imageSize, string expectedText)
    {
        var regions = new List<Rectangle>();
        var expectedDigits = NormalizeNumericText(expectedText);
        var estimatedCharacterCount = Math.Max(4, expectedDigits.Length + Math.Max(1, expectedDigits.Length / 3));
        var widths = new[]
        {
            Math.Max(72, estimatedCharacterCount * 18),
            Math.Max(96, estimatedCharacterCount * 22),
            Math.Max(124, estimatedCharacterCount * 28)
        };
        var heights = new[]
        {
            Math.Max(28, (int)Math.Round(imageSize.Height * 0.08d)),
            Math.Max(40, (int)Math.Round(imageSize.Height * 0.13d))
        };
        var yStarts = new[]
        {
            0,
            (int)Math.Round(imageSize.Height * 0.06d),
            (int)Math.Round(imageSize.Height * 0.14d)
        };
        var xPercents = new[] { 0.20d, 0.36d, 0.52d };

        foreach (var rawWidth in widths)
        {
            var width = Math.Min(Math.Max(24, rawWidth), imageSize.Width);
            var xStarts = xPercents
                .Select(percent => (int)Math.Round(imageSize.Width * percent))
                .Append(Math.Max(0, imageSize.Width - width - 4))
                .Distinct()
                .ToArray();

            foreach (var xStart in xStarts)
            {
                foreach (var yStart in yStarts)
                {
                    foreach (var rawHeight in heights)
                    {
                        var x = Math.Max(0, Math.Min(imageSize.Width - 1, xStart));
                        var y = Math.Max(0, Math.Min(imageSize.Height - 1, yStart));
                        var clampedWidth = Math.Min(width, imageSize.Width - x);
                        var clampedHeight = Math.Min(Math.Max(18, rawHeight), imageSize.Height - y);

                        if (clampedWidth < 12 || clampedHeight < 12)
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

    private static List<Rectangle> BuildWideLayoutAnchorRegions(Size imageSize)
    {
        var regions = new List<Rectangle>();
        var xStarts = new[] { 0.34d, 0.40d, 0.46d };
        var yStarts = new[] { 0.12d, 0.18d };
        var widths = new[] { 0.20d, 0.26d };
        var heights = new[] { 0.24d, 0.32d };

        foreach (var xStartRatio in xStarts)
        {
            foreach (var yStartRatio in yStarts)
            {
                foreach (var widthRatio in widths)
                {
                    foreach (var heightRatio in heights)
                    {
                        var x = (int)Math.Round(imageSize.Width * xStartRatio);
                        var y = (int)Math.Round(imageSize.Height * yStartRatio);
                        var width = (int)Math.Round(imageSize.Width * widthRatio);
                        var height = (int)Math.Round(imageSize.Height * heightRatio);

                        x = Math.Max(0, Math.Min(imageSize.Width - 1, x));
                        y = Math.Max(0, Math.Min(imageSize.Height - 1, y));
                        width = Math.Min(Math.Max(24, width), imageSize.Width - x);
                        height = Math.Min(Math.Max(18, height), imageSize.Height - y);

                        if (width < 12 || height < 12)
                        {
                            continue;
                        }

                        var region = new Rectangle(x, y, width, height);
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

    private static TesseractEngine CreateNumericEngine(string tessDataPath)
    {
        var engine = new TesseractEngine(tessDataPath, "eng", EngineMode.LstmOnly);
        engine.SetVariable("preserve_interword_spaces", "1");
        engine.SetVariable("user_defined_dpi", "300");
        engine.SetVariable("tessedit_char_whitelist", NumericWhitelist);
        return engine;
    }

    private static BaselineRunResult RunBaseline(TesseractEngine engine, string cropPath)
    {
        try
        {
            using var crop = new Bitmap(cropPath);
            using var grayscale = ToGrayscale(crop);
            using var contrast = StretchContrast(grayscale);
            using var binary = ApplyThreshold(contrast, 190);

            var attempts = new[]
            {
                RunAttempt(engine, grayscale, PageSegMode.SingleLine, "grayscale-line"),
                RunAttempt(engine, grayscale, PageSegMode.SingleWord, "grayscale-word"),
                RunAttempt(engine, contrast, PageSegMode.SingleLine, "contrast-line"),
                RunAttempt(engine, binary, PageSegMode.SingleLine, "binary-line")
            };

            var best = attempts
                .Select(attempt => new { Attempt = attempt, Score = ScoreAttempt(attempt) })
                .OrderByDescending(entry => entry.Score)
                .ThenByDescending(entry => entry.Attempt.Confidence)
                .First()
                .Attempt;

            return new BaselineRunResult(
                best.RawText,
                ExtractNumericValue(best.NormalizedText),
                best.Confidence,
                best.Name,
                null);
        }
        catch (Exception ex)
        {
            return new BaselineRunResult(null, null, null, null, ex.Message);
        }
    }

    private static OcrAttempt RunAttempt(TesseractEngine engine, Bitmap bitmap, PageSegMode pageSegMode, string name)
    {
        using var pix = Pix.LoadFromMemory(BitmapToBytes(bitmap));
        using var page = engine.Process(pix, pageSegMode);
        var rawText = page.GetText() ?? string.Empty;
        var normalized = rawText.Replace("\r", string.Empty, StringComparison.Ordinal).Trim();
        return new OcrAttempt(name, rawText, normalized, page.GetMeanConfidence());
    }

    private static byte[] BitmapToBytes(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    }

    private static double ScoreAttempt(OcrAttempt attempt)
    {
        var extracted = ExtractNumericValue(attempt.NormalizedText);
        var score = attempt.Confidence * 100d;

        if (!string.IsNullOrWhiteSpace(extracted))
        {
            score += extracted.Length * 10d;
            if (StrictNumericValuePattern.IsMatch(extracted))
            {
                score += 60d;
            }
            else if (extracted.Any(char.IsDigit))
            {
                score += 25d;
            }
        }

        if (attempt.Name.Contains("line", StringComparison.OrdinalIgnoreCase))
        {
            score += 8d;
        }

        return score;
    }

    internal static string ExtractNumericValue(string text)
    {
        var value = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(line => SignatureNumericValuePattern.Matches(line).Select(match => match.Value))
            .LastOrDefault();

        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var digitsOnly = new string(value.Where(character => char.IsDigit(character)).ToArray());
        if (int.TryParse(digitsOnly, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed.ToString("N0", CultureInfo.InvariantCulture);

        return value.Trim();
    }

    private static Bitmap BuildTrainingCrop(Bitmap source)
    {
        using var grayscale = ToGrayscale(source);
        using var contrast = StretchContrast(grayscale);
        using var trimmed = TrimToInkBounds(contrast, InkThreshold, ExportCropMargin);

        var canvas = new Bitmap(ExportCropWidth, ExportCropHeight, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(canvas);
        graphics.Clear(Color.White);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;

        var availableWidth = ExportCropWidth - (ExportCropMargin * 2);
        var availableHeight = ExportCropHeight - (ExportCropMargin * 2);
        var scale = Math.Min((double)availableWidth / trimmed.Width, (double)availableHeight / trimmed.Height);
        scale = Math.Min(scale, 8d);

        var targetWidth = Math.Max(1, (int)Math.Round(trimmed.Width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(trimmed.Height * scale));
        var targetX = (ExportCropWidth - targetWidth) / 2;
        var targetY = (ExportCropHeight - targetHeight) / 2;
        graphics.DrawImage(trimmed, new Rectangle(targetX, targetY, targetWidth, targetHeight));
        return canvas;
    }

    private static Bitmap TrimToInkBounds(Bitmap source, int threshold, int padding)
    {
        var minX = source.Width;
        var minY = source.Height;
        var maxX = -1;
        var maxY = -1;

        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                if (source.GetPixel(x, y).R >= threshold)
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
            return new Bitmap(source);
        }

        var left = Math.Max(0, minX - padding);
        var top = Math.Max(0, minY - padding);
        var right = Math.Min(source.Width, maxX + padding + 1);
        var bottom = Math.Min(source.Height, maxY + padding + 1);
        return CropBitmap(source, Rectangle.FromLTRB(left, top, right, bottom));
    }

    private static Rectangle BuildFallbackRegion(Size imageSize)
    {
        var x = imageSize.Width / 3;
        var y = 0;
        var width = Math.Max(32, imageSize.Width - x);
        var height = Math.Max(20, Math.Min(imageSize.Height, (int)Math.Round(imageSize.Height * 0.42d)));
        return new Rectangle(x, y, Math.Min(width, imageSize.Width - x), height);
    }

    private static Rectangle ExpandCropRegion(Size imageSize, Rectangle region)
    {
        var horizontalPadding = Math.Max(8, region.Width / 5);
        var verticalPadding = Math.Max(4, region.Height / 4);
        var left = Math.Max(0, region.Left - horizontalPadding);
        var top = Math.Max(0, region.Top - verticalPadding);
        var right = Math.Min(imageSize.Width, region.Right + horizontalPadding);
        var bottom = Math.Min(imageSize.Height, region.Bottom + verticalPadding);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private static int PromoteFixturesFromIngestCsv(ExportOptions options)
    {
        var ingestCsvPath = ResolvePath(options.IngestCsvPath!, options.RepositoryRoot, Path.GetDirectoryName(options.IngestCsvPath!));
        if (!File.Exists(ingestCsvPath))
        {
            throw new FileNotFoundException($"Ingest CSV was not found at '{ingestCsvPath}'.", ingestCsvPath);
        }

        Directory.CreateDirectory(options.FixturesRoot);

        var rows = ReadIngestRows(ingestCsvPath, options.RepositoryRoot);
        if (rows.Count == 0)
        {
            throw new InvalidOperationException($"No ingest rows were found in '{ingestCsvPath}'.");
        }

        foreach (var row in rows)
        {
            var destinationFileName = string.IsNullOrWhiteSpace(row.DestinationFileName)
                ? BuildFixtureFileName(row.SourceImagePath, row.ExpectedText)
                : SanitizeFileName(row.DestinationFileName!);

            var destinationPath = Path.Combine(options.FixturesRoot, destinationFileName);
            CopyFile(row.SourceImagePath, destinationPath, options.Overwrite);
        }

        return rows.Count;
    }

    private static List<IngestRow> ReadIngestRows(string csvPath, string repositoryRoot)
    {
        var rows = new List<IngestRow>();
        using var reader = new StreamReader(csvPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        string? headerLine = null;
        while ((headerLine = reader.ReadLine()) is not null)
        {
            if (!string.IsNullOrWhiteSpace(headerLine))
            {
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(headerLine))
        {
            return rows;
        }

        var headers = ParseCsvLine(headerLine);
        var sourceIndex = GetRequiredHeaderIndex(headers, "source_image_path");
        var expectedIndex = GetOptionalHeaderIndex(headers, "expected_text");
        var destinationIndex = GetOptionalHeaderIndex(headers, "destination_file_name");

        while (!reader.EndOfStream)
        {
            var line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var values = ParseCsvLine(line);
            var sourceValue = GetCsvValue(values, sourceIndex);
            if (string.IsNullOrWhiteSpace(sourceValue))
            {
                continue;
            }

            var sourcePath = ResolvePath(sourceValue, repositoryRoot, Path.GetDirectoryName(csvPath));
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException($"Ingest source image was not found at '{sourcePath}'.", sourcePath);
            }

            rows.Add(new IngestRow(
                sourcePath,
                NormalizeExpectedText(GetCsvValue(values, expectedIndex)),
                GetCsvValue(values, destinationIndex)));
        }

        return rows;
    }

    private static int GetRequiredHeaderIndex(IReadOnlyList<string> headers, string headerName)
    {
        var index = GetOptionalHeaderIndex(headers, headerName);
        if (index < 0)
        {
            throw new InvalidOperationException($"CSV is missing required header '{headerName}'.");
        }

        return index;
    }

    private static int GetOptionalHeaderIndex(IReadOnlyList<string> headers, string headerName)
    {
        for (var index = 0; index < headers.Count; index++)
        {
            if (string.Equals(headers[index], headerName, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static string? GetCsvValue(IReadOnlyList<string> values, int index)
    {
        if (index < 0 || index >= values.Count)
        {
            return null;
        }

        var value = values[index]?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        var insideQuotes = false;

        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (insideQuotes && index + 1 < line.Length && line[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                    continue;
                }

                insideQuotes = !insideQuotes;
                continue;
            }

            if (character == ',' && !insideQuotes)
            {
                values.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            current.Append(character);
        }

        values.Add(current.ToString().Trim());
        return values;
    }

    private static string BuildFixtureFileName(string sourceImagePath, string? expectedText)
    {
        var sourceStem = SanitizeStem(Path.GetFileNameWithoutExtension(sourceImagePath));
        var digits = NormalizeNumericText(expectedText);

        if (!string.IsNullOrWhiteSpace(digits))
        {
            return $"01_{digits}_{sourceStem}.png";
        }

        return $"unlabeled_{sourceStem}.png";
    }

    private static string NormalizeExpectedText(string? expectedText)
    {
        if (string.IsNullOrWhiteSpace(expectedText))
        {
            return string.Empty;
        }

        var digits = NormalizeNumericText(expectedText);
        if (string.IsNullOrWhiteSpace(digits))
        {
            return string.Empty;
        }

        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value.ToString("N0", CultureInfo.InvariantCulture)
            : digits;
    }

    private static string ResolvePath(string path, string repositoryRoot, string? fallbackBaseDirectory)
    {
        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        if (!string.IsNullOrWhiteSpace(fallbackBaseDirectory))
        {
            var candidate = Path.GetFullPath(Path.Combine(fallbackBaseDirectory, path));
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.GetFullPath(Path.Combine(repositoryRoot, path));
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = new string(fileName.Select(character => invalidCharacters.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "ingested_fixture.png" : sanitized;
    }

    private static string SanitizeStem(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (character is '-' or '_' or ' ' or '.')
            {
                builder.Append('_');
            }
        }

        var sanitized = builder.ToString().Trim('_');
        while (sanitized.Contains("__", StringComparison.Ordinal))
        {
            sanitized = sanitized.Replace("__", "_", StringComparison.Ordinal);
        }

        return string.IsNullOrWhiteSpace(sanitized) ? "capture" : sanitized;
    }

    private static void CopyFile(string sourcePath, string destinationPath, bool overwrite)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        if (!overwrite && File.Exists(destinationPath))
        {
            return;
        }

        File.Copy(sourcePath, destinationPath, overwrite: true);
    }

    private static void SavePng(Bitmap bitmap, string path, bool overwrite)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!overwrite && File.Exists(path))
        {
            return;
        }

        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static string CombineNotes(params string?[] notes)
    {
        return string.Join(" | ", notes.Where(note => !string.IsNullOrWhiteSpace(note)).Select(note => note!.Trim()));
    }

    private static string ToPortablePath(string path) => path.Replace('\\', '/');

    private static string Escape(string value)
    {
        var escaped = value.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }

    private static string FormatBounds(Rectangle bounds) => $"{bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}";

    private static bool IsPlausibleSignatureIconLocation(Size imageSize, Rectangle iconBounds, double matchScore)
    {
        if (iconBounds == Rectangle.Empty)
        {
            return false;
        }

        if (matchScore < SignatureTemplateScoreThreshold)
        {
            return false;
        }

        if (iconBounds.Width < 6 || iconBounds.Height < 6)
        {
            return false;
        }

        return IsPlausibleSignatureIconBounds(imageSize, iconBounds);
    }

    private static bool IsPlausibleSignatureIconBounds(Size imageSize, Rectangle iconBounds)
    {
        if (iconBounds == Rectangle.Empty || iconBounds.Width < 6 || iconBounds.Height < 6)
        {
            return false;
        }

        var remainingWidth = imageSize.Width - iconBounds.Right;
        var minimumExpectedValueWidth = Math.Max(32, Math.Min(64, imageSize.Width / 6));
        if (remainingWidth < minimumExpectedValueWidth)
        {
            return false;
        }

        return iconBounds.Top <= imageSize.Height * 0.7;
    }

    private static List<Rectangle> BuildSignatureValueRegions(Size imageSize, Rectangle iconBounds)
    {
        var regions = new List<Rectangle>();
        var startOffsets = new[]
        {
            0,
            -Math.Max(2, iconBounds.Width / 8),
            Math.Max(2, iconBounds.Width / 8),
            -Math.Max(4, iconBounds.Width / 4)
        };
        var topOffsets = new[] { 0, -Math.Max(2, iconBounds.Height / 10) };
        var heights = new[]
        {
            iconBounds.Height + Math.Max(4, iconBounds.Height / 2),
            iconBounds.Height + Math.Max(8, iconBounds.Height * 3 / 4)
        };
        var widths = new[]
        {
            Math.Max(iconBounds.Width * 2, 48),
            Math.Max(iconBounds.Width * 3, 72),
            Math.Min(Math.Max(iconBounds.Width * 4, 96), Math.Max(imageSize.Width - iconBounds.Right, 0))
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

    private static bool TryLocateTemplate(Bitmap source, Bitmap template, out Rectangle bestBounds, out double bestScore)
    {
        bestBounds = Rectangle.Empty;
        bestScore = double.MinValue;
        var bestPlausibleBounds = Rectangle.Empty;
        var bestPlausibleScore = double.MinValue;

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

                    var candidateBounds = new Rectangle(x, y, scaledTemplate.Width, scaledTemplate.Height);
                    if (score > bestPlausibleScore && IsPlausibleSignatureIconBounds(source.Size, candidateBounds))
                    {
                        bestPlausibleScore = score;
                        bestPlausibleBounds = candidateBounds;
                    }
                }
            }
        }

        if (bestPlausibleBounds != Rectangle.Empty)
        {
            bestBounds = bestPlausibleBounds;
            bestScore = bestPlausibleScore;
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
        return scales.OrderBy(scale => scale).ToArray();
    }

    private static Bitmap ResizeTemplate(Bitmap source, double scale)
    {
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var resized = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(resized);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
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

    private static Bitmap CropBitmap(Bitmap source, Rectangle bounds)
    {
        var output = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(output);
        graphics.DrawImage(source, new Rectangle(0, 0, bounds.Width, bounds.Height), bounds, GraphicsUnit.Pixel);
        return output;
    }

    private static Bitmap Upscale(Bitmap input, int factor)
    {
        var resized = new Bitmap(input.Width * factor, input.Height * factor, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(resized);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.DrawImage(input, new Rectangle(0, 0, resized.Width, resized.Height));
        return resized;
    }

    private static Bitmap ToGrayscale(Bitmap input)
    {
        var clone = new Bitmap(input.Width, input.Height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(clone);
        using var attributes = new ImageAttributes();
        var colorMatrix = new ColorMatrix(new[]
        {
            new[] {0.299f, 0.299f, 0.299f, 0f, 0f},
            new[] {0.587f, 0.587f, 0.587f, 0f, 0f},
            new[] {0.114f, 0.114f, 0.114f, 0f, 0f},
            new[] {0f, 0f, 0f, 1f, 0f},
            new[] {0f, 0f, 0f, 0f, 1f}
        });

        attributes.SetColorMatrix(colorMatrix);
        graphics.DrawImage(input, new Rectangle(0, 0, clone.Width, clone.Height), 0, 0, input.Width, input.Height, GraphicsUnit.Pixel, attributes);
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

    private static Bitmap ApplyThreshold(Bitmap input, byte threshold)
    {
        var output = new Bitmap(input.Width, input.Height, PixelFormat.Format24bppRgb);
        for (var y = 0; y < input.Height; y++)
        {
            for (var x = 0; x < input.Width; x++)
            {
                var value = input.GetPixel(x, y).R >= threshold ? 255 : 0;
                output.SetPixel(x, y, Color.FromArgb(value, value, value));
            }
        }

        return output;
    }

    private static int ClampThreshold(int value) => Math.Max(0, Math.Min(255, value));

    internal static string NormalizeNumericText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string(value.Where(character => char.IsDigit(character)).ToArray());
    }
}

internal sealed record ExportOptions(
    string RepositoryRoot,
    string FixturesRoot,
    string OutputRoot,
    string TessDataPath,
    string SignatureIconPath,
    string? IngestCsvPath,
    bool IngestOnly,
    bool Overwrite,
    bool ShowHelp)
{
    public string RawImagesDirectory => Path.Combine(OutputRoot, "data", "raw");
    public string CropImagesDirectory => Path.Combine(OutputRoot, "data", "crops");
    public string ManifestsDirectory => Path.Combine(OutputRoot, "data", "manifests");
    public string ReportsDirectory => Path.Combine(OutputRoot, "outputs", "reports");

    public static ExportOptions Parse(string[] args)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var currentDirectory = Environment.CurrentDirectory;
        var repoRoot = FindRepositoryRoot(baseDirectory);
        var defaultFixtures = Path.Combine(repoRoot, "BobTools", "Assets", "TestImages", "scanning");
        var defaultOutputRoot = Path.Combine(repoRoot, "tools", "signature-recognizer");
        var defaultTessDataPath = Path.Combine(repoRoot, "BobTools", "Assets", "tessdata");
        var defaultSignatureIconPath = Path.Combine(repoRoot, "BobTools", "Assets", "images", "signature_icon.png");

        var fixturesRoot = defaultFixtures;
        var outputRoot = defaultOutputRoot;
        var tessDataPath = defaultTessDataPath;
        var signatureIconPath = defaultSignatureIconPath;
        string? ingestCsvPath = null;
        var ingestOnly = false;
        var overwrite = false;
        var showHelp = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--fixtures-root":
                    fixturesRoot = RequireValue(args, ref index, "--fixtures-root");
                    break;
                case "--output-root":
                    outputRoot = RequireValue(args, ref index, "--output-root");
                    break;
                case "--tessdata-root":
                    tessDataPath = RequireValue(args, ref index, "--tessdata-root");
                    break;
                case "--signature-icon":
                    signatureIconPath = RequireValue(args, ref index, "--signature-icon");
                    break;
                case "--ingest-csv":
                    ingestCsvPath = RequireValue(args, ref index, "--ingest-csv");
                    break;
                case "--ingest-only":
                    ingestOnly = true;
                    break;
                case "--overwrite":
                    overwrite = true;
                    break;
                case "--help":
                case "-h":
                case "/?":
                    showHelp = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[index]}'. Use --help for usage.");
            }
        }

        fixturesRoot = ResolveCliPath(fixturesRoot, currentDirectory);
        outputRoot = ResolveCliPath(outputRoot, currentDirectory);
        tessDataPath = ResolveCliPath(tessDataPath, currentDirectory);
        signatureIconPath = ResolveCliPath(signatureIconPath, currentDirectory);
        ingestCsvPath = ingestCsvPath is null ? null : ResolveCliPath(ingestCsvPath, currentDirectory);

        return new ExportOptions(repoRoot, fixturesRoot, outputRoot, tessDataPath, signatureIconPath, ingestCsvPath, ingestOnly, overwrite, showHelp);
    }

    private static string ResolveCliPath(string path, string currentDirectory)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        return Path.GetFullPath(path, currentDirectory);
    }

    private static string FindRepositoryRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        DirectoryInfo? bestMatch = null;
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            if (File.Exists(Path.Combine(directory.FullName, "BobTools.sln")))
            {
                bestMatch = directory;
            }

            directory = directory.Parent;
        }

        if (bestMatch is not null)
        {
            return bestMatch.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the BobTools repository root from the exporter base directory.");
    }

    public static void WriteHelp()
    {
        Console.WriteLine("SignatureDatasetExporter");
        Console.WriteLine();
        Console.WriteLine("Exports BobTools scan fixtures into a training workspace with normalized crops and a current-OCR baseline report.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --fixtures-root <path>   Source fixture directory. Defaults to BobTools/Assets/TestImages/scanning.");
        Console.WriteLine("  --output-root <path>     Training workspace root. Defaults to tools/signature-recognizer.");
        Console.WriteLine("  --tessdata-root <path>   Tesseract tessdata directory. Defaults to BobTools/Assets/tessdata.");
        Console.WriteLine("  --signature-icon <path>  Signature icon template. Defaults to BobTools/Assets/images/signature_icon.png.");
        Console.WriteLine("  --ingest-csv <path>      CSV of raw captures to promote into fixtures before export.");
        Console.WriteLine("  --ingest-only            Only stage labeled fixtures from --ingest-csv; skip crop/baseline export.");
        Console.WriteLine("  --overwrite              Overwrite copied images and exported crops if they already exist.");
        Console.WriteLine("  --help                   Show this help text.");
        Console.WriteLine();
        Console.WriteLine("Ingest CSV columns:");
        Console.WriteLine("  source_image_path, expected_text, destination_file_name");
        Console.WriteLine("  expected_text may be plain digits like 16900 or formatted text like 16,900.");
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {option}.");
        }

        index++;
        return args[index];
    }
}

internal sealed record ManifestRecord(
    string Id,
    string SourceImagePath,
    string RawImageRelativePath,
    string CropImageRelativePath,
    string? ExpectedText,
    string? ExpectedResource,
    bool IsHardCase,
    string Split,
    string LabelSource,
    string CropStatus,
    string CropStrategy,
    string CropBounds,
    string? Notes)
{
    private static readonly Regex ExpectedValuePattern = new(@"^01_(?<digits>\d{4,5})(?:_.*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly HashSet<string> HardCaseFiles =
    [
        "01_7110.png",
        "01_6000.png",
        "01_8000.png",
        "01_12855.png",
        "01_16720.png",
        "01_6000_dirty.png",
        "01_16900.png"
    ];

    public static ManifestRecord Create(string sourceImagePath, int ordinal)
    {
        var fileName = Path.GetFileName(sourceImagePath);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var expectedText = TryBuildExpectedText(stem, out var parsedExpectedText)
            ? parsedExpectedText
            : null;

        var split = DetermineSplit(fileName, ordinal);
        var id = $"scan_{ordinal:D4}";

        return new ManifestRecord(
            id,
            sourceImagePath,
            Path.Combine("data", "raw", fileName),
            Path.Combine("data", "crops", $"{id}.png"),
            expectedText,
            null,
            HardCaseFiles.Contains(fileName),
            split,
            expectedText is null ? "manual-needed" : "filename-derived",
            "pending",
            "pending",
            string.Empty,
            expectedText is null ? "Provide expected label manually or exclude from training." : null);
    }

    private static bool TryBuildExpectedText(string stem, out string expectedText)
    {
        expectedText = string.Empty;
        var match = ExpectedValuePattern.Match(stem);
        if (!match.Success)
        {
            return false;
        }

        var digits = match.Groups["digits"].Value;
        if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        expectedText = value.ToString("N0", CultureInfo.InvariantCulture);
        return true;
    }

    private static string DetermineSplit(string fileName, int ordinal)
    {
        if (HardCaseFiles.Contains(fileName))
        {
            return "test";
        }

        return (ordinal % 10) switch
        {
            0 => "val",
            1 => "test",
            _ => "train"
        };
    }
}

internal sealed record BaselineRecord(
    string Id,
    string SourceImagePath,
    string CropImageRelativePath,
    string? ExpectedText,
    string? NormalizedText,
    string? RawText,
    double? Confidence,
    string? UsedMode,
    bool? MatchedExpected,
    string Status,
    string? Notes)
{
    public static BaselineRecord Create(ManifestRecord record, BaselineRunResult result)
    {
        var normalizedText = result.NormalizedText;
        bool? matchedExpected = string.IsNullOrWhiteSpace(record.ExpectedText)
            ? (bool?)null
            : string.Equals(NormalizeForComparison(record.ExpectedText), NormalizeForComparison(normalizedText), StringComparison.Ordinal);

        return new BaselineRecord(
            record.Id,
            record.SourceImagePath,
            record.CropImageRelativePath,
            record.ExpectedText,
            normalizedText,
            result.RawText,
            result.Confidence,
            result.UsedMode,
            matchedExpected,
            result.Error is null ? "ok" : "error",
            result.Error);
    }

    private static string NormalizeForComparison(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal).Trim();
    }
}

internal sealed record CropExportResult(
    string Status,
    string Strategy,
    string Bounds,
    string? Note);

internal enum CropMatchQuality
{
    None,
    LooseNumeric,
    StrictNumeric,
    Exact
}

internal sealed record BaselineRunResult(
    string? RawText,
    string? NormalizedText,
    double? Confidence,
    string? UsedMode,
    string? Error);

internal sealed record OcrAttempt(
    string Name,
    string RawText,
    string NormalizedText,
    float Confidence);

internal sealed record CropAttemptCandidate(
    OcrAttempt Attempt,
    string? ExtractedValue,
    double BaseScore)
{
    private static readonly Regex StrictValuePattern = new(@"(?<!\d)\d{1,3}(?:,\d{3})+(?!\d)", RegexOptions.Compiled);

    public static CropAttemptCandidate Create(OcrAttempt attempt, string? expectedText)
    {
        var extractedValue = ProgramMain.ExtractNumericValue(attempt.NormalizedText);
        var score = attempt.Confidence * 100d;
        var expectedDigits = ProgramMain.NormalizeNumericText(expectedText);

        if (!string.IsNullOrWhiteSpace(extractedValue))
        {
            var extractedDigits = ProgramMain.NormalizeNumericText(extractedValue);
            score += extractedValue.Length * 18d;
            if (StrictValuePattern.IsMatch(extractedValue))
            {
                score += 48d;
            }
            else if (extractedValue.Any(char.IsDigit))
            {
                score += 20d;
            }

            if (attempt.Name.Contains("line", StringComparison.OrdinalIgnoreCase))
            {
                score += 12d;
            }

            if (extractedValue.Length == 1)
            {
                score -= 90d;
            }

            if (!string.IsNullOrWhiteSpace(expectedDigits))
            {
                var digitLengthDifference = Math.Abs(extractedDigits.Length - expectedDigits.Length);
                score -= digitLengthDifference * 35d;

                if (extractedDigits.Length > 0 && extractedDigits.Length < Math.Max(2, expectedDigits.Length - 2))
                {
                    score -= 120d;
                }
            }

            if (!string.IsNullOrWhiteSpace(expectedText)
                && string.Equals(extractedDigits, expectedDigits, StringComparison.Ordinal))
            {
                score += 300d;
            }
        }
        else
        {
            score -= 40d;
        }

        return new CropAttemptCandidate(attempt, extractedValue, score);
    }
}

internal sealed record IngestRow(
    string SourceImagePath,
    string? ExpectedText,
    string? DestinationFileName);

internal sealed record CropRegionSelection(
    Rectangle Region,
    double RegionScore,
    CropMatchQuality MatchQuality,
    int SimilarityScore,
    string BestValue,
    string Strategy,
    string? Note);