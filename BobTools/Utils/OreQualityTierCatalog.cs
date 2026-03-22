using System.Collections.Generic;
using System.Globalization;
using BobTools.Models;

namespace BobTools.Utils;

public static class OreQualityTierCatalog
{
    public const string Common = "Common";
    public const string Rare = "Rare";
    public const string Legendary = "Legendary";
    public const string Mythic = "Mythic";

    public static string? GetTierKey(int? quality)
    {
        return quality switch
        {
            >= 0 and <= 749 => Common,
            >= 750 and <= 800 => Rare,
            >= 801 and <= 899 => Legendary,
            >= 900 and <= 999 => Mythic,
            _ => null
        };
    }

    public static string FormatQualityDescriptor(int? quality)
    {
        if (quality is null)
        {
            return string.Empty;
        }

        var qualityText = quality.Value.ToString(CultureInfo.InvariantCulture);
        var tierKey = GetTierKey(quality);
        return string.IsNullOrWhiteSpace(tierKey)
            ? $"Q{qualityText}"
            : $"{tierKey} {qualityText}";
    }

    public static OreQualityBreakdown CountTiers(IEnumerable<RockCompositionEntry> entries)
    {
        var common = 0;
        var rare = 0;
        var legendary = 0;
        var mythic = 0;

        foreach (var entry in entries)
        {
            switch (GetTierKey(entry.Quality))
            {
                case Common:
                    common++;
                    break;
                case Rare:
                    rare++;
                    break;
                case Legendary:
                    legendary++;
                    break;
                case Mythic:
                    mythic++;
                    break;
            }
        }

        return new OreQualityBreakdown(common, rare, legendary, mythic);
    }

    public static CompactOreHighlight GetCompactHighlight(int? quality, bool isTargetMatch)
    {
        return GetTierKey(quality) switch
        {
            Rare => new CompactOreHighlight("#0B2545", "#60A5FA", "#DBEAFE"),
            Legendary => new CompactOreHighlight("#3F2A00", "#F59E0B", "#FDE68A"),
            Mythic => new CompactOreHighlight("#3F1212", "#EF4444", "#FECACA"),
            _ when isTargetMatch => new CompactOreHighlight("#3F3A2200", "#F59E0B", "#FCD34D"),
            _ => new CompactOreHighlight("#052E2B", "#0F766E", "#D1FAE5")
        };
    }
}

public readonly record struct OreQualityBreakdown(int CommonCount, int RareCount, int LegendaryCount, int MythicCount);

public readonly record struct CompactOreHighlight(string BackgroundBrush, string BorderBrush, string ForegroundBrush);