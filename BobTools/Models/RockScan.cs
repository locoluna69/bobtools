namespace BobTools.Models;

public sealed record RockScan
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string SessionId { get; init; } = string.Empty;
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public string RsiHandle { get; init; } = string.Empty;
    public string Ship { get; init; } = string.Empty;
    public string StarSystem { get; init; } = string.Empty;
    public string LocationKey { get; init; } = string.Empty;
    public string LocationName { get; init; } = string.Empty;
    public string LocationPath { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public string SourceType { get; init; } = string.Empty;
    public string CaptureProfile { get; init; } = string.Empty;
    public string Material { get; init; } = string.Empty;
    public int? Mass { get; init; }
    public decimal? Resistance { get; init; }
    public decimal? Instability { get; init; }
    public decimal? Composition { get; init; }
    public IReadOnlyList<RockCompositionEntry> Elements { get; init; } = Array.Empty<RockCompositionEntry>();
    public IReadOnlyList<string> ParseWarnings { get; init; } = Array.Empty<string>();
    public string SourceText { get; init; } = string.Empty;
    public string? SourceImagePath { get; init; }
    public string? DebugFolderPath { get; init; }
}