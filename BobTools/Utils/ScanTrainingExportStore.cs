using System.IO;
using System.IO.Compression;
using System.Text;

namespace BobTools.Utils;

internal static class ScanTrainingExportStore
{
    public sealed record ScanTrainingRow(
        string ExportBatchId,
        string SessionId,
        string EntryId,
        DateTime TimestampUtc,
        string CaptureProfile,
        string? SourceImagePath,
        string? ProcessedImagePath,
        string DetectedValue,
        string CorrectValue,
        string SignatureSummary,
        string MatchedResource,
        bool IsTargetMatch,
        string OcrMode,
        string OcrSource,
        string ProcessingState,
        string SelectedTargets);

    public static string ExportBatch(string exportBatchId, IEnumerable<ScanTrainingRow> rows)
    {
        var rowList = rows.OrderByDescending(item => item.TimestampUtc).ToList();
        if (rowList.Count == 0)
        {
            throw new InvalidOperationException("No scan training rows were provided for export.");
        }

        var batchRoot = GetBatchRoot(exportBatchId);
        if (Directory.Exists(batchRoot))
        {
            Directory.Delete(batchRoot, recursive: true);
        }

        Directory.CreateDirectory(batchRoot);
        Directory.CreateDirectory(Path.Combine(batchRoot, "images", "source"));
        Directory.CreateDirectory(Path.Combine(batchRoot, "images", "processed"));

        var manifestPath = Path.Combine(batchRoot, "manifest.csv");
        using (var writer = new StreamWriter(manifestPath, false, new UTF8Encoding(false)))
        {
            writer.WriteLine("export_batch_id,session_id,entry_id,timestamp_utc,capture_profile,source_image_relative_path,processed_image_relative_path,detected_value,correct_value,signature_summary,matched_resource,is_target_match,ocr_mode,ocr_source,processing_state,selected_targets");

            foreach (var row in rowList)
            {
                var sourceRelativePath = CopyImageIntoBatch(batchRoot, "source", row.EntryId, row.SourceImagePath);
                var processedRelativePath = CopyImageIntoBatch(batchRoot, "processed", row.EntryId, row.ProcessedImagePath);

                writer.WriteLine(string.Join(",",
                    Escape(row.ExportBatchId),
                    Escape(row.SessionId),
                    Escape(row.EntryId),
                    Escape(row.TimestampUtc.ToString("O")),
                    Escape(row.CaptureProfile),
                    Escape(sourceRelativePath),
                    Escape(processedRelativePath),
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
        }

        var zipPath = Path.Combine(GetExportsRoot(), $"{exportBatchId}.zip");
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        ZipFile.CreateFromDirectory(batchRoot, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true);
        return zipPath;
    }

    private static string GetBatchRoot(string exportBatchId)
    {
        return Path.Combine(GetExportsRoot(), exportBatchId);
    }

    private static string GetExportsRoot()
    {
        var exportRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BobTools",
            "Exports",
            "scan-training");

        Directory.CreateDirectory(exportRoot);
        return exportRoot;
    }

    private static string CopyImageIntoBatch(string batchRoot, string imageType, string entryId, string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath) || !File.Exists(absolutePath))
        {
            return string.Empty;
        }

        var extension = Path.GetExtension(absolutePath);
        var safeExtension = string.IsNullOrWhiteSpace(extension) ? ".png" : extension;
        var fileName = $"{entryId}_{imageType}{safeExtension}";
        var targetPath = Path.Combine(batchRoot, "images", imageType, fileName);
        File.Copy(absolutePath, targetPath, overwrite: true);
        return Path.Combine("images", imageType, fileName).Replace('\\', '/');
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