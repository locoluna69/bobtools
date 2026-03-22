using BobTools.Utils;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;
using System.Text.Json;
using BobTools.Models;

namespace BobTools.Services;

public sealed class GlobalResultsStore : IGlobalResultsStore
{
    private readonly object _syncRoot = new();
    private readonly string _databasePath;
    private readonly string _legacyStorePath;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public GlobalResultsStore(string databasePath, string legacyStorePath)
    {
        _databasePath = databasePath;
        _legacyStorePath = legacyStorePath;
    }

    public bool Exists()
    {
        lock (_syncRoot)
        {
            EnsureDatabase();
            MigrateLegacyStoreIfNeeded();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM global_results LIMIT 1);";
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
        }
    }

    public IReadOnlyList<GlobalResultRecord> LoadAll()
    {
        lock (_syncRoot)
        {
            EnsureDatabase();
            MigrateLegacyStoreIfNeeded();

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT id, session_id, last_updated_utc, review_status, exported_at_utc, export_batch_id, entry_json
FROM global_results
ORDER BY entry_timestamp_utc DESC, last_updated_utc DESC;";

            using var reader = command.ExecuteReader();
            var records = new List<GlobalResultRecord>();
            while (reader.Read())
            {
                var entry = DeserializeEntry(reader.GetString(6));
                records.Add(new GlobalResultRecord
                {
                    Id = reader.GetString(0),
                    SessionId = reader.GetString(1),
                    LastUpdatedUtc = ParseDateTime(reader.GetString(2)),
                    ReviewStatus = ScanReviewCatalog.Normalize(reader.GetString(3)),
                    ExportedAtUtc = reader.IsDBNull(4) ? null : ParseDateTime(reader.GetString(4)),
                    ExportBatchId = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                    Entry = entry
                });
            }

            return records;
        }
    }

    public void ReplaceAll(IEnumerable<GlobalResultRecord> records)
    {
        lock (_syncRoot)
        {
            EnsureDatabase();
            var now = DateTime.UtcNow;
            var normalizedRecords = records
                .Where(record => record is not null && record.Entry is not null && !string.IsNullOrWhiteSpace(record.Id))
                .Select(record => NormalizeRecord(record, now))
                .GroupBy(record => record.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(record => record.LastUpdatedUtc)
                    .ThenByDescending(record => record.Entry.TimestampUtc)
                    .First())
                .OrderByDescending(record => record.Entry.TimestampUtc)
                .ToList();

            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using (var deleteCommand = connection.CreateCommand())
            {
                deleteCommand.Transaction = transaction;
                deleteCommand.CommandText = "DELETE FROM global_results;";
                deleteCommand.ExecuteNonQuery();
            }

            foreach (var record in normalizedRecords)
            {
                UpsertRecord(connection, transaction, record);
            }

            transaction.Commit();
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

            EnsureDatabase();
            var now = DateTime.UtcNow;
            var normalizedRecord = NormalizeRecord(record, now);
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            UpsertRecord(connection, transaction, normalizedRecord);
            transaction.Commit();
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

            EnsureDatabase();
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
UPDATE global_results
SET review_status = review_status,
    exported_at_utc = $exportedAtUtc,
    export_batch_id = $exportBatchId,
    last_updated_utc = $lastUpdatedUtc
WHERE id = $id;";

            var exportedAtParam = command.Parameters.Add("$exportedAtUtc", SqliteType.Text);
            var exportBatchParam = command.Parameters.Add("$exportBatchId", SqliteType.Text);
            var updatedAtParam = command.Parameters.Add("$lastUpdatedUtc", SqliteType.Text);
            var idParam = command.Parameters.Add("$id", SqliteType.Text);
            exportedAtParam.Value = exportedAtUtc.ToString("O", CultureInfo.InvariantCulture);
            exportBatchParam.Value = exportBatchId ?? string.Empty;
            updatedAtParam.Value = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

            foreach (var id in ids)
            {
                idParam.Value = id;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public void Clear()
    {
        lock (_syncRoot)
        {
            EnsureDatabase();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM global_results;";
            command.ExecuteNonQuery();
        }
    }

    private void EnsureDatabase()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
CREATE TABLE IF NOT EXISTS global_results (
    id TEXT PRIMARY KEY,
    session_id TEXT NOT NULL,
    last_updated_utc TEXT NOT NULL,
    review_status TEXT NOT NULL,
    exported_at_utc TEXT NULL,
    export_batch_id TEXT NOT NULL,
    entry_timestamp_utc TEXT NOT NULL,
    entry_json TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_global_results_entry_timestamp
    ON global_results(entry_timestamp_utc DESC, last_updated_utc DESC);";
        command.ExecuteNonQuery();
    }

    private void MigrateLegacyStoreIfNeeded()
    {
        using var connection = OpenConnection();
        using var existsCommand = connection.CreateCommand();
        existsCommand.CommandText = "SELECT EXISTS(SELECT 1 FROM global_results LIMIT 1);";
        var hasRows = Convert.ToInt32(existsCommand.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
        if (hasRows || !File.Exists(_legacyStorePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_legacyStorePath);
            var document = JsonSerializer.Deserialize<GlobalResultsStoreDocument>(json, _jsonOptions) ?? new GlobalResultsStoreDocument();
            document.Records ??= [];

            using var transaction = connection.BeginTransaction();
            foreach (var record in document.Records.Where(record => record.Entry is not null && !string.IsNullOrWhiteSpace(record.Id)))
            {
                UpsertRecord(connection, transaction, NormalizeRecord(record, record.LastUpdatedUtc == default ? DateTime.UtcNow : record.LastUpdatedUtc));
            }

            transaction.Commit();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (JsonException)
        {
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={_databasePath};Cache=Shared");
        connection.Open();
        return connection;
    }

    private void UpsertRecord(SqliteConnection connection, SqliteTransaction transaction, GlobalResultRecord record)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
INSERT INTO global_results (
    id,
    session_id,
    last_updated_utc,
    review_status,
    exported_at_utc,
    export_batch_id,
    entry_timestamp_utc,
    entry_json)
VALUES (
    $id,
    $sessionId,
    $lastUpdatedUtc,
    $reviewStatus,
    $exportedAtUtc,
    $exportBatchId,
    $entryTimestampUtc,
    $entryJson)
ON CONFLICT(id) DO UPDATE SET
    session_id = excluded.session_id,
    last_updated_utc = excluded.last_updated_utc,
    review_status = excluded.review_status,
    exported_at_utc = excluded.exported_at_utc,
    export_batch_id = excluded.export_batch_id,
    entry_timestamp_utc = excluded.entry_timestamp_utc,
    entry_json = excluded.entry_json;";

        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$sessionId", record.SessionId ?? string.Empty);
        command.Parameters.AddWithValue("$lastUpdatedUtc", record.LastUpdatedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$reviewStatus", ScanReviewCatalog.Normalize(record.ReviewStatus));
        command.Parameters.AddWithValue(
            "$exportedAtUtc",
            record.ExportedAtUtc is null
                ? DBNull.Value
                : record.ExportedAtUtc.Value.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$exportBatchId", record.ExportBatchId ?? string.Empty);
        command.Parameters.AddWithValue("$entryTimestampUtc", record.Entry.TimestampUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$entryJson", JsonSerializer.Serialize(record.Entry, _jsonOptions));
        command.ExecuteNonQuery();
    }

    private SessionHistoryEntry DeserializeEntry(string json)
    {
        return JsonSerializer.Deserialize<SessionHistoryEntry>(json, _jsonOptions) ?? new SessionHistoryEntry();
    }

    private static DateTime ParseDateTime(string value)
    {
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTime.UtcNow;
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