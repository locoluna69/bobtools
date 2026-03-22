using System.IO;
using System.Text.Json;
using BobTools.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace BobTools.Services;

public sealed class SessionHistoryService : ISessionHistoryService
{
    private readonly object _syncRoot = new();
    private readonly string _databasePath;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _sessionId;
    private readonly string _sessionsRootPath;

    public SessionHistoryService(string databasePath, string sessionsRootPath, string sessionId)
    {
        _databasePath = databasePath;
        _sessionsRootPath = sessionsRootPath;
        _sessionId = sessionId;
        SessionFilePath = Path.Combine(sessionsRootPath, sessionId, "session.json");
    }

    public string SessionFilePath { get; }

    public void Initialize()
    {
        lock (_syncRoot)
        {
            EnsureDatabase();
            MigrateLegacySessionsIfNeeded();
            using var connection = OpenConnection();
            EnsureSessionRowExists(connection, null, _sessionId);
        }
    }

    public void UpsertSessionMetadata(SessionMetadata metadata)
    {
        lock (_syncRoot)
        {
            EnsureDatabase();
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var existing = LoadDocument(connection, transaction, _sessionId) ?? CreateDocument();
            existing.Metadata = metadata ?? new SessionMetadata();
            existing.LastUpdatedUtc = DateTime.UtcNow;
            SaveDocument(connection, transaction, existing);
            transaction.Commit();
        }
    }

    public void UpsertEntry(string sessionId, SessionHistoryEntry entry)
    {
        lock (_syncRoot)
        {
            EnsureDatabase();
            var resolvedSessionId = string.IsNullOrWhiteSpace(sessionId) ? _sessionId : sessionId;
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            EnsureSessionRowExists(connection, transaction, resolvedSessionId);

            var normalizedEntry = entry ?? new SessionHistoryEntry();
            normalizedEntry.Id = string.IsNullOrWhiteSpace(normalizedEntry.Id) ? Guid.NewGuid().ToString() : normalizedEntry.Id;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO session_events (id, session_id, timestamp_utc, entry_json)
VALUES ($id, $sessionId, $timestampUtc, $entryJson)
ON CONFLICT(id) DO UPDATE SET
    session_id = excluded.session_id,
    timestamp_utc = excluded.timestamp_utc,
    entry_json = excluded.entry_json;";

                command.Parameters.AddWithValue("$id", normalizedEntry.Id);
                command.Parameters.AddWithValue("$sessionId", resolvedSessionId);
                command.Parameters.AddWithValue("$timestampUtc", normalizedEntry.TimestampUtc.ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$entryJson", JsonSerializer.Serialize(normalizedEntry, _jsonOptions));
                command.ExecuteNonQuery();
            }

            using (var updateCommand = connection.CreateCommand())
            {
                updateCommand.Transaction = transaction;
                updateCommand.CommandText = @"
UPDATE session_documents
SET last_updated_utc = $lastUpdatedUtc
WHERE session_id = $sessionId;";
                updateCommand.Parameters.AddWithValue("$lastUpdatedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                updateCommand.Parameters.AddWithValue("$sessionId", resolvedSessionId);
                updateCommand.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public SessionHistoryDocument LoadCurrentDocument()
    {
        lock (_syncRoot)
        {
            EnsureDatabase();
            MigrateLegacySessionsIfNeeded();
            using var connection = OpenConnection();
            return LoadDocument(connection, null, _sessionId) ?? CreateDocument();
        }
    }

    public IReadOnlyList<SessionHistoryDocument> LoadAllDocuments()
    {
        lock (_syncRoot)
        {
            EnsureDatabase();
            MigrateLegacySessionsIfNeeded();

            using var connection = OpenConnection();
            var documents = new Dictionary<string, SessionHistoryDocument>(StringComparer.OrdinalIgnoreCase);

            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT session_id, created_utc, last_updated_utc, metadata_json
FROM session_documents
ORDER BY last_updated_utc DESC, created_utc DESC;";

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var sessionId = reader.GetString(0);
                    documents[sessionId] = new SessionHistoryDocument
                    {
                        SessionId = sessionId,
                        CreatedUtc = ParseDateTime(reader.GetString(1)),
                        LastUpdatedUtc = ParseDateTime(reader.GetString(2)),
                        Metadata = DeserializeMetadata(reader.GetString(3)),
                        Events = []
                    };
                }
            }

            if (documents.Count == 0)
            {
                return [];
            }

            using (var eventCommand = connection.CreateCommand())
            {
                eventCommand.CommandText = @"
SELECT session_id, entry_json
FROM session_events
ORDER BY timestamp_utc DESC;";

                using var reader = eventCommand.ExecuteReader();
                while (reader.Read())
                {
                    var sessionId = reader.GetString(0);
                    if (!documents.TryGetValue(sessionId, out var document))
                    {
                        continue;
                    }

                    document.Events.Add(DeserializeEntry(reader.GetString(1)));
                }
            }

            return documents.Values
                .OrderByDescending(document => document.LastUpdatedUtc)
                .ThenByDescending(document => document.CreatedUtc)
                .ToList();
        }
    }

    private void EnsureDatabase()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        using var connection = OpenConnection();
        using var pragmaCommand = connection.CreateCommand();
        pragmaCommand.CommandText = "PRAGMA foreign_keys = ON;";
        pragmaCommand.ExecuteNonQuery();

        using var command = connection.CreateCommand();
        command.CommandText = @"
CREATE TABLE IF NOT EXISTS session_documents (
    session_id TEXT PRIMARY KEY,
    created_utc TEXT NOT NULL,
    last_updated_utc TEXT NOT NULL,
    metadata_json TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS session_events (
    id TEXT PRIMARY KEY,
    session_id TEXT NOT NULL,
    timestamp_utc TEXT NOT NULL,
    entry_json TEXT NOT NULL,
    FOREIGN KEY(session_id) REFERENCES session_documents(session_id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_session_events_session_timestamp
    ON session_events(session_id, timestamp_utc DESC);";
        command.ExecuteNonQuery();
    }

    private void MigrateLegacySessionsIfNeeded()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM session_documents LIMIT 1);";
        var hasRows = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
        if (hasRows || !Directory.Exists(_sessionsRootPath))
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        foreach (var path in Directory.EnumerateFiles(_sessionsRootPath, "session.json", SearchOption.AllDirectories))
        {
            var document = TryLoadDocumentFromPath(path);
            if (document is null)
            {
                continue;
            }

            SaveDocument(connection, transaction, document);
        }

        transaction.Commit();
    }

    private SessionHistoryDocument? LoadDocument(SqliteConnection connection, SqliteTransaction? transaction, string sessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
SELECT created_utc, last_updated_utc, metadata_json
FROM session_documents
WHERE session_id = $sessionId;";
        command.Parameters.AddWithValue("$sessionId", sessionId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var document = new SessionHistoryDocument
        {
            SessionId = sessionId,
            CreatedUtc = ParseDateTime(reader.GetString(0)),
            LastUpdatedUtc = ParseDateTime(reader.GetString(1)),
            Metadata = DeserializeMetadata(reader.GetString(2)),
            Events = []
        };

        using var eventCommand = connection.CreateCommand();
        eventCommand.Transaction = transaction;
        eventCommand.CommandText = @"
SELECT entry_json
FROM session_events
WHERE session_id = $sessionId
ORDER BY timestamp_utc DESC;";
        eventCommand.Parameters.AddWithValue("$sessionId", sessionId);

        using var eventReader = eventCommand.ExecuteReader();
        while (eventReader.Read())
        {
            document.Events.Add(DeserializeEntry(eventReader.GetString(0)));
        }

        return document;
    }

    private void SaveDocument(SqliteConnection connection, SqliteTransaction transaction, SessionHistoryDocument document)
    {
        var resolvedSessionId = string.IsNullOrWhiteSpace(document.SessionId) ? _sessionId : document.SessionId;
        document.SessionId = resolvedSessionId;
        document.Metadata ??= new SessionMetadata();
        document.Events ??= [];

        using (var documentCommand = connection.CreateCommand())
        {
            documentCommand.Transaction = transaction;
            documentCommand.CommandText = @"
INSERT INTO session_documents (session_id, created_utc, last_updated_utc, metadata_json)
VALUES ($sessionId, $createdUtc, $lastUpdatedUtc, $metadataJson)
ON CONFLICT(session_id) DO UPDATE SET
    created_utc = excluded.created_utc,
    last_updated_utc = excluded.last_updated_utc,
    metadata_json = excluded.metadata_json;";

            documentCommand.Parameters.AddWithValue("$sessionId", resolvedSessionId);
            documentCommand.Parameters.AddWithValue("$createdUtc", document.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
            documentCommand.Parameters.AddWithValue("$lastUpdatedUtc", document.LastUpdatedUtc.ToString("O", CultureInfo.InvariantCulture));
            documentCommand.Parameters.AddWithValue("$metadataJson", JsonSerializer.Serialize(document.Metadata, _jsonOptions));
            documentCommand.ExecuteNonQuery();
        }

        using (var deleteEventsCommand = connection.CreateCommand())
        {
            deleteEventsCommand.Transaction = transaction;
            deleteEventsCommand.CommandText = "DELETE FROM session_events WHERE session_id = $sessionId;";
            deleteEventsCommand.Parameters.AddWithValue("$sessionId", resolvedSessionId);
            deleteEventsCommand.ExecuteNonQuery();
        }

        foreach (var entry in document.Events)
        {
            using var eventCommand = connection.CreateCommand();
            eventCommand.Transaction = transaction;
            eventCommand.CommandText = @"
INSERT INTO session_events (id, session_id, timestamp_utc, entry_json)
VALUES ($id, $sessionId, $timestampUtc, $entryJson)
ON CONFLICT(id) DO UPDATE SET
    session_id = excluded.session_id,
    timestamp_utc = excluded.timestamp_utc,
    entry_json = excluded.entry_json;";
            eventCommand.Parameters.AddWithValue("$id", string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString() : entry.Id);
            eventCommand.Parameters.AddWithValue("$sessionId", resolvedSessionId);
            eventCommand.Parameters.AddWithValue("$timestampUtc", entry.TimestampUtc.ToString("O", CultureInfo.InvariantCulture));
            eventCommand.Parameters.AddWithValue("$entryJson", JsonSerializer.Serialize(entry, _jsonOptions));
            eventCommand.ExecuteNonQuery();
        }
    }

    private SessionHistoryDocument CreateDocument()
    {
        return CreateDocument(_sessionId);
    }

    private SessionHistoryDocument CreateDocument(string sessionId)
    {
        var now = DateTime.UtcNow;
        return new SessionHistoryDocument
        {
            SessionId = sessionId,
            CreatedUtc = now,
            LastUpdatedUtc = now,
            Metadata = new SessionMetadata(),
            Events = []
        };
    }

    private string GetSessionFilePath(string sessionId)
    {
        return Path.Combine(_sessionsRootPath, sessionId, "session.json");
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={_databasePath};Cache=Shared");
        connection.Open();
        using var pragmaCommand = connection.CreateCommand();
        pragmaCommand.CommandText = "PRAGMA foreign_keys = ON;";
        pragmaCommand.ExecuteNonQuery();
        return connection;
    }

    private void EnsureSessionRowExists(SqliteConnection connection, SqliteTransaction? transaction, string sessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
INSERT INTO session_documents (session_id, created_utc, last_updated_utc, metadata_json)
VALUES ($sessionId, $createdUtc, $lastUpdatedUtc, $metadataJson)
ON CONFLICT(session_id) DO NOTHING;";
        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$createdUtc", now);
        command.Parameters.AddWithValue("$lastUpdatedUtc", now);
        command.Parameters.AddWithValue("$metadataJson", JsonSerializer.Serialize(new SessionMetadata(), _jsonOptions));
        command.ExecuteNonQuery();
    }

    private SessionMetadata DeserializeMetadata(string json)
    {
        return JsonSerializer.Deserialize<SessionMetadata>(json, _jsonOptions) ?? new SessionMetadata();
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

    private SessionHistoryDocument? TryLoadDocumentFromPath(string sessionFilePath, string? fallbackSessionId = null)
    {
        if (!File.Exists(sessionFilePath))
        {
            return fallbackSessionId is null ? null : CreateDocument(fallbackSessionId);
        }

        try
        {
            var json = File.ReadAllText(sessionFilePath);
            var document = JsonSerializer.Deserialize<SessionHistoryDocument>(json, _jsonOptions);
            if (document is null)
            {
                return fallbackSessionId is null ? null : CreateDocument(fallbackSessionId);
            }

            document.SessionId = string.IsNullOrWhiteSpace(document.SessionId)
                ? fallbackSessionId ?? Path.GetFileName(Path.GetDirectoryName(sessionFilePath) ?? string.Empty)
                : document.SessionId;
            document.Metadata ??= new SessionMetadata();
            document.Events ??= [];
            return document;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return fallbackSessionId is null ? null : CreateDocument(fallbackSessionId);
        }
    }
}