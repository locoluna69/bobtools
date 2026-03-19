using BobTools.Models;
using BobTools.Utils;
using System;
using System.IO;
using System.Text.Json;

namespace BobTools.Services;

public sealed class SettingsService : ISettingsService
{
    private const uint LegacyDefaultModifiers = 0x0002 | 0x0004;
    private const uint LegacyDefaultVirtualKey = 0x51;
    private const uint PriorDefaultModifiers = 0x0001;
    private const uint PriorDefaultVirtualKey = 0x42;
    private readonly string _settingsPath;

    public SettingsService(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        var json = File.ReadAllText(_settingsPath);
        var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();

        if ((settings.HotkeyModifiers == LegacyDefaultModifiers && settings.HotkeyVirtualKey == LegacyDefaultVirtualKey)
            || (settings.HotkeyModifiers == PriorDefaultModifiers && settings.HotkeyVirtualKey == PriorDefaultVirtualKey))
        {
            settings.HotkeyModifiers = HotkeyCatalog.DefaultModifiers;
            settings.HotkeyVirtualKey = HotkeyCatalog.DefaultVirtualKey;
        }

        if (!HotkeyCatalog.IsSupported(settings.HotkeyModifiers, settings.HotkeyVirtualKey))
        {
            settings.HotkeyModifiers = HotkeyCatalog.DefaultModifiers;
            settings.HotkeyVirtualKey = HotkeyCatalog.DefaultVirtualKey;
        }

        if (!OcrModeCatalog.IsValid(settings.OcrMode))
        {
            settings.OcrMode = OcrModeCatalog.Auto;
        }

        if (!settings.SignatureCaptureRegion.IsValid && !settings.MiningCaptureRegion.IsValid && settings.CaptureRegion.IsValid)
        {
            settings.SignatureCaptureRegion = settings.CaptureRegion;
        }

        if (!CaptureProfileCatalog.IsValid(settings.ActiveCaptureProfile))
        {
            settings.ActiveCaptureProfile = CaptureProfileCatalog.Signature;
        }

        if (!WorkflowModeCatalog.IsValid(settings.ActiveWorkflowMode))
        {
            settings.ActiveWorkflowMode = string.Equals(settings.ActiveCaptureProfile, CaptureProfileCatalog.Mining, StringComparison.OrdinalIgnoreCase)
                ? WorkflowModeCatalog.Mining
                : WorkflowModeCatalog.Scanning;
        }

        settings.LastShip ??= string.Empty;
        settings.LastStarSystem ??= string.Empty;
        settings.LastLocationKey ??= string.Empty;
        settings.RsiHandle ??= string.Empty;
        settings.SelectedScanResources ??= [];
        settings.LifetimeSignatureCount = Math.Max(0, settings.LifetimeSignatureCount);
        settings.LifetimeRockCount = Math.Max(0, settings.LifetimeRockCount);
        settings.LifetimeHighValueOreCount = Math.Max(0, settings.LifetimeHighValueOreCount);

        return settings;
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_settingsPath, json);
    }
}
