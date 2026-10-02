using System;
using System.IO;
using Nuvia.App.Services;
using Xunit;

namespace Nuvia.Tests;

/// <summary>
/// Tests for <see cref="IndexStore.DeleteAllForAccount"/> — the "reset the local file list" primitive behind
/// Settings → Storage. It must forget every file row AND every Nuvia-local folder for one account in a single
/// transaction, report how many file rows went, never touch another account, and require an account id. Uses a
/// real on-disk SQLite database in a temp folder — no network, no credentials.
/// </summary>
public sealed class IndexStoreResetTests : IDisposable
{
    private const long Account = 7;
    private const long OtherAccount = 8;
    private static readonly string SelfKind = SavedMessageRef.PeerKindSelf;

    private readonly string _dbPath;
    private readonly IndexStore _store;

    public IndexStoreResetTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "nuvia-reset-" + Guid.NewGuid().ToString("N") + ".db");
        _store = new IndexStore(_dbPath);
        _store.Migrate();
    }

    private static SavedMessageRef Ref(long ownerId, int messageId) => new(
        PeerKind: SavedMessageRef.PeerKindSelf,
        PeerId: ownerId,
        MessageId: messageId,
        DocumentId: 1000 + messageId,
        AccessHash: 555,
        FileReference: new byte[] { 1, 2, 3 },
        DcId: 2,
        SizeBytes: 10,
        MimeType: "application/octet-stream",
        UploadedUtc: DateTime.UtcNow);

    private IndexedFile Insert(long ownerId, int messageId, string name) => _store.Insert(
        ownerId, Ref(ownerId, messageId), name, name, 10, "application/octet-stream", DateTime.UtcNow);

    [Fact]
    public void DeleteAllForAccount_RemovesEveryFileAndReturnsTheCount()
    {
        Insert(Account, 1, "a.txt");
        Insert(Account, 2, "b.txt");

        var removed = _store.DeleteAllForAccount(Account);

        Assert.Equal(2, removed);
        Assert.Empty(_store.ListForAccount(Account));
    }

    [Fact]
    public void DeleteAllForAccount_AlsoRemovesTheAccountsFolders()
    {
        var work = _store.CreateFolder(Account, SelfKind, Account, null, "Work");
        _store.CreateFolder(Account, SelfKind, Account, work, "Invoices");

        _store.DeleteAllForAccount(Account);

        Assert.Empty(_store.GetFolders(Account, SelfKind, Account));
    }

    [Fact]
    public void DeleteAllForAccount_DoesNotCrossAccountBoundary()
    {
        Insert(Account, 1, "mine.txt");
        Insert(OtherAccount, 1, "theirs.txt");
        _store.CreateFolder(OtherAccount, SelfKind, OtherAccount, null, "Keep");

        var removed = _store.DeleteAllForAccount(Account);

        Assert.Equal(1, removed);
        Assert.Single(_store.ListForAccount(OtherAccount));
        Assert.Single(_store.GetFolders(OtherAccount, SelfKind, OtherAccount));
    }

    [Fact]
    public void DeleteAllForAccount_RequiresAnAccountId()
    {
        Assert.Throws<ArgumentException>(() => _store.DeleteAllForAccount(0));
    }

    public void Dispose()
    {
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { /* best-effort */ }
        try { File.Delete(_dbPath); } catch { /* best-effort temp cleanup */ }
    }
}
