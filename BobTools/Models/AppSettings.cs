namespace BobTools.Models;

public sealed class AppSettings
{
    public CaptureRegion CaptureRegion { get; set; } = new();
    public CaptureRegion SignatureCaptureRegion { get; set; } = new();
    public CaptureRegion MiningCaptureRegion { get; set; } = new();
    public uint HotkeyModifiers { get; set; } = 0x0001; // ALT
    public uint HotkeyVirtualKey { get; set; } = 0x42; // B
    public bool SaveDebugImages { get; set; }
    public string OcrMode { get; set; } = "auto";
    public string ActiveCaptureProfile { get; set; } = "signature";
    public string ActiveWorkflowMode { get; set; } = "scanning";
}
