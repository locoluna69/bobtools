using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BobTools.Services;

internal sealed class OcrPipelineLogger
{
    private readonly object _syncRoot = new();
    private readonly string _logsRootPath;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public OcrPipelineLogger(string logsRootPath)
    {
        _logsRootPath = logsRootPath;
    }

    public void Write(OcrPipelineLogEntry entry)
    {
        Directory.CreateDirectory(_logsRootPath);
        var logFilePath = Path.Combine(_logsRootPath, $"ocr-pipeline-{DateTime.UtcNow:yyyyMMdd}.jsonl");
        var line = JsonSerializer.Serialize(entry, _jsonOptions);

        lock (_syncRoot)
        {
            File.AppendAllText(logFilePath, line + Environment.NewLine);
        }
    }
}

internal sealed class OcrPipelineLogEntry
{
    public string RequestId { get; set; } = Guid.NewGuid().ToString();
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "completed";
    public string? CaptureProfile { get; set; }
    public string? PreferredMode { get; set; }
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    public bool DebugArtifactsEnabled { get; set; }
    public bool SignatureFastPathAttempted { get; set; }
    public bool SignatureFastPathUsed { get; set; }
    public bool FallbackPipelineUsed { get; set; }
    public string? DefaultEngineLease { get; set; }
    public string? NumericEngineLease { get; set; }
    public int AttemptCount { get; set; }
    public int SignatureCandidateRegionCount { get; set; }
    public int SignatureRegionsProcessedCount { get; set; }
    public int SignatureCandidateAttemptCount { get; set; }
    public bool SignatureEarlyStopTriggered { get; set; }
    public int? SignatureEarlyStopRegionIndex { get; set; }
    public int? SignatureWinningRegionIndex { get; set; }
    public string? SignatureFastPathRejectedReason { get; set; }
    public int? MaxCandidateAttemptsInRegion { get; set; }
    public double? AverageCandidateAttemptsPerProcessedRegion { get; set; }
    public string? ResultMode { get; set; }
    public double? ResultConfidence { get; set; }
    public int ResultTextLength { get; set; }
    public string? Exception { get; set; }
    public Dictionary<string, double> StageTimingsMs { get; set; } = [];
    public List<OcrCandidateRegionLogEntry> CandidateRegions { get; set; } = [];
}

internal sealed class OcrCandidateRegionLogEntry
{
    public int RegionIndex { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int AttemptCount { get; set; }
    public int CandidateCount { get; set; }
    public double ElapsedMs { get; set; }
    public string? BestValue { get; set; }
    public double? BestScore { get; set; }
    public bool HasStrictNumericMatch { get; set; }
    public bool EarlyStopTriggered { get; set; }
}