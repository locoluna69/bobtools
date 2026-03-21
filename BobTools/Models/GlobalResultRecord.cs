using BobTools.Utils;

namespace BobTools.Models;

public sealed class GlobalResultsStoreDocument
{
    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
    public List<GlobalResultRecord> Records { get; set; } = [];
}

public sealed class GlobalResultRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string SessionId { get; set; } = string.Empty;
    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
    public string ReviewStatus { get; set; } = ScanReviewCatalog.Pending;
    public DateTime? ExportedAtUtc { get; set; }
    public string ExportBatchId { get; set; } = string.Empty;
    public SessionHistoryEntry Entry { get; set; } = new();
}