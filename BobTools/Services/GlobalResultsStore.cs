using BobTools.Utils;
using System.IO;
using System.Text.Json;
using BobTools.Models;

namespace BobTools.Services;

public sealed class GlobalResultsStore : IGlobalResultsStore
{
    private readonly object _syncRoot = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _storePath;

    public GlobalResultsStore(string storePath)
    {
        _storePath = storePath;
    }

    public bool Exists()
    {
        lock (_syncRoot)
        {
            return File.Exists(_storePath);
        }
    }

    public IReadOnlyList<GlobalResultRecord> LoadAll()
    {
        lock (_syncRoot)
        {
            return LoadDocument().Records
                .OrderByDescending(record => record.Entry.TimestampUtc)
                .ThenByDescending(record => record.LastUpdatedUtc)
                .ToList();
        }
    }

    public void ReplaceAll(IEnumerable<GlobalResultRecord> records)
    {
        lock (_syncRoot)
        {
            var now = DateTime.UtcNow;
            var document = new GlobalResultsStoreDocument
            {
                LastUpdatedUtc = now,
                Records = records
                    .Where(record => record is not null && record.Entry is not null && !string.IsNullOrWhiteSpace(record.Id))
                    .Select(record => NormalizeRecord(record, now))
                    .GroupBy(record => record.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group
                        .OrderByDescending(record => record.LastUpdatedUtc)
                        .ThenByDescending(record => record.Entry.TimestampUtc)
                        .First())
                    .OrderByDescending(record => record.Entry.TimestampUtc)
                    .ToList()
            };

            SaveDocument(document);
        }
    }

    public void Upsert(GlobalResultRecord record)
    {
        lock (_syncRoot)
        {
            if (record.Entry is null || string.IsNullOrWhiteSpace(record.Id))
            {
                return;
            }

            var now = DateTime.UtcNow;
            var document = LoadDocument();
            var normalizedRecord = NormalizeRecord(record, now);
            var existingIndex = document.Records.FindIndex(item => string.Equals(item.Id, normalizedRecord.Id, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                document.Records[existingIndex] = normalizedRecord;
            }
            else
            {
                document.Records.Add(normalizedRecord);
            }

            document.LastUpdatedUtc = now;
            SaveDocument(document);
        }
    }

    public void MarkExported(IEnumerable<string> recordIds, string exportBatchId, DateTime exportedAtUtc)
    {
        lock (_syncRoot)
        {
            var ids = new HashSet<string>(recordIds.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.OrdinalIgnoreCase);
            if (ids.Count == 0)
            {
                return;
            }

            var now = DateTime.UtcNow;
            var document = LoadDocument();
            var updated = false;
            foreach (var record in document.Records)
            {
                if (!ids.Contains(record.Id))
                {
                    continue;
                }

                record.ExportedAtUtc = exportedAtUtc;
                record.ExportBatchId = exportBatchId ?? string.Empty;
                record.LastUpdatedUtc = now;
                updated = true;
            }

            if (!updated)
            {
                return;
            }

            document.LastUpdatedUtc = now;
            SaveDocument(document);
        }
    }

    public void Clear()
    {
        lock (_syncRoot)
        {
            SaveDocument(new GlobalResultsStoreDocument());
        }
    }

    private GlobalResultsStoreDocument LoadDocument()
    {
        if (!File.Exists(_storePath))
        {
            return new GlobalResultsStoreDocument();
        }

        try
        {
            var json = File.ReadAllText(_storePath);
            var document = JsonSerializer.Deserialize<GlobalResultsStoreDocument>(json, _jsonOptions) ?? new GlobalResultsStoreDocument();
            document.Records ??= [];
            return document;
        }
        catch (IOException)
        {
            return new GlobalResultsStoreDocument();
        }
        catch (UnauthorizedAccessException)
        {
            return new GlobalResultsStoreDocument();
        }
        catch (JsonException)
        {
            return new GlobalResultsStoreDocument();
        }
    }

    private void SaveDocument(GlobalResultsStoreDocument document)
    {
        document.Records ??= [];
        document.LastUpdatedUtc = document.LastUpdatedUtc == default ? DateTime.UtcNow : document.LastUpdatedUtc;

        var directory = Path.GetDirectoryName(_storePath)!;
        Directory.CreateDirectory(directory);

        var tempFilePath = Path.Combine(directory, "live-results.tmp");
        var json = JsonSerializer.Serialize(document, _jsonOptions);
        File.WriteAllText(tempFilePath, json);
        File.Move(tempFilePath, _storePath, overwrite: true);
    }

    private static GlobalResultRecord NormalizeRecord(GlobalResultRecord record, DateTime now)
    {
        record.Id = string.IsNullOrWhiteSpace(record.Id) ? record.Entry.Id : record.Id;
        record.Entry.Id = string.IsNullOrWhiteSpace(record.Entry.Id) ? record.Id : record.Entry.Id;
        record.SessionId ??= string.Empty;
        record.ReviewStatus = ScanReviewCatalog.Normalize(record.ReviewStatus);
        record.ExportBatchId ??= string.Empty;
        record.LastUpdatedUtc = now;
        return record;
    }
}