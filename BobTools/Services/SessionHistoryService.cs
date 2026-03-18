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

    public SessionHistoryService(string sessionsRootPath, string sessionId)
    {
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

    public void UpsertEntry(SessionHistoryEntry entry)
    {
        lock (_syncRoot)
        {
            var document = LoadDocument();
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
            SaveDocument(document);
        }
    }

    private SessionHistoryDocument LoadDocument()
    {
        if (!File.Exists(SessionFilePath))
        {
            return CreateDocument();
        }

        try
        {
            var json = File.ReadAllText(SessionFilePath);
            var document = JsonSerializer.Deserialize<SessionHistoryDocument>(json, _jsonOptions) ?? CreateDocument();
            document.SessionId = string.IsNullOrWhiteSpace(document.SessionId) ? _sessionId : document.SessionId;
            document.Events ??= [];
            return document;
        }
        catch (JsonException)
        {
            return CreateDocument();
        }
    }

    private void SaveDocument(SessionHistoryDocument document)
    {
        document.SessionId = _sessionId;
        document.Events ??= [];

        var directory = Path.GetDirectoryName(SessionFilePath)!;
        Directory.CreateDirectory(directory);

        var tempFilePath = Path.Combine(directory, "session.tmp");
        var json = JsonSerializer.Serialize(document, _jsonOptions);
        File.WriteAllText(tempFilePath, json);
        File.Move(tempFilePath, SessionFilePath, overwrite: true);
    }

    private SessionHistoryDocument CreateDocument()
    {
        var now = DateTime.UtcNow;
        return new SessionHistoryDocument
        {
            SessionId = _sessionId,
            CreatedUtc = now,
            LastUpdatedUtc = now,
            Events = []
        };
    }
}