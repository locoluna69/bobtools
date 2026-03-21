using System.IO;
using System.Text.Json;
using BobTools.Models;

namespace BobTools.Services;

public sealed class SessionHistoryService : ISessionHistoryService
{
    private readonly object _syncRoot = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _sessionId;
    private readonly string _sessionsRootPath;

    public SessionHistoryService(string sessionsRootPath, string sessionId)
    {
        _sessionsRootPath = sessionsRootPath;
        _sessionId = sessionId;
        SessionFilePath = Path.Combine(sessionsRootPath, sessionId, "session.json");
    }

    public string SessionFilePath { get; }

    public void Initialize()
    {
        lock (_syncRoot)
        {
            var document = LoadDocument();
            SaveDocument(document);
        }
    }

    public void UpsertSessionMetadata(SessionMetadata metadata)
    {
        lock (_syncRoot)
        {
            var document = LoadDocument();
            document.Metadata = metadata ?? new SessionMetadata();
            document.LastUpdatedUtc = DateTime.UtcNow;
            SaveDocument(document);
        }
    }

    public void UpsertEntry(string sessionId, SessionHistoryEntry entry)
    {
        lock (_syncRoot)
        {
            var resolvedSessionId = string.IsNullOrWhiteSpace(sessionId) ? _sessionId : sessionId;
            var document = LoadDocument(resolvedSessionId);
            var existingIndex = document.Events.FindIndex(item => string.Equals(item.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                document.Events[existingIndex] = entry;
            }
            else
            {
                document.Events.Add(entry);
            }

            document.LastUpdatedUtc = DateTime.UtcNow;
            SaveDocument(resolvedSessionId, document);
        }
    }

    public SessionHistoryDocument LoadCurrentDocument()
    {
        lock (_syncRoot)
        {
            return LoadDocument();
        }
    }

    public IReadOnlyList<SessionHistoryDocument> LoadAllDocuments()
    {
        lock (_syncRoot)
        {
            if (!Directory.Exists(_sessionsRootPath))
            {
                return [];
            }

            return Directory.EnumerateFiles(_sessionsRootPath, "session.json", SearchOption.AllDirectories)
                .Select(path => TryLoadDocumentFromPath(path))
                .Where(document => document is not null)
                .Select(document => document!)
                .OrderByDescending(document => document.LastUpdatedUtc)
                .ThenByDescending(document => document.CreatedUtc)
                .ToList();
        }
    }

    private SessionHistoryDocument LoadDocument()
    {
        return TryLoadDocumentFromPath(SessionFilePath, _sessionId) ?? CreateDocument();
    }

    private SessionHistoryDocument LoadDocument(string sessionId)
    {
        var resolvedSessionId = string.IsNullOrWhiteSpace(sessionId) ? _sessionId : sessionId;
        return TryLoadDocumentFromPath(GetSessionFilePath(resolvedSessionId), resolvedSessionId) ?? CreateDocument(resolvedSessionId);
    }

    private void SaveDocument(SessionHistoryDocument document)
    {
        SaveDocument(_sessionId, document);
    }

    private void SaveDocument(string sessionId, SessionHistoryDocument document)
    {
        var resolvedSessionId = string.IsNullOrWhiteSpace(sessionId) ? _sessionId : sessionId;
        document.SessionId = resolvedSessionId;
        document.Metadata ??= new SessionMetadata();
        document.Events ??= [];

        var sessionFilePath = GetSessionFilePath(resolvedSessionId);
        var directory = Path.GetDirectoryName(sessionFilePath)!;
        Directory.CreateDirectory(directory);

        var tempFilePath = Path.Combine(directory, "session.tmp");
        var json = JsonSerializer.Serialize(document, _jsonOptions);
        File.WriteAllText(tempFilePath, json);
        File.Move(tempFilePath, sessionFilePath, overwrite: true);
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