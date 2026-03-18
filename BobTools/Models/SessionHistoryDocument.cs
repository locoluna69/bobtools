namespace BobTools.Models;

public sealed class SessionHistoryDocument
{
    public string SessionId { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
    public List<SessionHistoryEntry> Events { get; set; } = [];
}

public sealed class SessionHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ResultKind { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string WorkflowMode { get; set; } = string.Empty;
    public string CaptureProfile { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string? SourceImagePath { get; set; }
    public string? ProcessedImagePath { get; set; }
    public string? DebugFolderPath { get; set; }
    public string OcrText { get; set; } = string.Empty;
    public string OcrMode { get; set; } = string.Empty;
    public string OcrSourceSummary { get; set; } = string.Empty;
    public string ProcessingState { get; set; } = string.Empty;
    public ScanSessionPayload? Scan { get; set; }
    public MiningSessionPayload? Mining { get; set; }
}

public sealed class ScanSessionPayload
{
    public string Summary { get; set; } = string.Empty;
    public string? MatchedResource { get; set; }
    public bool IsTargetMatch { get; set; }
    public List<string> SelectedTargets { get; set; } = [];
}

public sealed class MiningSessionPayload
{
    public string Summary { get; set; } = string.Empty;
    public RockScan RockScan { get; set; } = new();
}