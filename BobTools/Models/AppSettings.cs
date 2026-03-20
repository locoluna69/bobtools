using System.Text.Json.Serialization;
using BobTools.Utils;

namespace BobTools.Models;

public sealed class AppSettings
{
    public const double DefaultOverlayOpacity = 0.94;
    public const double MinOverlayOpacity = 0.45;
    public const double MaxOverlayOpacity = 1.0;
    public const double DefaultCompactModeScale = 0.5;
    public const double MinCompactModeScale = 0.4;
    public const double MaxCompactModeScale = 1.25;

    public CaptureRegion CaptureRegion { get; set; } = new();
    public CaptureRegion SignatureCaptureRegion { get; set; } = new();
    public CaptureRegion MiningCaptureRegion { get; set; } = new();
    public uint HotkeyModifiers { get; set; } = HotkeyCatalog.DefaultModifiers;
    public uint HotkeyVirtualKey { get; set; } = HotkeyCatalog.DefaultVirtualKey;
    public bool IsCompactMode { get; set; }
    public double OverlayOpacity { get; set; } = DefaultOverlayOpacity;
    public double CompactModeScale { get; set; } = DefaultCompactModeScale;
    public double FullModeWindowWidth { get; set; }
    public double FullModeWindowHeight { get; set; }
    public double CompactModeWindowWidth { get; set; }
    public double CompactModeWindowHeight { get; set; }
    public bool SaveDebugImages { get; set; }
    public bool CaptureOnlyMode { get; set; }
    public string OcrMode { get; set; } = "auto";
    public string ActiveCaptureProfile { get; set; } = "signature";
    public string ActiveWorkflowMode { get; set; } = "scanning";
    public string LastShip { get; set; } = string.Empty;
    public string LastStarSystem { get; set; } = string.Empty;
    public string LastLocationKey { get; set; } = string.Empty;
    public string RsiHandle { get; set; } = string.Empty;
    public int LifetimeSignatureCount { get; set; }
    public int LifetimeRockCount { get; set; }
    public int LifetimeHighValueOreCount { get; set; }
    public List<string> SelectedScanResources { get; set; } = [];

    [JsonPropertyName("lastMiningShip")]
    public string LegacyLastMiningShip
    {
        set => LastShip = value ?? string.Empty;
    }

    [JsonPropertyName("lastMiningSystem")]
    public string LegacyLastMiningSystem
    {
        set => LastStarSystem = value ?? string.Empty;
    }

    [JsonPropertyName("lastMiningLocationKey")]
    public string LegacyLastMiningLocationKey
    {
        set => LastLocationKey = value ?? string.Empty;
    }
}
