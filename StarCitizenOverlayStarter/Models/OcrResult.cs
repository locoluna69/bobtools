namespace StarCitizenOverlayStarter.Models;

public sealed class OcrResult
{
    public string RawText { get; init; } = string.Empty;
    public string NormalizedText { get; init; } = string.Empty;
    public double Confidence { get; init; }
    public string UsedMode { get; init; } = string.Empty;
}
