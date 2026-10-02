using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Nuvia.App.Services;
using Xunit;

namespace Nuvia.Tests;

/// <summary>
/// Tests for <see cref="LibraryRebuildService"/> — the UI-free coordinator that rebuilds the local index
/// from Telegram after a reinstall and re-scans Saved Messages when the "show external files" toggle is
/// switched on. Driven by a fake <see cref="ITelegramStorage"/> plus a real on-disk <see cref="IndexStore"/>
/// and <see cref="SettingsStore"/> in a temp folder — no network, no credentials.
/// </summary>
public sealed class LibraryRebuildServiceTests : IDisposable
{
    private const long Account = 7;
    private const long ChannelId = 42;

    private readonly string _dbPath;
    private readonly string _settingsPath;
    private readonly IndexStore _index;
    private readonly SettingsStore _settings;
    private readonly FakeTelegramStorage _storage = new();
    private readonly LibraryRebuildService _service;

    public LibraryRebuildServiceTests()
    {
        var stamp = Guid.NewGuid().ToString("N");
        _dbPath = Path.Combine(Path.GetTempPath(), "nuvia-rebuild-" + stamp + ".db");
        _settingsPath = Path.Combine(Path.GetTempPath(), "nuvia-rebuild-" + stamp + ".json");
        _index = new IndexStore(_dbPath);
        _index.Migrate();
        _settings = new SettingsStore(_settingsPath);
        _service = new LibraryRebuildService(_index, _storage, _settings);
    }

    // ------------------------------------------------------------------- tests

    [Fact]
    public async Task NoRealAccount_ReturnsEmpty_AndTouchesNothing()
    {
        _storage.SavedDocs.Add(NuviaSelf(10, "a.txt"));

        var summary = await _service.RebuildAsync(0, CancellationToken.None);

        Assert.Same(RebuildSummary.Empty, summary);
        Assert.Equal(0, _storage.SavedCallCount);
        Assert.Equal(0, _storage.DiscoverCallCount);
    }

    [Fact]
    public async Task StorageUnavailable_ReturnsEmpty_AndTouchesNothing()
    {
        _storage.Available = false;
        _storage.SavedDocs.Add(NuviaSelf(10, "a.txt"));

        var summary = await _service.RebuildAsync(Account, CancellationToken.None);

        Assert.Same(RebuildSummary.Empty, summary);
        Assert.Equal(0, _storage.SavedCallCount);
    }

    [Fact]
    public async Task SavedMessages_ToggleOff_IndexesOnlyNuvia_AndAdvancesWatermark()
    {
        // Default: the toggle is off, so external Saved Messages files are skipped.
        _storage.SavedDocs.Add(NuviaSelf(10, "mine.txt"));
        _storage.SavedDocs.Add(ExternalSelf(11, "theirs.bin"));
        _storage.SavedHighestId = 11;

        var summary = await _service.RebuildAsync(Account, CancellationToken.None);

        Assert.Equal(1, summary.SavedMessagesInserted);
        Assert.Equal(0, summary.SavedMessagesUpdated);
        Assert.Equal(0, _storage.SavedAfterId); // first pass scans from the start (watermark 0)

        var rows = _index.ListForAccount(Account);
        var row = Assert.Single(rows);
        Assert.Equal(IndexStore.SourceNuvia, row.Source);
        Assert.Equal(11, _index.GetImportWatermark(Account, SavedMessageRef.PeerKindSelf, Account));
    }

    [Fact]
    public async Task SavedMessages_ToggleOn_IndexesExternalToo()
    {
        _settings.SetShowExternalSavedMessages(Account, true);
        _storage.SavedDocs.Add(NuviaSelf(10, "mine.txt"));
        _storage.SavedDocs.Add(ExternalSelf(11, "theirs.bin"));
        _storage.SavedHighestId = 11;

        var summary = await _service.RebuildAsync(Account, CancellationToken.None);

        Assert.Equal(2, summary.SavedMessagesInserted);
        var rows = _index.ListForAccount(Account);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Source == IndexStore.SourceNuvia);
        Assert.Contains(rows, r => r.Source == IndexStore.SourceExternal);
    }

    [Fact]
    public async Task SavedMessages_SecondPass_ResumesAboveStoredWatermark()
    {
        _storage.SavedDocs.Add(NuviaSelf(10, "mine.txt"));
        _storage.SavedHighestId = 10;
        await _service.RebuildAsync(Account, CancellationToken.None);
        Assert.Equal(10, _index.GetImportWatermark(Account, SavedMessageRef.PeerKindSelf, Account));

        // A second pass must ask the server only for messages above the stored watermark.
        await _service.RebuildAsync(Account, CancellationToken.None);
        Assert.Equal(10, _storage.SavedAfterId);
    }

    [Fact]
    public async Task SavedMessages_FirstPass_RestoresTheFolderHiddenInTheCaption()
    {
        // A fresh index after a reinstall: the file's caption names "Work/Invoices", so the rebuild must
        // recreate that folder chain and file the row into the leaf — not flatten it to the root.
        _storage.SavedDocs.Add(NuviaSelfInFolder(10, "a.txt", "Work/Invoices"));
        _storage.SavedHighestId = 10;

        await _service.RebuildAsync(Account, CancellationToken.None);

        var row = Assert.Single(_index.ListForAccount(Account));
        Assert.NotNull(row.FolderId);
        var leaf = _index.GetFolder(Account, row.FolderId!.Value)!;
        Assert.Equal("Invoices", leaf.Name);
        Assert.Equal("Work", _index.GetFolder(Account, leaf.ParentFolderId!.Value)!.Name);
    }

    [Fact]
    public async Task SavedMessages_SecondPass_DoesNotPullBackAFileTheUserMovedLocally()
    {
        _storage.SavedDocs.Add(NuviaSelfInFolder(10, "a.txt", "Work"));
        _storage.SavedHighestId = 10;
        await _service.RebuildAsync(Account, CancellationToken.None);

        // The user moves it out to the location root after the first import.
        var rowId = _index.ListForAccount(Account).Single().Id;
        _index.SetFilesFolder(Account, new[] { rowId }, SavedMessageRef.PeerKindSelf, Account, null);

        // A later rebuild sees the same "Work" caption, but must leave the local placement alone.
        await _service.RebuildAsync(Account, CancellationToken.None);

        Assert.Null(_index.ListForAccount(Account).Single().FolderId);
    }

    [Fact]
    public async Task Group_Rediscovered_WhenNoLocalRow_ThenImportsEveryFile()
    {
        // No local group row: the coordinator must find it by its marker and re-save it.
        _storage.Discovered.Add(new DiscoveredStorageGroup(ChannelId, 777, "Vault"));
        _storage.GroupDocs.Add(NuviaGroup(3, "mine.pdf"));
        _storage.GroupDocs.Add(ExternalGroup(4, "theirs.zip")); // group shows all, marker or not
        _storage.GroupHighestId = 4;

        var summary = await _service.RebuildAsync(Account, CancellationToken.None);

        Assert.True(summary.GroupDiscovered);
        Assert.Equal(1, _storage.DiscoverCallCount);
        Assert.Equal(2, summary.GroupFilesInserted); // both indexed regardless of origin

        var saved = _index.GetStorageGroup(Account, ChannelId);
        Assert.NotNull(saved);
        Assert.Equal(ChannelId, saved!.ChannelId);
        Assert.Equal(4, _index.GetImportWatermark(Account, SavedMessageRef.PeerKindChannel, ChannelId));

        var rows = _index.ListForAccount(Account);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(SavedMessageRef.PeerKindChannel, r.Remote.PeerKind));
    }

    [Fact]
    public async Task Group_KnownLocalRow_IsKeptAndNotDuplicated_NewGroupIsAdded()
    {
        // A group we already know locally is never re-saved (no duplicate row), while a group discovered on
        // Telegram that we have no local row for is added. Discovery therefore always runs.
        SaveLocalGroup();                                                                 // ChannelId
        _storage.Discovered.Add(new DiscoveredStorageGroup(ChannelId, 777, "Vault"));     // the one we know
        _storage.Discovered.Add(new DiscoveredStorageGroup(999, 111, "Second vault"));    // new to us

        var summary = await _service.RebuildAsync(Account, CancellationToken.None);

        Assert.True(summary.GroupDiscovered);        // the second group was discovered
        Assert.Equal(1, _storage.DiscoverCallCount); // discovery runs on every pass

        var groups = _index.GetStorageGroups(Account);
        Assert.Equal(2, groups.Count);               // the known row plus the new one — no duplicate
        Assert.Equal("Vault", groups.Single(g => g.ChannelId == ChannelId).Title); // known row left as-is
        Assert.Equal("Second vault", groups.Single(g => g.ChannelId == 999).Title);

        // Both usable groups were imported (each is a separate history scan).
        Assert.Equal(2, _storage.GroupCallCount);
    }

    [Fact]
    public async Task MultipleGroups_AreAllDiscoveredSavedAndImported()
    {
        const long secondChannel = 99;

        // Two Nuvia-marked groups on Telegram, neither known locally, each holding its own file.
        _storage.Discovered.Add(new DiscoveredStorageGroup(ChannelId, 777, "Vault"));
        _storage.Discovered.Add(new DiscoveredStorageGroup(secondChannel, 888, "Archive"));
        _storage.GroupDocs.Add(GroupDoc(ChannelId, 3, "mine.pdf"));
        _storage.GroupDocs.Add(GroupDoc(secondChannel, 4, "theirs.zip"));
        _storage.GroupHighestId = 4;

        var summary = await _service.RebuildAsync(Account, CancellationToken.None);

        Assert.True(summary.GroupDiscovered);
        Assert.Equal(2, summary.GroupFilesInserted); // one file inserted from each group

        var groups = _index.GetStorageGroups(Account);
        Assert.Equal(2, groups.Count);
        Assert.Contains(groups, g => g.ChannelId == ChannelId);
        Assert.Contains(groups, g => g.ChannelId == secondChannel);

        // Each group's file was indexed under its own channel — never collapsed into one group.
        var rows = _index.ListForAccount(Account);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Remote.PeerId == ChannelId && r.OriginalFileName == "mine.pdf");
        Assert.Contains(rows, r => r.Remote.PeerId == secondChannel && r.OriginalFileName == "theirs.zip");
    }

    [Fact]
    public async Task Group_Unavailable_IsBestEffortSkip_LeavesRowAndWatermark()
    {
        SaveLocalGroup();
        _storage.GroupUnavailable = true;

        var summary = await _service.RebuildAsync(Account, CancellationToken.None);

        Assert.Equal(0, summary.GroupFilesInserted);
        Assert.Equal(0, summary.GroupFilesUpdated);
        // The group row survives an unreachable pass, and its watermark is not advanced.
        Assert.NotNull(_index.GetStorageGroup(Account, ChannelId));
        Assert.Equal(0, _index.GetImportWatermark(Account, SavedMessageRef.PeerKindChannel, ChannelId));
    }

    [Fact]
    public async Task ReRun_OverSameMessages_CountsAsUpdated_NotInserted()
    {
        _storage.SavedDocs.Add(NuviaSelf(10, "mine.txt"));
        _storage.SavedHighestId = 10;

        var first = await _service.RebuildAsync(Account, CancellationToken.None);
        Assert.Equal(1, first.SavedMessagesInserted);

        // The same message seen again refreshes the existing row rather than adding a duplicate.
        var second = await _service.RebuildAsync(Account, CancellationToken.None);
        Assert.Equal(0, second.SavedMessagesInserted);
        Assert.Equal(1, second.SavedMessagesUpdated);
        Assert.Single(_index.ListForAccount(Account));
    }

    [Fact]
    public void RebuildSummary_AnyChange_AndTotals()
    {
        Assert.False(RebuildSummary.Empty.AnyChange);
        Assert.Equal(0, RebuildSummary.Empty.TotalInserted);

        Assert.True(new RebuildSummary(true, 0, 0, 0, 0).AnyChange);           // group discovered
        Assert.True(new RebuildSummary(false, 0, 2, 0, 0).AnyChange);          // saved rows refreshed
        Assert.Equal(3, new RebuildSummary(false, 1, 0, 2, 0).TotalInserted);  // saved + group inserts
    }

    // ----------------------------------------------------------------- helpers

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

    private static SavedMessageRef ChannelRef(int messageId) => ChannelRefFor(ChannelId, messageId);

    private static SavedMessageRef ChannelRefFor(long channelId, int messageId) => new(
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

    private static ImportedDocument NuviaSelf(int id, string name) => new(SelfRef(id), name, IsNuviaUpload: true);
    private static ImportedDocument NuviaSelfInFolder(int id, string name, string folderPath) =>
        new(SelfRef(id), name, IsNuviaUpload: true, FolderPath: folderPath);
    private static ImportedDocument ExternalSelf(int id, string name) => new(SelfRef(id), name, IsNuviaUpload: false);
    private static ImportedDocument NuviaGroup(int id, string name) => new(ChannelRef(id), name, IsNuviaUpload: true);
    private static ImportedDocument ExternalGroup(int id, string name) => new(ChannelRef(id), name, IsNuviaUpload: false);

    /// <summary>A file that lives in <paramref name="channelId"/>, tagged as a Nuvia upload.</summary>
    private static ImportedDocument GroupDoc(long channelId, int id, string name) =>
        new(ChannelRefFor(channelId, id), name, IsNuviaUpload: true);

    private ManagedStorageGroup SaveLocalGroup(string title = "Vault") =>
        SaveAndReturn(new ManagedStorageGroup(Account, ChannelId, 777, title, DateTime.UtcNow));

    private ManagedStorageGroup SaveAndReturn(ManagedStorageGroup group)
    {
        _index.SaveStorageGroup(group);
        return group;
    }

    /// <summary>
    /// A configurable, network-free <see cref="ITelegramStorage"/>. Only the three members the rebuild
    /// coordinator uses (the two imports and group discovery) do anything; every other member throws, so a
    /// test fails loudly if the coordinator ever reaches for an operation it must not perform on a rebuild.
    /// </summary>
    private sealed class FakeTelegramStorage : ITelegramStorage
    {
        public bool Available { get; set; } = true;
        public bool IsAvailable => Available;
        public string? UnavailableReason => Available ? null : "unavailable";

        public List<ImportedDocument> SavedDocs { get; } = new();
        public int SavedHighestId { get; set; }
        public int? SavedAfterId { get; private set; }
        public int SavedCallCount { get; private set; }

        public List<ImportedDocument> GroupDocs { get; } = new();
        public int GroupHighestId { get; set; }
        public int? GroupAfterId { get; private set; }
        public int GroupCallCount { get; private set; }
        public bool GroupUnavailable { get; set; }

        public List<DiscoveredStorageGroup> Discovered { get; } = new();
        public int DiscoverCallCount { get; private set; }

        public Task<HistoryImportResult> ImportSavedMessagesAsync(int afterMessageId, CancellationToken cancellationToken)
        {
            SavedCallCount++;
            SavedAfterId = afterMessageId;
            return Task.FromResult(new HistoryImportResult(SavedDocs.ToArray(), SavedHighestId));
        }

        public Task<HistoryImportResult> ImportGroupHistoryAsync(ManagedStorageGroup group, int afterMessageId, CancellationToken cancellationToken)
        {
            GroupCallCount++;
            GroupAfterId = afterMessageId;
            if (GroupUnavailable)
                throw new StorageGroupUnavailableException("The group can no longer be reached.");
            // Faithful to a real history read: a group only ever yields the files that live in it.
            var docs = GroupDocs.Where(d => d.Reference.PeerId == group.ChannelId).ToArray();
            return Task.FromResult(new HistoryImportResult(docs, GroupHighestId));
        }

        public Task<IReadOnlyList<DiscoveredStorageGroup>> DiscoverManagedStorageGroupsAsync(CancellationToken cancellationToken)
        {
            DiscoverCallCount++;
            return Task.FromResult<IReadOnlyList<DiscoveredStorageGroup>>(Discovered.ToArray());
        }

        // Not part of a rebuild — a rebuild must never upload, delete, rename or watch. Throwing keeps the
        // tests honest about the coordinator's read-only, index-only contract.
        public event EventHandler<RemoteDeletionEventArgs>? RemoteFilesDeleted { add { } remove { } }
        public void StartWatching() => throw new NotSupportedException();
        public Task<IReadOnlyList<RemoteMessageId>> FindMissingRemoteMessagesAsync(IReadOnlyList<SavedMessageRef> references, ManagedStorageGroup? channelGroup, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ManagedStorageGroup> CreatePrivateStorageGroupAsync(string title, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ManagedStorageGroup> RenameStorageGroupAsync(ManagedStorageGroup group, string newTitle, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteStorageGroupAsync(ManagedStorageGroup group, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SavedMessageRef> UploadDocumentAsync(string localPath, StorageDestination destination, IProgress<double>? progress, CancellationToken cancellationToken, TransferGate? pauseGate = null, string? folderPath = null) => throw new NotSupportedException();
        public Task DownloadDocumentAsync(SavedMessageRef reference, string destinationPath, IProgress<double>? progress, CancellationToken cancellationToken, TransferGate? pauseGate = null) => throw new NotSupportedException();
        public Task<SavedMessageRef> RefreshMetadataAsync(SavedMessageRef reference, ManagedStorageGroup? channelGroup, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteDocumentAsync(SavedMessageRef reference, ManagedStorageGroup? channelGroup, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpdateFolderMarkerAsync(SavedMessageRef reference, ManagedStorageGroup? channelGroup, string? folderPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void Dispose() { }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* best-effort */ }
        try { File.Delete(_settingsPath); } catch { /* best-effort */ }
    }
}
