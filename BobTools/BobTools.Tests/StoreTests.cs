using System.Text.Json;
using BobTools.Models;
using BobTools.Services;
using Xunit;

namespace BobTools.Tests;

public sealed class StoreTests
{
    [Fact]
    public void GlobalResultsStore_MigratesLegacyJson_WithNullExportedTimestamp()
    {
        using var workspace = new TemporaryWorkspace();
        var databasePath = Path.Combine(workspace.RootPath, "data", "bobtools.db");
        var legacyStorePath = Path.Combine(workspace.RootPath, "live-results.json");
        var timestamp = new DateTime(2026, 3, 1, 12, 30, 0, DateTimeKind.Utc);

        var legacyDocument = new GlobalResultsStoreDocument
        {
            Records =
            [
                new GlobalResultRecord
                {
                    Id = "record-1",
                    SessionId = "session-1",
                    ReviewStatus = "reviewed",
                    ExportedAtUtc = null,
                    ExportBatchId = string.Empty,
                    Entry = new SessionHistoryEntry
                    {
                        Id = "entry-1",
                        TimestampUtc = timestamp,
                        ResultKind = "Scan",
                        OcrText = "sample text"
                    }
                }
            ]
        };

        File.WriteAllText(legacyStorePath, JsonSerializer.Serialize(legacyDocument, JsonOptions));

        var store = new GlobalResultsStore(databasePath, legacyStorePath);

        Assert.True(store.Exists());

        var records = store.LoadAll();

        var record = Assert.Single(records);
        Assert.Equal("record-1", record.Id);
        Assert.Equal("session-1", record.SessionId);
        Assert.Equal("Reviewed", record.ReviewStatus);
        Assert.Null(record.ExportedAtUtc);
        Assert.Equal(timestamp, record.Entry.TimestampUtc);
        Assert.Equal("entry-1", record.Entry.Id);
    }

    [Fact]
    public void GlobalResultsStore_MarkExported_UpdatesStoredRecord()
    {
        using var workspace = new TemporaryWorkspace();
        var databasePath = Path.Combine(workspace.RootPath, "data", "bobtools.db");
        var legacyStorePath = Path.Combine(workspace.RootPath, "missing-live-results.json");
        var exportedAt = new DateTime(2026, 3, 2, 8, 45, 0, DateTimeKind.Utc);

        var store = new GlobalResultsStore(databasePath, legacyStorePath);

        store.Upsert(new GlobalResultRecord
        {
            Id = "record-2",
            SessionId = "session-2",
            ReviewStatus = "Reviewed",
            Entry = new SessionHistoryEntry
            {
                Id = "entry-2",
                TimestampUtc = new DateTime(2026, 3, 2, 8, 0, 0, DateTimeKind.Utc),
                ResultKind = "Mining"
            }
        });

        store.MarkExported(["record-2"], "batch-42", exportedAt);

        var record = Assert.Single(store.LoadAll());
        Assert.Equal(exportedAt, record.ExportedAtUtc);
        Assert.Equal("batch-42", record.ExportBatchId);
        Assert.Equal("Reviewed", record.ReviewStatus);
    }

    [Fact]
    public void SessionHistoryService_MigratesLegacySessionDocument()
    {
        using var workspace = new TemporaryWorkspace();
        var databasePath = Path.Combine(workspace.RootPath, "data", "bobtools.db");
        var sessionsRootPath = Path.Combine(workspace.RootPath, "Sessions");
        var sessionId = "session-legacy";
        var sessionDirectory = Path.Combine(sessionsRootPath, sessionId);
        Directory.CreateDirectory(sessionDirectory);

        var legacyDocument = new SessionHistoryDocument
        {
            SessionId = sessionId,
            CreatedUtc = new DateTime(2026, 3, 1, 5, 0, 0, DateTimeKind.Utc),
            LastUpdatedUtc = new DateTime(2026, 3, 1, 6, 0, 0, DateTimeKind.Utc),
            Metadata = new SessionMetadata
            {
                RsiHandle = "pilot",
                Ship = "Prospector",
                StarSystem = "Pyro"
            },
            Events =
            [
                new SessionHistoryEntry
                {
                    Id = "event-1",
                    TimestampUtc = new DateTime(2026, 3, 1, 5, 30, 0, DateTimeKind.Utc),
                    ResultKind = "Mining",
                    OcrText = "ocr result"
                }
            ]
        };

        File.WriteAllText(Path.Combine(sessionDirectory, "session.json"), JsonSerializer.Serialize(legacyDocument, JsonOptions));

        var service = new SessionHistoryService(databasePath, sessionsRootPath, sessionId);

        service.Initialize();

        var current = service.LoadCurrentDocument();

        Assert.Equal("pilot", current.Metadata.RsiHandle);
        Assert.Equal("Prospector", current.Metadata.Ship);
        Assert.Equal("Pyro", current.Metadata.StarSystem);
        var entry = Assert.Single(current.Events);
        Assert.Equal("event-1", entry.Id);
        Assert.Equal("Mining", entry.ResultKind);
        Assert.Equal("ocr result", entry.OcrText);
    }

    [Fact]
    public void SessionHistoryService_UpsertEntry_UsesCurrentSessionWhenNoneProvided()
    {
        using var workspace = new TemporaryWorkspace();
        var databasePath = Path.Combine(workspace.RootPath, "data", "bobtools.db");
        var sessionsRootPath = Path.Combine(workspace.RootPath, "Sessions");
        var sessionId = "session-current";
        var service = new SessionHistoryService(databasePath, sessionsRootPath, sessionId);

        service.Initialize();
        service.UpsertEntry(string.Empty, new SessionHistoryEntry
        {
            Id = string.Empty,
            TimestampUtc = new DateTime(2026, 3, 3, 10, 0, 0, DateTimeKind.Utc),
            ResultKind = "Scan",
            OcrText = "transcript"
        });

        var current = service.LoadCurrentDocument();

        Assert.Equal(sessionId, current.SessionId);
        var entry = Assert.Single(current.Events);
        Assert.False(string.IsNullOrWhiteSpace(entry.Id));
        Assert.Equal("Scan", entry.ResultKind);
        Assert.Equal("transcript", entry.OcrText);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private sealed class TemporaryWorkspace : IDisposable
    {
        public TemporaryWorkspace()
        {
            RootPath = Path.Combine(Path.GetTempPath(), "BobTools.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();

                for (var attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        Directory.Delete(RootPath, recursive: true);
                        break;
                    }
                    catch (IOException) when (attempt < 4)
                    {
                        Thread.Sleep(100);
                    }
                    catch (UnauthorizedAccessException) when (attempt < 4)
                    {
                        Thread.Sleep(100);
                    }
                    catch (IOException)
                    {
                        break;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        break;
                    }
                }
            }
        }
    }
}