using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nuvia.App.Services;

/// <summary>
/// Stand-in used when there is no signed-in account — most importantly in demo mode.
/// <para>
/// Every operation refuses. It deliberately does <b>not</b> return a fake reference and does not
/// write anything to the index: Phase 4 is not allowed to fabricate remote identifiers or to record
/// a success that did not happen.
/// </para>
/// </summary>
public sealed class UnavailableStorageService : ITelegramStorage
{
    private readonly string _reason;

    public UnavailableStorageService(string reason)
    {
        _reason = string.IsNullOrWhiteSpace(reason) ? "Saved Messages is not available." : reason;
    }

    public bool IsAvailable => false;

    public string? UnavailableReason => _reason;

    public Task<ManagedStorageGroup> CreatePrivateStorageGroupAsync(string title, CancellationToken cancellationToken)
        => throw new InvalidOperationException(_reason);

    public Task<ManagedStorageGroup> RenameStorageGroupAsync(ManagedStorageGroup group, string newTitle, CancellationToken cancellationToken)
        => throw new InvalidOperationException(_reason);

    public Task DeleteStorageGroupAsync(ManagedStorageGroup group, CancellationToken cancellationToken)
        => throw new InvalidOperationException(_reason);

    public Task<SavedMessageRef> UploadDocumentAsync(string localPath, StorageDestination destination, IProgress<double>? progress, CancellationToken cancellationToken, TransferGate? pauseGate = null, string? folderPath = null)
        => throw new InvalidOperationException(_reason);

    public Task DownloadDocumentAsync(SavedMessageRef reference, string destinationPath, IProgress<double>? progress, CancellationToken cancellationToken, TransferGate? pauseGate = null)
        => throw new InvalidOperationException(_reason);

    public Task<SavedMessageRef> RefreshMetadataAsync(SavedMessageRef reference, ManagedStorageGroup? channelGroup, CancellationToken cancellationToken)
        => throw new InvalidOperationException(_reason);

    public Task DeleteDocumentAsync(SavedMessageRef reference, ManagedStorageGroup? channelGroup, CancellationToken cancellationToken)
        => throw new InvalidOperationException(_reason);

    public Task UpdateFolderMarkerAsync(SavedMessageRef reference, ManagedStorageGroup? channelGroup, string? folderPath, CancellationToken cancellationToken)
        => throw new InvalidOperationException(_reason);

    // No account, so nothing is ever watched and no deletion can be reported. The empty add/remove
    // accessors keep the interface satisfied without the compiler warning about an unused event (CS0067).
    public event EventHandler<RemoteDeletionEventArgs>? RemoteFilesDeleted { add { } remove { } }

    public void StartWatching() { }

    public Task<IReadOnlyList<RemoteMessageId>> FindMissingRemoteMessagesAsync(
        IReadOnlyList<SavedMessageRef> references, ManagedStorageGroup? channelGroup, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<RemoteMessageId>>(Array.Empty<RemoteMessageId>());

    // Nothing is signed in, so there is no history to import and no group to discover. These return
    // empty rather than throwing: a rebuild pass over an unavailable account is a harmless no-op, not an
    // error, so callers can run it unconditionally.
    public Task<HistoryImportResult> ImportSavedMessagesAsync(int afterMessageId, CancellationToken cancellationToken)
        => Task.FromResult(new HistoryImportResult(Array.Empty<ImportedDocument>(), 0));

    public Task<HistoryImportResult> ImportGroupHistoryAsync(ManagedStorageGroup group, int afterMessageId, CancellationToken cancellationToken)
        => Task.FromResult(new HistoryImportResult(Array.Empty<ImportedDocument>(), 0));

    public Task<IReadOnlyList<DiscoveredStorageGroup>> DiscoverManagedStorageGroupsAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<DiscoveredStorageGroup>>(Array.Empty<DiscoveredStorageGroup>());

    public void Dispose() { }
}
