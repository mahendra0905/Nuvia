using System;
using System.IO;
using System.Linq;
using Nuvia.App.Services;
using Xunit;

namespace Nuvia.Tests;

/// <summary>
/// Tests for <see cref="IndexStore.DeleteFile"/>: it must remove exactly the targeted row, refuse to
/// cross account boundaries, and report honestly when nothing matched. These use a real on-disk SQLite
/// database in a temp folder — no network, no credentials.
/// </summary>
public sealed class IndexStoreDeleteTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IndexStore _store;

    public IndexStoreDeleteTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "nuvia-tests-" + Guid.NewGuid().ToString("N") + ".db");
        _store = new IndexStore(_dbPath);
        _store.Migrate();
    }

    private static SavedMessageRef Ref(int messageId) => new(
        PeerKind: SavedMessageRef.PeerKindSelf,
        PeerId: 7,
        MessageId: messageId,
        DocumentId: 1000 + messageId,
        AccessHash: 555,
        FileReference: new byte[] { 1, 2, 3 },
        DcId: 2,
        SizeBytes: 10,
        MimeType: "application/octet-stream",
        UploadedUtc: DateTime.UtcNow);

    private IndexedFile Insert(long ownerId, int messageId, string name) => _store.Insert(
        ownerId, Ref(messageId), name, name, 10, "application/octet-stream", DateTime.UtcNow);

    [Fact]
    public void DeleteFile_RemovesOnlyTheTargetedRow()
    {
        var keep = Insert(7, 1, "keep.txt");
        var remove = Insert(7, 2, "remove.txt");

        var deleted = _store.DeleteFile(ownerId: 7, id: remove.Id);

        Assert.True(deleted);
        var remaining = _store.ListForAccount(7);
        Assert.Single(remaining);
        Assert.Equal(keep.Id, remaining[0].Id);
    }

    [Fact]
    public void DeleteFile_DoesNotCrossAccountBoundary()
    {
        var mine = Insert(7, 1, "mine.txt");

        // Same row id, wrong owner: nothing must be deleted.
        var deleted = _store.DeleteFile(ownerId: 99, id: mine.Id);

        Assert.False(deleted);
        Assert.Single(_store.ListForAccount(7));
    }

    [Fact]
    public void DeleteFile_ReturnsFalseWhenRowIsMissing()
    {
        var deleted = _store.DeleteFile(ownerId: 7, id: 123456);
        Assert.False(deleted);
    }

    [Fact]
    public void DeleteFile_RequiresAnAccountId()
    {
        Assert.Throws<ArgumentException>(() => _store.DeleteFile(ownerId: 0, id: 1));
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { /* best-effort temp cleanup */ }
    }
}
