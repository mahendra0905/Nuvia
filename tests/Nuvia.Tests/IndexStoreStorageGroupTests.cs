using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Nuvia.App.Services;
using Xunit;

namespace Nuvia.Tests;

/// <summary>
/// Tests for the managed storage-group rows in <see cref="IndexStore"/> — the piece that used to cap an
/// account at a single group and now lets it keep as many as Telegram allows. Each group is its own row
/// keyed by (account_id, channel_id): saves accumulate, deletes are channel-scoped, and one account can
/// never read or remove another's groups or files. No network, no credentials.
/// </summary>
public sealed class IndexStoreStorageGroupTests : IDisposable
{
    private const long Account = 7;
    private const long OtherAccount = 8;
    private const long VaultChannel = 42;
    private const long ArchiveChannel = 99;

    private readonly string _dbPath;
    private readonly IndexStore _store;

    public IndexStoreStorageGroupTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "nuvia-groups-" + Guid.NewGuid().ToString("N") + ".db");
        _store = new IndexStore(_dbPath);
        _store.Migrate();
    }

    [Fact]
    public void SaveStorageGroup_AccumulatesSeveralGroupsForOneAccount()
    {
        // The old "at most one group per account" cap is gone: a second save adds a row, not replaces.
        _store.SaveStorageGroup(Group(VaultChannel, "Vault"));
        _store.SaveStorageGroup(Group(ArchiveChannel, "Archive"));

        var groups = _store.GetStorageGroups(Account);
        Assert.Equal(2, groups.Count);
        Assert.Contains(groups, g => g.ChannelId == VaultChannel && g.Title == "Vault");
        Assert.Contains(groups, g => g.ChannelId == ArchiveChannel && g.Title == "Archive");
    }

    [Fact]
    public void SaveStorageGroup_SameChannelTwice_RefreshesInPlace()
    {
        _store.SaveStorageGroup(Group(VaultChannel, "Vault"));
        _store.SaveStorageGroup(Group(VaultChannel, "Renamed vault", accessHash: 1234));

        var only = Assert.Single(_store.GetStorageGroups(Account));
        Assert.Equal("Renamed vault", only.Title);
        Assert.Equal(1234, only.AccessHash);
    }

    [Fact]
    public void GetStorageGroup_IsScopedByChannel_AndNullWhenUnknown()
    {
        _store.SaveStorageGroup(Group(VaultChannel, "Vault"));
        _store.SaveStorageGroup(Group(ArchiveChannel, "Archive"));

        Assert.Equal("Vault", _store.GetStorageGroup(Account, VaultChannel)!.Title);
        Assert.Equal("Archive", _store.GetStorageGroup(Account, ArchiveChannel)!.Title);
        Assert.Null(_store.GetStorageGroup(Account, 12345)); // no such group
        Assert.Empty(_store.GetStorageGroups(OtherAccount)); // another account has none
    }

    [Fact]
    public void GetStorageGroups_AreScopedPerAccount()
    {
        _store.SaveStorageGroup(Group(VaultChannel, "Mine"));
        _store.SaveStorageGroup(new ManagedStorageGroup(OtherAccount, VaultChannel, 777, "Theirs", DateTime.UtcNow));

        var mine = _store.GetStorageGroups(Account);
        Assert.Equal("Mine", Assert.Single(mine).Title);
    }

    [Fact]
    public void DeleteStorageGroup_RemovesOnlyThatChannel_LeavingSiblingGroupsAndFiles()
    {
        _store.SaveStorageGroup(Group(VaultChannel, "Vault"));
        _store.SaveStorageGroup(Group(ArchiveChannel, "Archive"));

        // One file in each group, plus one in Saved Messages (a different peer entirely).
        _store.Insert(Account, ChannelRef(VaultChannel, 1), "v.pdf", "v.pdf", 10, "application/pdf", DateTime.UtcNow);
        _store.Insert(Account, ChannelRef(ArchiveChannel, 2), "a.pdf", "a.pdf", 10, "application/pdf", DateTime.UtcNow);
        _store.Insert(Account, SelfRef(3), "s.txt", "s.txt", 10, "text/plain", DateTime.UtcNow);

        var removed = _store.DeleteStorageGroup(Account, VaultChannel);

        Assert.Equal(1, removed); // only the vault's file row
        Assert.Equal("Archive", Assert.Single(_store.GetStorageGroups(Account)).Title);

        var rows = _store.ListForAccount(Account);
        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, r => r.Remote.PeerId == VaultChannel); // the deleted group's files are gone
        Assert.Contains(rows, r => r.Remote.PeerId == ArchiveChannel);     // its sibling's file survives
        Assert.Contains(rows, r => r.Remote.PeerKind == SavedMessageRef.PeerKindSelf);
    }

    // ----------------------------------------------------------------- helpers

    private static ManagedStorageGroup Group(long channelId, string title, long accessHash = 777) =>
        new(Account, channelId, accessHash, title, DateTime.UtcNow);

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

    private static SavedMessageRef ChannelRef(long channelId, int messageId) => new(
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

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* best-effort temp cleanup */ }
    }
}
