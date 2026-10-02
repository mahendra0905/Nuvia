using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nuvia.App.Services;

/// <summary>
/// Rebuilds the local file index from Telegram for the signed-in account — the piece that makes files
/// survive an uninstall/reinstall (which wipes the local SQLite index but leaves everything on Telegram).
/// <para>
/// It is deliberately UI-free and takes only the three services it orchestrates, so it can be unit-tested
/// with a fake <see cref="ITelegramStorage"/>. It performs three read-only passes and writes only to the
/// local index — it never uploads, deletes, sends a message, or edits anything on Telegram:
/// </para>
/// <list type="number">
/// <item><b>Group rediscovery.</b> When a managed group exists on Telegram but has no local row (e.g. after
/// a reinstall), it asks the storage layer for this account's Nuvia-marked private groups and re-saves each
/// one it does not already know. Groups already known locally are kept as-is and never re-discovered.</item>
/// <item><b>Saved Messages import.</b> It reads the account's own Saved Messages history above the stored
/// watermark. Files Nuvia uploaded (recognised by the invisible caption marker) are always indexed;
/// external files are indexed only when the account's "show external files" preference is on.</item>
/// <item><b>Managed group import.</b> For each usable group it reads that group's history above the group's
/// own watermark and indexes <b>every</b> file, whoever added it — a group intentionally shows all
/// files — recording provenance in the row's source for information only.</item>
/// </list>
/// <para>
/// Each peer's scan high-water mark is advanced after its pass, so repeat rebuilds are cheap. Turning the
/// "show external files" preference on is what resets the Saved Messages watermark (elsewhere) to force a
/// full re-scan; this service only ever advances marks forward.
/// </para>
/// </summary>
public sealed class LibraryRebuildService
{
    private readonly IndexStore _index;
    private readonly ITelegramStorage _storage;
    private readonly SettingsStore _settings;

    public LibraryRebuildService(IndexStore index, ITelegramStorage storage, SettingsStore settings)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// Run one rebuild pass for <paramref name="accountId"/>. A no-op that returns
    /// <see cref="RebuildSummary.Empty"/> when there is no real account (demo/owner 0) or the storage layer
    /// is unavailable, so callers can run it unconditionally on sign-in. Best-effort for the group pass: an
    /// unreachable managed group is skipped (its local row is left untouched) rather than failing the whole
    /// rebuild. Honours cancellation between items.
    /// </summary>
    public async Task<RebuildSummary> RebuildAsync(long accountId, CancellationToken cancellationToken)
    {
        if (accountId <= 0 || !_storage.IsAvailable)
            return RebuildSummary.Empty;

        var showExternal = _settings.GetShowExternalSavedMessages(accountId);

        // One folder-id cache for the whole pass, keyed by (location kind, location peer, folder path), so a
        // first full import does not re-resolve the same folder chain for every file that lives in it.
        // EnsureFolderPath is already idempotent; this just avoids the repeat SQL within a single rebuild.
        var folderCache = new Dictionary<(string Kind, long PeerId, string Path), long>();

        // 1) Rediscover any managed groups we have no local record of (post-reinstall rebuild), then take
        //    the full local set to import.
        var (groups, groupsDiscovered) = await RediscoverGroupsAsync(accountId, cancellationToken);

        // 2) Saved Messages: Nuvia uploads always; external files only when the user opted in.
        var (savedNew, savedUpdated) = await ImportSavedMessagesAsync(accountId, showExternal, folderCache, cancellationToken);

        // 3) Managed groups: every file in each, whoever added it (a group shows all). Skip any unreachable.
        var groupNew = 0;
        var groupUpdated = 0;
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!group.LooksUsable())
                continue;

            var (inserted, updated) = await ImportGroupAsync(accountId, group, folderCache, cancellationToken);
            groupNew += inserted;
            groupUpdated += updated;
        }

        return new RebuildSummary(groupsDiscovered, savedNew, savedUpdated, groupNew, groupUpdated);
    }

    /// <summary>
    /// Rediscover and persist any of the account's managed private groups (recognised by their Nuvia marker)
    /// that have no local row yet — the post-reinstall case, where Telegram still holds the groups but the
    /// local index was wiped. Groups already known locally are kept as-is and never re-discovered or
    /// re-tagged. Returns the full local set to import (existing rows plus any freshly saved this pass) and
    /// whether at least one new group was discovered.
    /// </summary>
    private async Task<(IReadOnlyList<ManagedStorageGroup> Groups, bool Discovered)> RediscoverGroupsAsync(
        long accountId, CancellationToken cancellationToken)
    {
        var known = _index.GetStorageGroups(accountId);

        var discovered = await _storage.DiscoverManagedStorageGroupsAsync(cancellationToken);
        if (discovered.Count == 0)
            return (known, false);

        var knownChannels = new HashSet<long>();
        foreach (var g in known)
            knownChannels.Add(g.ChannelId);

        // Save every discovered group we don't already have a local row for; leave existing rows untouched.
        var anyNew = false;
        foreach (var d in discovered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (knownChannels.Contains(d.ChannelId))
                continue;

            var group = new ManagedStorageGroup(
                AccountId: accountId,
                ChannelId: d.ChannelId,
                AccessHash: d.AccessHash,
                Title: d.Title ?? string.Empty,
                CreatedUtc: DateTime.UtcNow);

            if (!group.LooksUsable())
                continue;

            _index.SaveStorageGroup(group);
            knownChannels.Add(d.ChannelId);
            anyNew = true;
        }

        // Re-read so newly-saved groups are included alongside any that already existed.
        var groups = anyNew ? _index.GetStorageGroups(accountId) : known;
        return (groups, anyNew);
    }

    /// <summary>
    /// Import the account's own Saved Messages above the stored watermark. Nuvia uploads are indexed as
    /// <see cref="IndexStore.SourceNuvia"/> always; external files are indexed as
    /// <see cref="IndexStore.SourceExternal"/> only when <paramref name="showExternal"/> is on. The
    /// watermark is advanced to the highest scanned id regardless, so a later pass is cheap.
    /// </summary>
    private async Task<(int Inserted, int Updated)> ImportSavedMessagesAsync(
        long accountId, bool showExternal,
        Dictionary<(string Kind, long PeerId, string Path), long> folderCache,
        CancellationToken cancellationToken)
    {
        var watermark = _index.GetImportWatermark(accountId, SavedMessageRef.PeerKindSelf, accountId);
        var result = await _storage.ImportSavedMessagesAsync(watermark, cancellationToken);

        var inserted = 0;
        var updated = 0;
        foreach (var doc in result.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Saved Messages keeps only files Nuvia stored, unless the user opted to also show external ones.
            if (!doc.IsNuviaUpload && !showExternal)
                continue;

            var source = doc.IsNuviaUpload ? IndexStore.SourceNuvia : IndexStore.SourceExternal;
            if (Upsert(accountId, doc, source, folderCache) == IndexStore.UpsertOutcome.Inserted)
                inserted++;
            else
                updated++;
        }

        if (result.HighestScannedMessageId > 0)
            _index.SetImportWatermark(accountId, SavedMessageRef.PeerKindSelf, accountId, result.HighestScannedMessageId);

        return (inserted, updated);
    }

    /// <summary>
    /// Import the managed group's history above its watermark, indexing every file regardless of origin
    /// (the group always shows all). Provenance is recorded in the row's source for information. An
    /// unreachable group is a best-effort skip: the local group row is left untouched for a later pass.
    /// </summary>
    private async Task<(int Inserted, int Updated)> ImportGroupAsync(
        long accountId, ManagedStorageGroup group,
        Dictionary<(string Kind, long PeerId, string Path), long> folderCache,
        CancellationToken cancellationToken)
    {
        var watermark = _index.GetImportWatermark(accountId, SavedMessageRef.PeerKindChannel, group.ChannelId);

        HistoryImportResult result;
        try
        {
            result = await _storage.ImportGroupHistoryAsync(group, watermark, cancellationToken);
        }
        catch (StorageGroupUnavailableException)
        {
            // The group can't be reached right now (left, deleted, or temporarily unavailable). Don't fail
            // the rebuild and don't touch the local group row — a later pass can pick it up.
            return (0, 0);
        }

        var inserted = 0;
        var updated = 0;
        foreach (var doc in result.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var source = doc.IsNuviaUpload ? IndexStore.SourceNuvia : IndexStore.SourceExternal;
            if (Upsert(accountId, doc, source, folderCache) == IndexStore.UpsertOutcome.Inserted)
                inserted++;
            else
                updated++;
        }

        if (result.HighestScannedMessageId > 0)
            _index.SetImportWatermark(accountId, SavedMessageRef.PeerKindChannel, group.ChannelId, result.HighestScannedMessageId);

        return (inserted, updated);
    }

    /// <summary>
    /// Upsert one imported document into the local index. The display name seeds from the original file
    /// name on first insert; a later re-import preserves any local rename and the first-seen time (see
    /// <see cref="IndexStore.UpsertByRemote"/>). <c>indexedUtc</c> is "now" so it reads as when Nuvia first
    /// saw the file locally.
    /// <para>
    /// The folder id resolved from <see cref="ImportedDocument.FolderPath"/> is passed to
    /// <see cref="IndexStore.UpsertByRemote"/> for a <b>new</b> row only — a re-import never overwrites a
    /// folder the user has since set locally (that invariant lives in UpsertByRemote). So a fresh,
    /// post-reinstall index inserts every row and restores its folder from the caption marker, while a
    /// repeat pass over an existing row leaves a local move alone.
    /// </para>
    /// </summary>
    private IndexStore.UpsertOutcome Upsert(
        long accountId, ImportedDocument doc, string source,
        Dictionary<(string Kind, long PeerId, string Path), long> folderCache)
    {
        var folderId = ResolveFolderId(accountId, doc, folderCache);
        return _index.UpsertByRemote(
            accountId,
            doc.Reference,
            doc.OriginalFileName,
            doc.OriginalFileName,
            doc.Reference.SizeBytes,
            doc.Reference.MimeType,
            source,
            DateTime.UtcNow,
            folderId);
    }

    /// <summary>
    /// Turn an imported document's hidden folder path into a local folder id, find-or-creating the folder
    /// chain in the file's own location (self + account id, or channel + channel id). Returns null for the
    /// location root — no path, or a path that no longer parses into valid segments (defensive; the path was
    /// already validated when it was read from the caption). Results are cached per
    /// (location kind, location peer, path) for the duration of the rebuild pass.
    /// </summary>
    private long? ResolveFolderId(
        long accountId, ImportedDocument doc,
        Dictionary<(string Kind, long PeerId, string Path), long> folderCache)
    {
        var path = doc.FolderPath;
        if (string.IsNullOrEmpty(path))
            return null;

        var kind = doc.Reference.PeerKind;
        var peerId = doc.Reference.PeerId;
        var key = (kind, peerId, path);
        if (folderCache.TryGetValue(key, out var cached))
            return cached;

        var segments = FolderNames.Split(path);
        if (segments is null)
            return null;

        var folderId = _index.EnsureFolderPath(accountId, kind, peerId, segments);
        folderCache[key] = folderId;
        return folderId;
    }
}

/// <summary>
/// The outcome of one <see cref="LibraryRebuildService.RebuildAsync"/> pass. Counts are of local index
/// rows, split by peer and by whether a row was newly created or an existing one refreshed. Used by the UI
/// only to decide whether to refresh the list and what (if anything) to report — never for any remote action.
/// </summary>
public sealed record RebuildSummary(
    bool GroupDiscovered,
    int SavedMessagesInserted,
    int SavedMessagesUpdated,
    int GroupFilesInserted,
    int GroupFilesUpdated)
{
    /// <summary>A no-op rebuild (no real account, or storage unavailable).</summary>
    public static readonly RebuildSummary Empty = new(false, 0, 0, 0, 0);

    /// <summary>Total newly-indexed rows across Saved Messages and every managed group.</summary>
    public int TotalInserted => SavedMessagesInserted + GroupFilesInserted;

    /// <summary>True when anything changed locally — a newly discovered group, a new row, or a refreshed row.</summary>
    public bool AnyChange =>
        GroupDiscovered || TotalInserted > 0 || SavedMessagesUpdated > 0 || GroupFilesUpdated > 0;
}
