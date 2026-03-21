using BobTools.Models;

namespace BobTools.Services;

public interface ISessionHistoryService
{
    string SessionFilePath { get; }
    void Initialize();
    void UpsertSessionMetadata(SessionMetadata metadata);
    void UpsertEntry(string sessionId, SessionHistoryEntry entry);
    SessionHistoryDocument LoadCurrentDocument();
    IReadOnlyList<SessionHistoryDocument> LoadAllDocuments();
}