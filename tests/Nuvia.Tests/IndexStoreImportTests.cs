using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Nuvia.App.Services;
using Xunit;

namespace Nuvia.Tests;

/// <summary>
/// Tests for the schema-v3 import machinery on <see cref="IndexStore"/>: the per-row <c>source</c> tag,
/// the import-time upsert-by-remote, the external-file purge, and the per-peer scan watermark. These use
/// a real on-disk SQLite database in a temp folder — no network, no credentials.
/// </summary>
public sealed class IndexStoreImportTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IndexStore _store;

    public IndexStoreImportTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "nuvia-tests-" + Guid.NewGuid().ToString("N") + ".db");
        _store = new IndexStore(_dbPath);
        _store.Migrate();
    }

    private static SavedMessageRef SelfRef(int messageId, byte[]? fileRef = null) => new(
        PeerKind: SavedMessageRef.PeerKindSelf,
        PeerId: 7,
        MessageId: messageId,
        DocumentId: 1000 + messageId,
        AccessHash: 555,
        FileReference: fileRef ?? new byte[] { 1, 2, 3 },
        DcId: 2,
        SizeBytes: 10,
        MimeType: "application/octet-stream",
        UploadedUtc: DateTime.UtcNow);

    private static SavedMessageRef ChannelRef(int messageId, long channelId = 42) => new(
        PeerKind: SavedMessageRef.PeerKindChannel,
        PeerId: channelId,
        MessageId: messageId,
        DocumentId: 2000 + messageId,
        AccessHash: 777,
        FileReference: new byte[] { 9, 9 },
        DcId: 2,
        SizeBytes: 20,
        MimeType: "application/pdf",
        UploadedUtc: DateTime.UtcNow);

    [Fact]
    public void SchemaVersion_IsFour()
    {
        Assert.Equal(4, IndexStore.SchemaVersion);
    }

    [Fact]
    public void Insert_DefaultsSourceToNuvia()
    {
        _store.Insert(7, SelfRef(1), "a.txt", "a.txt", 10, "application/octet-stream", DateTime.UtcNow);
        var row = Assert.Single(_store.ListForAccount(7));
        Assert.Equal(IndexStore.SourceNuvia, row.Source);
    }

    [Fact]
    public void UpsertByRemote_InsertsThenUpdates_PreservingLocalRenameAndFirstSeenTime()
    {
        var firstSeen = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var inserted = _store.UpsertByRemote(
            7, SelfRef(1, new byte[] { 1 }), "orig.txt", "orig.txt", 10,
            "application/octet-stream", IndexStore.SourceNuvia, firstSeen);
        Assert.Equal(IndexStore.UpsertOutcome.Inserted, inserted);

        // A local rename must survive a later re-import.
        var id = _store.ListForAccount(7).Single().Id;
        Assert.True(_store.UpdateDisplayName(7, id, "my nice name"));

        // Re-import the same remote message with a fresh file reference and a later time.
        var later = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var updated = _store.UpsertByRemote(
            7, SelfRef(1, new byte[] { 4, 5, 6 }), "orig.txt", "IGNORED display", 10,
            "application/octet-stream", IndexStore.SourceNuvia, later);
        Assert.Equal(IndexStore.UpsertOutcome.Updated, updated);

        var row = Assert.Single(_store.ListForAccount(7));
        Assert.Equal("my nice name", row.DisplayName);                 // rename preserved
        Assert.Equal(firstSeen, row.IndexedUtc);                       // first-seen time preserved
        Assert.Equal(new byte[] { 4, 5, 6 }, row.Remote.FileReference); // reference refreshed
        Assert.NotNull(row.MetadataRefreshedUtc);                      // refresh recorded
    }

    [Fact]
    public void UpsertByRemote_StoresExternalSource()
    {
        _store.UpsertByRemote(
            7, SelfRef(5), "ext.bin", "ext.bin", 10, "application/octet-stream",
            IndexStore.SourceExternal, DateTime.UtcNow);
        var row = Assert.Single(_store.ListForAccount(7));
        Assert.Equal(IndexStore.SourceExternal, row.Source);
    }

    [Fact]
    public void DeleteExternalSelfFiles_RemovesOnlyExternalSavedMessagesRows()
    {
        _store.UpsertByRemote(7, SelfRef(1), "nuvia.txt", "nuvia.txt", 10, "text/plain",
            IndexStore.SourceNuvia, DateTime.UtcNow);
        _store.UpsertByRemote(7, SelfRef(2), "ext.txt", "ext.txt", 10, "text/plain",
            IndexStore.SourceExternal, DateTime.UtcNow);
        _store.UpsertByRemote(7, ChannelRef(3), "grp-ext.pdf", "grp-ext.pdf", 20, "application/pdf",
            IndexStore.SourceExternal, DateTime.UtcNow);

        var removed = _store.DeleteExternalSelfFiles(7);

        Assert.Equal(1, removed); // only the external self row
        var remaining = _store.ListForAccount(7);
        Assert.Equal(2, remaining.Count);
        Assert.DoesNotContain(remaining, r =>
            r.Remote.PeerKind == SavedMessageRef.PeerKindSelf && r.Source == IndexStore.SourceExternal);
        // The external file that lives in the group is untouched — the group shows all files.
        Assert.Contains(remaining, r => r.Remote.PeerKind == SavedMessageRef.PeerKindChannel);
    }

    [Fact]
    public void ImportWatermark_DefaultsToZero_AdvancesForwardOnly_AndResets()
    {
        Assert.Equal(0, _store.GetImportWatermark(7, SavedMessageRef.PeerKindSelf, 7));

        _store.SetImportWatermark(7, SavedMessageRef.PeerKindSelf, 7, 100);
        Assert.Equal(100, _store.GetImportWatermark(7, SavedMessageRef.PeerKindSelf, 7));

        // A lower value must not move the mark backwards.
        _store.SetImportWatermark(7, SavedMessageRef.PeerKindSelf, 7, 50);
        Assert.Equal(100, _store.GetImportWatermark(7, SavedMessageRef.PeerKindSelf, 7));

        // A higher value advances it.
        _store.SetImportWatermark(7, SavedMessageRef.PeerKindSelf, 7, 250);
        Assert.Equal(250, _store.GetImportWatermark(7, SavedMessageRef.PeerKindSelf, 7));

        // Marks are independent per peer.
        Assert.Equal(0, _store.GetImportWatermark(7, SavedMessageRef.PeerKindChannel, 42));

        _store.ResetImportWatermark(7, SavedMessageRef.PeerKindSelf, 7);
        Assert.Equal(0, _store.GetImportWatermark(7, SavedMessageRef.PeerKindSelf, 7));
    }

    [Fact]
    public void Migrate_FromV2_PreservesExistingRowsAndTagsThemNuvia()
    {
        // Build a v2-shaped database by hand (indexed_files without the source column, plus a single-PK
        // storage_groups holding one group) with one file row, then let IndexStore migrate it to the
        // current schema and confirm the file row survives tagged 'nuvia' and the group survives the v4
        // composite-key rebuild.
        var path = Path.Combine(Path.GetTempPath(), "nuvia-v2-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            SeedV2Database(path);

            var store = new IndexStore(path);
            store.Migrate();

            var row = Assert.Single(store.ListForAccount(7));
            Assert.Equal("legacy.txt", row.OriginalFileName);
            Assert.Equal(IndexStore.SourceNuvia, row.Source);

            // The pre-existing group row is copied across the v4 storage_groups rebuild unchanged.
            var group = store.GetStorageGroup(7, 42);
            Assert.NotNull(group);
            Assert.Equal("Legacy vault", group!.Title);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(path); } catch { /* best-effort */ }
        }
    }

    private static void SeedV2Database(string path)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                CREATE TABLE indexed_files (
                    id                     INTEGER PRIMARY KEY AUTOINCREMENT,
                    owner_id               INTEGER NOT NULL,
                    peer_kind              TEXT    NOT NULL,
                    peer_id                INTEGER NOT NULL,
                    message_id             INTEGER NOT NULL,
                    document_id            INTEGER NOT NULL,
                    access_hash            INTEGER NOT NULL,
                    file_reference         BLOB    NOT NULL,
                    dc_id                  INTEGER NOT NULL,
                    original_file_name     TEXT    NOT NULL,
                    display_name           TEXT    NOT NULL,
                    size_bytes             INTEGER NOT NULL,
                    mime_type              TEXT    NULL,
                    remote_upload_utc      TEXT    NOT NULL,
                    indexed_utc            TEXT    NOT NULL,
                    metadata_refreshed_utc TEXT    NULL
                );
                INSERT INTO indexed_files
                    (owner_id, peer_kind, peer_id, message_id, document_id, access_hash,
                     file_reference, dc_id, original_file_name, display_name, size_bytes,
                     mime_type, remote_upload_utc, indexed_utc, metadata_refreshed_utc)
                VALUES
                    (7, 'self', 7, 1, 1001, 555, X'010203', 2, 'legacy.txt', 'legacy.txt', 10,
                     'text/plain', @ts, @ts, NULL);

                -- A faithful v2 database also has the group tables created by migration [2]. The v2
                -- storage_groups had a single-column PRIMARY KEY (account_id) — the v4 migration rebuilds
                -- it with a composite key, so this row must survive that rebuild.
                CREATE TABLE storage_groups (
                    account_id   INTEGER PRIMARY KEY,
                    channel_id   INTEGER NOT NULL,
                    access_hash  INTEGER NOT NULL,
                    title        TEXT    NOT NULL,
                    created_utc  TEXT    NOT NULL
                );
                INSERT INTO storage_groups (account_id, channel_id, access_hash, title, created_utc)
                VALUES (7, 42, 777, 'Legacy vault', @ts);
                CREATE TABLE account_prefs (
                    account_id       INTEGER PRIMARY KEY,
                    destination_kind TEXT    NOT NULL
                );
                PRAGMA user_version = 2;";
            cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* best-effort temp cleanup */ }
    }
}
