using BobTools.Models;
using BobTools.Utils;
using Xunit;

namespace BobTools.Tests;

public sealed class UtilityTests
{
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
        Assert.Contains("Quantanium", RockScanParser.BuildSummary(result));
    }
}