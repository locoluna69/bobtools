using System.IO;
using System.IO.Compression;
using System.Text;

namespace BobTools.Utils;

internal static class ScanTrainingExportStore
{
    public sealed record ScanTrainingRow(
        string SessionId,
        string EntryId,
        DateTime TimestampUtc,
        string CaptureProfile,
        string SourceImageRelativePath,
        string ProcessedImageRelativePath,
        string DetectedValue,
        string CorrectValue,
        string SignatureSummary,
        string MatchedResource,
        bool IsTargetMatch,
        string OcrMode,
        string OcrSource,
        string ProcessingState,
        string SelectedTargets);

    public static string GetSessionRoot(string sessionId)
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BobTools",
            "LiveCaptures",
            sessionId);
    }

    public static string GetCsvPath(string sessionId)
    {
        var sessionRoot = GetSessionRoot(sessionId);
        Directory.CreateDirectory(sessionRoot);
        return Path.Combine(sessionRoot, "scan_training_labels.csv");
    }

    public static string WriteSessionCsv(string sessionId, IEnumerable<ScanTrainingRow> rows)
    {
        var csvPath = GetCsvPath(sessionId);
        using var writer = new StreamWriter(csvPath, false, new UTF8Encoding(false));

        writer.WriteLine("session_id,entry_id,timestamp_utc,capture_profile,source_image_relative_path,processed_image_relative_path,detected_value,correct_value,signature_summary,matched_resource,is_target_match,ocr_mode,ocr_source,processing_state,selected_targets");

        foreach (var row in rows.OrderByDescending(item => item.TimestampUtc))
        {
            writer.WriteLine(string.Join(",",
                Escape(row.SessionId),
                Escape(row.EntryId),
                Escape(row.TimestampUtc.ToString("O")),
                Escape(row.CaptureProfile),
                Escape(row.SourceImageRelativePath),
                Escape(row.ProcessedImageRelativePath),
                Escape(row.DetectedValue),
                Escape(row.CorrectValue),
                Escape(row.SignatureSummary),
                Escape(row.MatchedResource),
                row.IsTargetMatch ? "true" : "false",
                Escape(row.OcrMode),
                Escape(row.OcrSource),
                Escape(row.ProcessingState),
                Escape(row.SelectedTargets)));
        }

        return csvPath;
    }

    public static string CreateSessionZip(string sessionId)
    {
        var sessionRoot = GetSessionRoot(sessionId);
        if (!Directory.Exists(sessionRoot))
        {
            throw new DirectoryNotFoundException($"No live capture folder exists for session {sessionId}.");
        }

        var exportRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BobTools",
            "Exports");

        Directory.CreateDirectory(exportRoot);

        var zipPath = Path.Combine(exportRoot, $"scan_training_{sessionId}_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        ZipFile.CreateFromDirectory(sessionRoot, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true);
        return zipPath;
    }

    public static string ToSessionRelativePath(string sessionId, string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
        {
            return string.Empty;
        }

        var sessionRoot = Path.GetFullPath(GetSessionRoot(sessionId));
        var fullPath = Path.GetFullPath(absolutePath);
        var rootPrefix = sessionRoot.EndsWith(Path.DirectorySeparatorChar)
            ? sessionRoot
            : sessionRoot + Path.DirectorySeparatorChar;

        if (fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetRelativePath(sessionRoot, fullPath).Replace('\\', '/');
        }

        return Path.GetFileName(fullPath);
    }

    private static string Escape(string value)
    {
        var safeValue = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
        if (safeValue.Contains(',') || safeValue.Contains('"'))
        {
            return $"\"{safeValue.Replace("\"", "\"\"")}\"";
        }

        return safeValue;
    }
}