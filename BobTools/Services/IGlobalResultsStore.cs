using BobTools.Models;

namespace BobTools.Services;

public interface IGlobalResultsStore
{
    bool Exists();
    IReadOnlyList<GlobalResultRecord> LoadAll();
    void ReplaceAll(IEnumerable<GlobalResultRecord> records);
    void Upsert(GlobalResultRecord record);
    void MarkExported(IEnumerable<string> recordIds, string exportBatchId, DateTime exportedAtUtc);
    void Clear();
}