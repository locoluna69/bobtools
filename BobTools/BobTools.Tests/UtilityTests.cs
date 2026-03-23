using System.Drawing;
using BobTools.Models;
using BobTools.Utils;
using System.Reflection;
using System.Windows.Media.Imaging;
using Xunit;

namespace BobTools.Tests;

public sealed class UtilityTests
{
    [Fact]
    public void MiningContextCatalog_GetLocationOptions_IncludesPlanetsStationsAndPois()
    {
        var options = MiningContextCatalog.GetLocationOptions();

        Assert.Contains(options, option => string.Equals(option.DisplayName, "Terminus", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(options, option => string.Equals(option.DisplayName, "HUR-L1", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(options, option => string.Equals(option.DisplayName, "RAB-KILO", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(options, option => string.Equals(option.DisplayName, "Mining Base #E38-S5G", StringComparison.OrdinalIgnoreCase));

        var rabKilo = Assert.Single(options, option => string.Equals(option.DisplayName, "RAB-KILO", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Pyro", rabKilo.SystemName);
        Assert.Contains("RAB-KILO", rabKilo.DisplayPath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Pyro /", rabKilo.DisplayPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OfflineMiningResultItem_ReviewEdit_AutoSavesWithoutManualCommand()
    {
        using var workspace = new TemporaryWorkspace();

        RockScan? previousPersistedScan = null;
        RockScan? updatedPersistedScan = null;
        var persistCallCount = 0;

        var item = new OfflineMiningResultItem(
            sourceImagePath: Path.Combine(workspace.RootPath, "capture.png"),
            processedImagePath: null,
            extractedScan: new RockScan
            {
                Id = "scan-1",
                SessionId = "session-1",
                TimestampUtc = new DateTime(2026, 3, 22, 18, 0, 0, DateTimeKind.Utc),
                Material = "Quantanium",
                Mass = 4500,
                Resistance = 42.5m,
                Instability = 0.35m,
                Composition = 12.5m,
                Location = "Lyria",
                Elements =
                [
                    new RockCompositionEntry("Quantanium", 12.5m, 950)
                ]
            },
            summary: "Initial summary",
            jsonPath: null,
            csvPath: null,
            persistCallback: (_, previousScan, updatedScan) =>
            {
                Interlocked.Increment(ref persistCallCount);
                previousPersistedScan = previousScan;
                updatedPersistedScan = updatedScan;
            },
            enableAutoSave: true);

        item.ApplyReviewMetadata(ScanReviewCatalog.Reviewed, DateTime.UtcNow, "batch-1");
        item.ReviewStatus = ScanReviewCatalog.Pending;

        await WaitForConditionAsync(
            () => Volatile.Read(ref persistCallCount) == 1 && string.Equals(item.AutoSaveStatus, "Saved", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));

        Assert.Equal(1, persistCallCount);
        Assert.Null(previousPersistedScan);
        Assert.Null(updatedPersistedScan);
        Assert.False(item.IsDirty);
        Assert.Equal("Saved", item.AutoSaveStatus);
        Assert.Equal(ScanReviewCatalog.Pending, item.ReviewStatus);
        Assert.Null(item.ExportedAtUtc);
        Assert.Equal(string.Empty, item.ExportBatchId);
    }

    [Fact]
    public void OfflineMiningResultItem_CompactMetricsSummary_ShowsCompositionAsDecimal()
    {
        var item = new OfflineMiningResultItem(
            sourceImagePath: Path.Combine(Path.GetTempPath(), "compact-metrics.png"),
            processedImagePath: null,
            extractedScan: new RockScan
            {
                Material = "Quantanium",
                Mass = 4500,
                Resistance = 42.5m,
                Instability = 0.35m,
                Composition = 24.43m
            },
            summary: "Summary",
            jsonPath: null,
            csvPath: null);

        Assert.Contains("Comp 24.43", item.CompactMetricsSummary);
        Assert.DoesNotContain("Comp 24.43%", item.CompactMetricsSummary);
    }

    [Fact]
    public void OreQualityTierCatalog_CountTiersAndFormatDescriptor_ReturnExpectedValues()
    {
        var entries = new[]
        {
            new RockCompositionEntry("Quartz", 10m, 700),
            new RockCompositionEntry("Gold", 20m, 780),
            new RockCompositionEntry("Bexalite", 30m, 850),
            new RockCompositionEntry("Quantanium", 40m, 920),
            new RockCompositionEntry("Inert", null, null)
        };

        var breakdown = OreQualityTierCatalog.CountTiers(entries);

        Assert.Equal(1, breakdown.CommonCount);
        Assert.Equal(1, breakdown.RareCount);
        Assert.Equal(1, breakdown.LegendaryCount);
        Assert.Equal(1, breakdown.MythicCount);
        Assert.Equal("Rare 780", OreQualityTierCatalog.FormatQualityDescriptor(780));
        Assert.Equal(string.Empty, OreQualityTierCatalog.FormatQualityDescriptor(null));
    }

    [Fact]
    public void OreQualityTierCatalog_CalculateLifetimeDelta_TracksTierChangesAcrossEdits()
    {
        var previousScan = new RockScan
        {
            Material = "Quantanium",
            Elements =
            [
                new RockCompositionEntry("Ore A", 10m, 740),
                new RockCompositionEntry("Ore B", 15m, 780),
                new RockCompositionEntry("Ore C", 20m, 920)
            ]
        };

        var updatedScan = previousScan with
        {
            Elements =
            [
                new RockCompositionEntry("Ore A", 10m, 780),
                new RockCompositionEntry("Ore B", 15m, 850),
                new RockCompositionEntry("Ore C", 20m, null)
            ]
        };

        var delta = OreQualityTierCatalog.CalculateLifetimeDelta(previousScan, updatedScan);

        Assert.Equal(0, delta.RockDelta);
        Assert.Equal(-1, delta.CommonDelta);
        Assert.Equal(0, delta.RareDelta);
        Assert.Equal(1, delta.LegendaryDelta);
        Assert.Equal(-1, delta.MythicDelta);
    }

    [Fact]
    public void RockScanParser_TryParse_ParsesStructuredScanText()
    {
        const string text = """
QUANTANIUM
MASS: 4500
RESISTANCE: 42.5%
INSTABILITY: 0.35
COMPOSITION: 12.5%
12.5% QUANTANIUM 950
""";

        var success = RockScanParser.TryParse(text, out var result);

        Assert.True(success);
        Assert.Equal("Quantanium", result.Material);
        Assert.Equal(4500, result.Mass);
        Assert.Equal(42.5m, result.Resistance);
        Assert.Equal(0.35m, result.Instability);
        Assert.Equal(12.5m, result.Composition);
        var element = Assert.Single(result.Elements);
        Assert.Equal("Quantanium", element.Element);
        Assert.Equal(12.5m, element.Percentage);
        Assert.Equal(950, element.Quality);
        var summary = RockScanParser.BuildSummary(result);
        Assert.Contains("Quantanium", summary);
        Assert.Contains("Composition 12.5", summary);
        Assert.DoesNotContain("Composition 12.5%", summary);
    }

    [Fact]
    public void MiningOcrSegmentation_DetectRowBounds_FindsSeparatedTextBands()
    {
        using var bitmap = new Bitmap(180, 120);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        using var brush = new SolidBrush(Color.Black);

        graphics.FillRectangle(brush, 12, 10, 140, 8);
        graphics.FillRectangle(brush, 14, 42, 132, 9);
        graphics.FillRectangle(brush, 18, 78, 128, 8);

        var rowBounds = MiningOcrSegmentation.DetectRowBounds(bitmap);

        Assert.Equal(3, rowBounds.Count);
        Assert.InRange(rowBounds[0].Y, 8, 12);
        Assert.InRange(rowBounds[1].Y, 40, 44);
        Assert.InRange(rowBounds[2].Y, 76, 80);
        Assert.All(rowBounds, bounds => Assert.Equal(bitmap.Width, bounds.Width));
    }

    [Fact]
    public void MiningOcrSegmentation_SelectPreferredCandidate_PrefersMoreCompleteParse()
    {
        var wholeRegion = MiningOcrSegmentation.CreateCandidate(
            MiningOcrStrategyCatalog.WholeRegion,
            OcrModeCatalog.GrayscaleBlock,
            "QUANTANIUM\nMASS: 4500\n",
            1);
        var rowSegmented = MiningOcrSegmentation.CreateCandidate(
            MiningOcrStrategyCatalog.RowSegmented,
            OcrModeCatalog.GrayscaleBlock,
            "QUANTANIUM\nMASS: 4500\nRESISTANCE: 42.5%\nINSTABILITY: 0.35\nCOMPOSITION: 12.5%\n12.5% QUANTANIUM 950",
            5);

        var selected = MiningOcrSegmentation.SelectPreferredCandidate([wholeRegion, rowSegmented]);

        Assert.Equal(MiningOcrStrategyCatalog.RowSegmented, selected.Strategy);
        Assert.True(selected.ParseScore > wholeRegion.ParseScore);
        Assert.True(selected.PopulatedFieldCount > wholeRegion.PopulatedFieldCount);
    }

    [Fact]
    public void MiningOcrSegmentation_SelectPreferredCandidate_PrefersWholeRegionOnExactTie()
    {
        var wholeRegion = MiningOcrSegmentation.CreateCandidate(
            MiningOcrStrategyCatalog.WholeRegion,
            OcrModeCatalog.GrayscaleBlock,
            "QUANTANIUM\nMASS: 4500",
            1);
        var rowSegmented = MiningOcrSegmentation.CreateCandidate(
            MiningOcrStrategyCatalog.RowSegmented,
            OcrModeCatalog.GrayscaleBlock,
            "QUANTANIUM\nMASS: 4500",
            2);

        var selected = MiningOcrSegmentation.SelectPreferredCandidate([wholeRegion, rowSegmented]);

        Assert.Equal(MiningOcrStrategyCatalog.WholeRegion, selected.Strategy);
    }

    [Fact]
    public void PngMetadataWriter_BuildMetadata_WritesExpectedImageProperties()
    {
        var sessionMetadata = new SessionMetadata
        {
            RsiHandle = "pilot",
            Ship = "Prospector",
            StarSystem = "Pyro",
            LocationKey = "pyro-ruin-station",
            LocationName = "Ruin Station",
            LocationPath = "Pyro / Ruin Station"
        };

        var metadata = BuildMetadataForTest(
            sessionMetadata,
            "session-123",
            CaptureProfileCatalog.Mining,
            "source",
            new Dictionary<string, string?>
            {
                ["BobTools.SourceType"] = "live",
                ["BobTools.SourceImagePath"] = "captures/mine_001.png"
            });

        Assert.Equal("BobTools", GetMetadataQueryValue(metadata, "/xmp/xmp:CreatorTool"));
        Assert.Equal("session-123", GetMetadataQueryValue(metadata, "/xmp/BobTools:SessionId"));
        Assert.Equal("pilot", GetMetadataQueryValue(metadata, "/xmp/BobTools:RsiHandle"));
        Assert.Equal("Prospector", GetMetadataQueryValue(metadata, "/xmp/BobTools:Ship"));
        Assert.Equal("Pyro", GetMetadataQueryValue(metadata, "/xmp/BobTools:StarSystem"));
        Assert.Equal("pyro-ruin-station", GetMetadataQueryValue(metadata, "/xmp/BobTools:LocationKey"));
        Assert.Equal("Ruin Station", GetMetadataQueryValue(metadata, "/xmp/BobTools:LocationName"));
        Assert.Equal("Pyro / Ruin Station", GetMetadataQueryValue(metadata, "/xmp/BobTools:LocationPath"));
        Assert.Equal(CaptureProfileCatalog.Mining, GetMetadataQueryValue(metadata, "/xmp/BobTools:CaptureProfile"));
        Assert.Equal("source", GetMetadataQueryValue(metadata, "/xmp/BobTools:ImageRole"));
        Assert.Equal("live", GetMetadataQueryValue(metadata, "/xmp/BobTools:SourceType"));
        Assert.Equal("captures/mine_001.png", GetMetadataQueryValue(metadata, "/xmp/BobTools:SourceImagePath"));

        var timestampText = GetMetadataQueryValue(metadata, "/xmp/xmp:CreateDate");
        Assert.True(DateTime.TryParse(timestampText, out _));
    }

    [Fact]
    public void PngMetadataWriter_BuildMetadata_WritesExpectedXmpProperties()
    {
        var sessionMetadata = new SessionMetadata
        {
            RsiHandle = "miner-01",
            Ship = "MOLE",
            StarSystem = "Stanton",
            LocationKey = "stanton-aaron-halo",
            LocationName = "Aaron Halo",
            LocationPath = "Stanton / Aaron Halo"
        };

        var metadata = BuildMetadataForTest(
            sessionMetadata,
            "session-456",
            CaptureProfileCatalog.Signature,
            "processed",
            new Dictionary<string, string?>
            {
                ["BobTools.SourceType"] = "offline"
            });

        Assert.Equal("BobTools", GetMetadataQueryValue(metadata, "/xmp/xmp:CreatorTool"));
        Assert.Equal("session-456", GetMetadataQueryValue(metadata, "/xmp/BobTools:SessionId"));
        Assert.Equal("miner-01", GetMetadataQueryValue(metadata, "/xmp/BobTools:RsiHandle"));
        Assert.Equal("MOLE", GetMetadataQueryValue(metadata, "/xmp/BobTools:Ship"));
        Assert.Equal("Stanton", GetMetadataQueryValue(metadata, "/xmp/BobTools:StarSystem"));
        Assert.Equal("stanton-aaron-halo", GetMetadataQueryValue(metadata, "/xmp/BobTools:LocationKey"));
        Assert.Equal("Aaron Halo", GetMetadataQueryValue(metadata, "/xmp/BobTools:LocationName"));
        Assert.Equal("Stanton / Aaron Halo", GetMetadataQueryValue(metadata, "/xmp/BobTools:LocationPath"));
        Assert.Equal(CaptureProfileCatalog.Signature, GetMetadataQueryValue(metadata, "/xmp/BobTools:CaptureProfile"));
        Assert.Equal("processed", GetMetadataQueryValue(metadata, "/xmp/BobTools:ImageRole"));
        Assert.Equal("offline", GetMetadataQueryValue(metadata, "/xmp/BobTools:SourceType"));
        Assert.True(DateTime.TryParse(GetMetadataQueryValue(metadata, "/xmp/xmp:CreateDate"), out _));
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.True(condition(), "Timed out waiting for the expected autosave state.");
    }

    private static BitmapMetadata BuildMetadataForTest(
        SessionMetadata sessionMetadata,
        string sessionId,
        string captureProfile,
        string imageRole,
        IReadOnlyDictionary<string, string?>? extraMetadata = null)
    {
        var method = typeof(PngMetadataWriter).GetMethod("BuildMetadata", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var metadata = method!.Invoke(null, [sessionMetadata, sessionId, captureProfile, imageRole, extraMetadata]);
        return Assert.IsType<BitmapMetadata>(metadata);
    }

    private static string GetMetadataQueryValue(BitmapMetadata metadata, string query)
    {
        return metadata.GetQuery(query) as string ?? string.Empty;
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        public TemporaryWorkspace()
        {
            RootPath = Path.Combine(Path.GetTempPath(), "BobTools.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public void Dispose()
        {
            if (!Directory.Exists(RootPath))
            {
                return;
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();

            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Directory.Delete(RootPath, recursive: true);
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(100);
                }
                catch (UnauthorizedAccessException) when (attempt < 4)
                {
                    Thread.Sleep(100);
                }
                catch (IOException)
                {
                    break;
                }
                catch (UnauthorizedAccessException)
                {
                    break;
                }
            }
        }
    }
}