using BobTools.Models;

namespace BobTools.Services;

public interface ISessionHistoryService
{
    string SessionFilePath { get; }
    void Initialize();
    void UpsertEntry(SessionHistoryEntry entry);
}