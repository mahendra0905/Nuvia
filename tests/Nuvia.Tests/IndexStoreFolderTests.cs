using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Nuvia.App.Services;
using Xunit;

namespace Nuvia.Tests;

/// <summary>
/// Tests for the local-folder layer in <see cref="IndexStore"/> — the virtual, Nuvia-only organisation
/// that lives entirely in the index (Telegram has no folders). Covers folder CRUD, per-location/per-account
/// scoping, the sibling-name rule, tree counting/deletion, and the insert-vs-update folder asymmetry that
/// lets a reinstall restore folders while a re-import never clobbers a local move. No network, no credentials.
/// </summary>
public sealed class IndexStoreFolderTests : IDisposable
{
    private const long Account = 7;
    private const long OtherAccount = 8;
    private const long Channel = 42;
    private static readonly string SelfKind = SavedMessageRef.PeerKindSelf;
    private static readonly string ChannelKind = SavedMessageRef.PeerKindChannel;

    private readonly string _dbPath;
    private readonly IndexStore _store;

    public IndexStoreFolderTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "nuvia-folders-" + Guid.NewGuid().ToString("N") + ".db");
        _store = new IndexStore(_dbPath);
        _store.Migrate();
    }

    [Fact]
    public void CreateFolder_ThenGetFolders_ListsItWithParentAndName()
    {
        var work = _store.CreateFolder(Account, SelfKind, Account, null, "Work");

        var all = _store.GetFolders(Account, SelfKind, Account);
        var only = Assert.Single(all);
        Assert.Equal(work, only.Id);
        Assert.Equal("Work", only.Name);
        Assert.Null(only.ParentFolderId);
        Assert.Equal("Work", _store.GetFolder(Account, work)!.Name);
    }

    [Fact]
    public void CreateFolder_NestedChild_IsListedUnderItsParent()
    {
        var work = _store.CreateFolder(Account, SelfKind, Account, null, "Work");
        var invoices = _store.CreateFolder(Account, SelfKind, Account, work, "Invoices");

        var all = _store.GetFolders(Account, SelfKind, Account);
        Assert.Equal(2, all.Count);
        Assert.Equal(work, all.Single(f => f.Id == invoices).ParentFolderId);
    }

    [Fact]
    public void CreateFolder_DuplicateSiblingName_IsRejectedCaseInsensitively()
    {
        _store.CreateFolder(Account, SelfKind, Account, null, "Work");
        Assert.Throws<DuplicateFolderNameException>(
            () => _store.CreateFolder(Account, SelfKind, Account, null, "work"));
    }

    [Fact]
    public void CreateFolder_SameName_IsAllowedInAnotherLocationOrUnderAnotherParent()
    {
        var work = _store.CreateFolder(Account, SelfKind, Account, null, "Work");

        // Same name, different location (a group) — fine; the restored path is still unambiguous.
        _store.CreateFolder(Account, ChannelKind, Channel, null, "Work");
        // Same name, different parent — fine; siblings are what must stay unique.
        _store.CreateFolder(Account, SelfKind, Account, work, "Work");

        Assert.Single(_store.GetFolders(Account, ChannelKind, Channel));
        Assert.Equal(2, _store.GetFolders(Account, SelfKind, Account).Count);
    }

    [Fact]
    public void RenameFolder_ChangesTheName_AndRejectsASiblingClash()
    {
        var work = _store.CreateFolder(Account, SelfKind, Account, null, "Work");
        _store.CreateFolder(Account, SelfKind, Account, null, "Personal");

        Assert.True(_store.RenameFolder(Account, work, "Job"));
        Assert.Equal("Job", _store.GetFolder(Account, work)!.Name);

        Assert.Throws<DuplicateFolderNameException>(() => _store.RenameFolder(Account, work, "Personal"));
    }

    [Fact]
    public void CountFilesInFolderTree_IncludesDescendants()
    {
        var work = _store.CreateFolder(Account, SelfKind, Account, null, "Work");
        var invoices = _store.CreateFolder(Account, SelfKind, Account, work, "Invoices");

        _store.Insert(Account, SelfRef(1), "a.pdf", "a.pdf", 10, "application/pdf", DateTime.UtcNow,
            folderId: work);
        _store.Insert(Account, SelfRef(2), "b.pdf", "b.pdf", 10, "application/pdf", DateTime.UtcNow,
            folderId: invoices);

        Assert.Equal(2, _store.CountFilesInFolderTree(Account, work));     // folder + descendant
        Assert.Equal(1, _store.CountFilesInFolderTree(Account, invoices)); // leaf only
    }

    [Fact]
    public void DeleteFolderTree_RefusesWhenTheTreeStillHoldsFiles()
    {
        var work = _store.CreateFolder(Account, SelfKind, Account, null, "Work");
        _store.Insert(Account, SelfRef(1), "a.pdf", "a.pdf", 10, "application/pdf", DateTime.UtcNow,
            folderId: work);

        Assert.Throws<InvalidOperationException>(() => _store.DeleteFolderTree(Account, work));
        Assert.NotNull(_store.GetFolder(Account, work)); // nothing was removed
    }

    [Fact]
    public void DeleteFolderTree_RemovesAnEmptySubtreeWholesale()
    {
        var work = _store.CreateFolder(Account, SelfKind, Account, null, "Work");
        _store.CreateFolder(Account, SelfKind, Account, work, "Invoices");

        var removed = _store.DeleteFolderTree(Account, work);

        Assert.Equal(2, removed); // the folder and its one child
        Assert.Empty(_store.GetFolders(Account, SelfKind, Account));
    }

    [Fact]
    public void Folders_AreScopedPerAccount()
    {
        var work = _store.CreateFolder(Account, SelfKind, Account, null, "Work");

        Assert.Empty(_store.GetFolders(OtherAccount, SelfKind, OtherAccount));
        Assert.Null(_store.GetFolder(OtherAccount, work)); // cannot read another account's folder by id
    }

    [Fact]
    public void EnsureFolderPath_CreatesTheChainOnce_AndIsIdempotent()
    {
        var segments = new[] { "Work", "Invoices" };
        var first = _store.EnsureFolderPath(Account, SelfKind, Account, segments);
        var second = _store.EnsureFolderPath(Account, SelfKind, Account, segments);

        Assert.Equal(first, second); // same leaf, not a duplicate
        Assert.Equal(2, _store.GetFolders(Account, SelfKind, Account).Count);
    }

    [Fact]
    public void SetFilesFolder_MovesRowsInAndBackOutToTheRoot()
    {
        var work = _store.CreateFolder(Account, SelfKind, Account, null, "Work");
        var file = _store.Insert(Account, SelfRef(1), "a.pdf", "a.pdf", 10, "application/pdf", DateTime.UtcNow);
        Assert.Null(file.FolderId);

        _store.SetFilesFolder(Account, new[] { file.Id }, SelfKind, Account, work);
        Assert.Equal(work, Row(file.Id).FolderId);

        _store.SetFilesFolder(Account, new[] { file.Id }, SelfKind, Account, null);
        Assert.Null(Row(file.Id).FolderId);
    }

    [Fact]
    public void Insert_WithAFolderId_StoresIt()
    {
        var work = _store.CreateFolder(Account, SelfKind, Account, null, "Work");
        var file = _store.Insert(Account, SelfRef(1), "a.pdf", "a.pdf", 10, "application/pdf", DateTime.UtcNow,
            folderId: work);

        Assert.Equal(work, file.FolderId);
        Assert.Equal(work, Row(file.Id).FolderId);
    }

    [Fact]
    public void UpsertByRemote_TakesTheFolderOnInsert_ButKeepsALocalMoveOnReImport()
    {
        var work = _store.CreateFolder(Account, SelfKind, Account, null, "Work");
        var invoices = _store.CreateFolder(Account, SelfKind, Account, work, "Invoices");
        var remote = SelfRef(1);

        // Fresh import (empty index after a reinstall): the row is inserted into the caption's folder.
        var first = _store.UpsertByRemote(Account, remote, "a.pdf", "a.pdf", 10, "application/pdf",
            IndexStore.SourceNuvia, DateTime.UtcNow, folderId: work);
        Assert.Equal(IndexStore.UpsertOutcome.Inserted, first);
        Assert.Equal(work, Row(FileIdFor(remote)).FolderId);

        // The user then moves it locally to a different folder.
        _store.SetFilesFolder(Account, new[] { FileIdFor(remote) }, SelfKind, Account, invoices);

        // A later re-import over the same message refreshes identity but must NOT drag it back to "Work".
        var second = _store.UpsertByRemote(Account, remote, "a.pdf", "a.pdf", 10, "application/pdf",
            IndexStore.SourceNuvia, DateTime.UtcNow, folderId: work);
        Assert.Equal(IndexStore.UpsertOutcome.Updated, second);
        Assert.Equal(invoices, Row(FileIdFor(remote)).FolderId); // local placement preserved
    }

    // ----------------------------------------------------------------- helpers

    private IndexedFile Row(long id) => _store.ListForAccount(Account).Single(r => r.Id == id);

    private long FileIdFor(SavedMessageRef remote) =>
        _store.ListForAccount(Account).Single(r => r.Remote.MessageId == remote.MessageId).Id;

    private static SavedMessageRef SelfRef(int messageId) => new(
        PeerKind: SavedMessageRef.PeerKindSelf,
        PeerId: Account,
        MessageId: messageId,
        DocumentId: 1000 + messageId,
        AccessHash: 555,
        FileReference: new byte[] { 1, 2, 3 },
        DcId: 2,
        SizeBytes: 10,
        MimeType: "application/octet-stream",
        UploadedUtc: DateTime.UtcNow);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* best-effort temp cleanup */ }
    }
}
