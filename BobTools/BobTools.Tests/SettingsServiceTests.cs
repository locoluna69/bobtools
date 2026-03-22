using System.Text.Json;
using BobTools.Models;
using BobTools.Services;
using BobTools.Utils;
using Xunit;

namespace BobTools.Tests;

public sealed class SettingsServiceTests
{
    [Fact]
    public void Load_NormalizesLegacyAndInvalidValues()
    {
        using var workspace = new TemporaryWorkspace();
        var settingsPath = Path.Combine(workspace.RootPath, "settings.json");

        var settings = new AppSettings
        {
            HotkeyModifiers = 0x0002 | 0x0004,
            HotkeyVirtualKey = 0x51,
            OcrMode = "bad-mode",
            ActiveCaptureProfile = "bad-profile",
            ActiveWorkflowMode = "bad-workflow",
            CaptureRegion = new CaptureRegion { X = 10, Y = 20, Width = 30, Height = 40 },
            SignatureCaptureRegion = new CaptureRegion(),
            MiningCaptureRegion = new CaptureRegion(),
            OverlayOpacity = 99,
            CompactModeScale = 0.1,
            FullModeWindowWidth = -1,
            FullModeWindowHeight = 0,
            CompactModeWindowWidth = 400,
            CompactModeWindowHeight = 0,
            LifetimeSignatureCount = -2,
            LifetimeRockCount = -3,
            LifetimeCommonOreCount = -4,
            LifetimeRareOreCount = -5,
            LifetimeLegendaryOreCount = -6,
            LifetimeMythicOreCount = -7,
            SelectedScanResources = null!,
            LastShip = null!,
            LastStarSystem = null!,
            LastLocationKey = null!,
            RsiHandle = null!
        };

        File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings));

        var service = new SettingsService(settingsPath);

        var loaded = service.Load();

        Assert.Equal(HotkeyCatalog.DefaultModifiers, loaded.HotkeyModifiers);
        Assert.Equal(HotkeyCatalog.DefaultVirtualKey, loaded.HotkeyVirtualKey);
        Assert.Equal(OcrModeCatalog.Auto, loaded.OcrMode);
        Assert.Equal(CaptureProfileCatalog.Signature, loaded.ActiveCaptureProfile);
        Assert.Equal(WorkflowModeCatalog.Scanning, loaded.ActiveWorkflowMode);
        Assert.Equal(settings.CaptureRegion.X, loaded.SignatureCaptureRegion.X);
        Assert.Equal(settings.CaptureRegion.Y, loaded.SignatureCaptureRegion.Y);
        Assert.Equal(settings.CaptureRegion.Width, loaded.SignatureCaptureRegion.Width);
        Assert.Equal(settings.CaptureRegion.Height, loaded.SignatureCaptureRegion.Height);
        Assert.Equal(AppSettings.MaxOverlayOpacity, loaded.OverlayOpacity);
        Assert.Equal(AppSettings.MinCompactModeScale, loaded.CompactModeScale);
        Assert.Equal(0, loaded.FullModeWindowWidth);
        Assert.Equal(0, loaded.FullModeWindowHeight);
        Assert.Equal(400, loaded.CompactModeWindowWidth);
        Assert.Equal(0, loaded.CompactModeWindowHeight);
        Assert.Empty(loaded.SelectedScanResources);
        Assert.Equal(string.Empty, loaded.LastShip);
        Assert.Equal(string.Empty, loaded.LastStarSystem);
        Assert.Equal(string.Empty, loaded.LastLocationKey);
        Assert.Equal(string.Empty, loaded.RsiHandle);
        Assert.Equal(0, loaded.LifetimeSignatureCount);
        Assert.Equal(0, loaded.LifetimeRockCount);
        Assert.Equal(0, loaded.LifetimeCommonOreCount);
        Assert.Equal(0, loaded.LifetimeRareOreCount);
        Assert.Equal(0, loaded.LifetimeLegendaryOreCount);
        Assert.Equal(0, loaded.LifetimeMythicOreCount);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsExpectedValues()
    {
        using var workspace = new TemporaryWorkspace();
        var settingsPath = Path.Combine(workspace.RootPath, "nested", "settings.json");
        var service = new SettingsService(settingsPath);

        var settings = new AppSettings
        {
            HotkeyModifiers = HotkeyCatalog.DefaultModifiers,
            HotkeyVirtualKey = HotkeyCatalog.DefaultVirtualKey,
            OcrMode = OcrModeCatalog.GrayscaleBlock,
            ActiveCaptureProfile = CaptureProfileCatalog.Mining,
            ActiveWorkflowMode = WorkflowModeCatalog.Mining,
            OverlayOpacity = 0.77,
            CompactModeScale = 0.85,
            FullModeWindowWidth = 1280,
            FullModeWindowHeight = 720,
            CompactModeWindowWidth = 560,
            CompactModeWindowHeight = 300,
            LastShip = "Prospector",
            LastStarSystem = "Stanton",
            LastLocationKey = "arc-l1",
            RsiHandle = "pilot",
            SelectedScanResources = ["Quantanium", "Bexalite"],
            LifetimeSignatureCount = 12,
            LifetimeRockCount = 3,
            LifetimeCommonOreCount = 8,
            LifetimeRareOreCount = 4,
            LifetimeLegendaryOreCount = 2,
            LifetimeMythicOreCount = 1
        };

        service.Save(settings);

        var loaded = service.Load();

        Assert.Equal(OcrModeCatalog.GrayscaleBlock, loaded.OcrMode);
        Assert.Equal(CaptureProfileCatalog.Mining, loaded.ActiveCaptureProfile);
        Assert.Equal(WorkflowModeCatalog.Mining, loaded.ActiveWorkflowMode);
        Assert.Equal(0.77, loaded.OverlayOpacity, 3);
        Assert.Equal(0.85, loaded.CompactModeScale, 3);
        Assert.Equal("Prospector", loaded.LastShip);
        Assert.Equal("Stanton", loaded.LastStarSystem);
        Assert.Equal("arc-l1", loaded.LastLocationKey);
        Assert.Equal("pilot", loaded.RsiHandle);
        Assert.Equal(["Quantanium", "Bexalite"], loaded.SelectedScanResources);
        Assert.Equal(12, loaded.LifetimeSignatureCount);
        Assert.Equal(1, loaded.LifetimeMythicOreCount);
    }

    [Fact]
    public void Catalogs_FormatAndMapExpectedValues()
    {
        Assert.Equal("Ctrl+Shift+F10", HotkeyCatalog.Format(HotkeyCatalog.DefaultModifiers, HotkeyCatalog.DefaultVirtualKey));
        Assert.Equal("Unknown", CaptureProfileCatalog.GetDisplayName("unknown"));
        Assert.Equal("Contrast Grayscale Block (from Auto)", OcrModeCatalog.GetDisplayName("grayscale-contrast-block|auto"));
        Assert.Equal(CaptureProfileCatalog.Mining, WorkflowModeCatalog.GetCaptureProfile(WorkflowModeCatalog.Mining));
        Assert.Null(WorkflowModeCatalog.GetCaptureProfile(WorkflowModeCatalog.Offline));
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        public TemporaryWorkspace()
        {
            RootPath = Path.Combine(Path.GetTempPath(), "BobTools.SettingsTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                try
                {
                    Directory.Delete(RootPath, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}