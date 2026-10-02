using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nuvia.App.Services;

/// <summary>
/// Remote file operations against the signed-in account's chosen storage destination.
/// <para>
/// The destination is either the account's own Saved Messages (the default) or the one optional
/// managed private storage group. There is deliberately no chat enumeration, no arbitrary message
/// send, no bulk transfer and no history scanning here.
/// </para>
/// <para>
/// It also exposes a narrow live-delete watch: <see cref="StartWatching"/> subscribes to the account's
/// own update stream and raises <see cref="RemoteFilesDeleted"/> when messages are deleted anywhere,
/// and <see cref="FindMissingRemoteMessagesAsync"/> re-checks a set of already-known message ids to
/// catch deletions missed while offline. Both operate only on ids Nuvia already stored — never a
/// history enumeration. Implementations are <see cref="IDisposable"/> so the watch can be detached.
/// </para>
/// </summary>
public interface ITelegramStorage : IDisposable
{
    /// <summary>False when no account is signed in (or when running against the demo stand-in).</summary>
    bool IsAvailable { get; }

    /// <summary>Human-readable reason shown when <see cref="IsAvailable"/> is false. Null when available.</summary>
    string? UnavailableReason { get; }

    /// <summary>
    /// Create the one optional private storage group — a private supergroup (megagroup) owned solely by
    /// this account, with no public username, no invited members and no invite link. Returns the
    /// identifiers read back from the real server response. Only ever called after an explicit,
    /// confirmed user action; never on startup, login, refresh or any retry.
    /// </summary>
    Task<ManagedStorageGroup> CreatePrivateStorageGroupAsync(string title, CancellationToken cancellationToken);

    /// <summary>
    /// Change the title of the managed private storage group on Telegram (channels.editTitle). This edits
    /// only the group's own name — it never renames or edits any stored message. Returns the group with its
    /// title updated from the real server response. Only ever called after an explicit, confirmed user
    /// action. Throws <see cref="StorageGroupUnavailableException"/> when the group can no longer be reached.
    /// </summary>
    Task<ManagedStorageGroup> RenameStorageGroupAsync(ManagedStorageGroup group, string newTitle, CancellationToken cancellationToken);

    /// <summary>
    /// Permanently delete the managed private storage group from Telegram (channels.deleteChannel). This
    /// destroys the supergroup and every file stored in it — it is irreversible and only ever called after
    /// an explicit, confirmed user action. Removing the local group row and the orphaned file rows from
    /// Nuvia's index is the caller's separate responsibility. Throws
    /// <see cref="StorageGroupUnavailableException"/> when the group is already gone or unreachable.
    /// </summary>
    Task DeleteStorageGroupAsync(ManagedStorageGroup group, CancellationToken cancellationToken);

    /// <summary>
    /// Upload one file as a document to the given <paramref name="destination"/> (Saved Messages or the
    /// managed group). Returns a reference built from the real server response; never a synthesised one.
    /// When a <paramref name="pauseGate"/> is supplied the byte flow is genuinely pausable through it.
    /// <para>
    /// <paramref name="folderPath"/> is the "/"-joined Nuvia folder path the file is being uploaded into,
    /// or null/empty for the location root. It is hidden (invisibly) in the message caption by
    /// <see cref="NuviaMarkers.BuildUploadCaption"/> so a reinstall restores the file into the same folder;
    /// it never changes what is transferred or how. Throws <see cref="FolderPathTooLongException"/> when the
    /// encoded path would not fit Telegram's caption limit (refused rather than silently truncated).
    /// </para>
    /// </summary>
    Task<SavedMessageRef> UploadDocumentAsync(string localPath, StorageDestination destination, IProgress<double>? progress, CancellationToken cancellationToken, TransferGate? pauseGate = null, string? folderPath = null);

    /// <summary>
    /// Download the document described by <paramref name="reference"/> into
    /// <paramref name="destinationPath"/> (this writes the file as given — callers are responsible
    /// for downloading to a temporary path and moving it into place only on success).
    /// Throws <see cref="FileReferenceExpiredException"/> when the stored reference is stale.
    /// When a <paramref name="pauseGate"/> is supplied the byte flow is genuinely pausable through it.
    /// </summary>
    Task DownloadDocumentAsync(SavedMessageRef reference, string destinationPath, IProgress<double>? progress, CancellationToken cancellationToken, TransferGate? pauseGate = null);

    /// <summary>
    /// Re-read the remote message and return a refreshed reference. This is how an expired file
    /// reference is recovered. When the file lives in the managed group rather than Saved Messages, the
    /// caller must supply that <paramref name="channelGroup"/> so the message can be re-read through the
    /// channel; passing null for a channel-backed file yields a clear failure rather than a wrong peer.
    /// </summary>
    Task<SavedMessageRef> RefreshMetadataAsync(SavedMessageRef reference, ManagedStorageGroup? channelGroup, CancellationToken cancellationToken);

    /// <summary>
    /// Permanently delete the remote message described by <paramref name="reference"/> from Telegram —
    /// the account's own Saved Messages, or the managed group. This is irreversible and only ever called
    /// after an explicit, confirmed user action. When the file lives in the managed group the caller must
    /// supply that <paramref name="channelGroup"/> (mirroring <see cref="RefreshMetadataAsync"/>); passing
    /// null for a channel-backed file yields a clear failure rather than deleting from the wrong peer.
    /// Removing the row from Nuvia's local index is the caller's separate responsibility.
    /// </summary>
    Task DeleteDocumentAsync(SavedMessageRef reference, ManagedStorageGroup? channelGroup, CancellationToken cancellationToken);

    /// <summary>
    /// Rewrite the invisible Nuvia folder marker in the caption of the message described by
    /// <paramref name="reference"/> so that it carries <paramref name="folderPath"/> (null/empty = the
    /// location root). This is the one place Nuvia edits an already-sent message, and it is deliberately
    /// narrow: the caption of a Nuvia upload is only the invisible marker, so this changes nothing visible
    /// and leaves the document, its name and every remote identifier untouched — it exists solely so a
    /// reinstall restores the file into the folder the user has since moved or renamed it into. Only ever
    /// called for a Nuvia-uploaded row (never an external file, which Nuvia must not claim) and only after
    /// an explicit, confirmed user action (move-to-folder or folder rename).
    /// <para>
    /// When the file lives in the managed group the caller must supply that <paramref name="channelGroup"/>
    /// (mirroring <see cref="DeleteDocumentAsync"/>); passing null for a channel-backed file yields a clear
    /// failure rather than editing the wrong peer. Throws <see cref="FolderPathTooLongException"/> when the
    /// encoded path would not fit the caption limit.
    /// </para>
    /// </summary>
    Task UpdateFolderMarkerAsync(SavedMessageRef reference, ManagedStorageGroup? channelGroup, string? folderPath, CancellationToken cancellationToken);

    /// <summary>
    /// Raised when Telegram reports that one or more messages were deleted — from any source (this app,
    /// another device, the Telegram client). Only fires after <see cref="StartWatching"/>. It is raised on
    /// the client's own callback thread and carries only message identifiers; handlers must marshal to their
    /// UI thread and match the ids against the local index themselves (no UI or index work is done here).
    /// </summary>
    event EventHandler<RemoteDeletionEventArgs>? RemoteFilesDeleted;

    /// <summary>
    /// Begin watching the signed-in account's update stream for message deletions, raising
    /// <see cref="RemoteFilesDeleted"/> as they arrive. Idempotent and safe to call repeatedly; a no-op
    /// when no account is signed in. This subscribes to already-flowing updates only — it starts no
    /// history scan and creates no traffic of its own.
    /// </summary>
    void StartWatching();

    /// <summary>
    /// Re-check a set of already-known message references and return those the server no longer has
    /// (deleted remotely). This queries only the supplied ids — it is not a history enumeration. Channel
    /// (managed-group) references are only checked when <paramref name="channelGroup"/> is the matching,
    /// usable group; otherwise they are skipped rather than reported missing, so a temporarily unreachable
    /// group never triggers a false deletion. Best-effort: transient server errors yield fewer results
    /// rather than throwing.
    /// </summary>
    Task<IReadOnlyList<RemoteMessageId>> FindMissingRemoteMessagesAsync(
        IReadOnlyList<SavedMessageRef> references, ManagedStorageGroup? channelGroup, CancellationToken cancellationToken);

    /// <summary>
    /// Read the account's own Saved Messages history and return every message that carries a downloadable
    /// document, each classified by whether its caption holds Nuvia's invisible upload marker
    /// (<see cref="ImportedDocument.IsNuviaUpload"/>). This is how files are rebuilt after a reinstall wiped
    /// the local index. Only messages with an id greater than <paramref name="afterMessageId"/> are scanned
    /// (pass the stored import watermark, or 0 for a full first scan), so repeat passes are cheap. The
    /// returned <see cref="HistoryImportResult.HighestScannedMessageId"/> is the new watermark. This reads
    /// history but sends nothing, deletes nothing and creates no traffic beyond the paged reads.
    /// </summary>
    Task<HistoryImportResult> ImportSavedMessagesAsync(int afterMessageId, CancellationToken cancellationToken);

    /// <summary>
    /// Read the managed private group's history and return every message that carries a downloadable
    /// document. Unlike Saved Messages, the group intentionally shows all files regardless of origin, so
    /// callers keep every result; the <see cref="ImportedDocument.IsNuviaUpload"/> flag is still filled in
    /// for information. Only messages newer than <paramref name="afterMessageId"/> are scanned. Throws
    /// <see cref="StorageGroupUnavailableException"/> when the group can no longer be reached.
    /// </summary>
    Task<HistoryImportResult> ImportGroupHistoryAsync(ManagedStorageGroup group, int afterMessageId, CancellationToken cancellationToken);

    /// <summary>
    /// Find the account's own private storage groups by their Nuvia marker — a supergroup this account
    /// created whose <c>about</c> text carries <see cref="NuviaMarkers.GroupAboutMarker"/>. This is how the
    /// managed-group record is rebuilt after a reinstall. It reads only chat metadata and group descriptions
    /// (never message history) and touches no group that is not already marked as Nuvia's. Groups created
    /// before the marker existed carry an empty <c>about</c> and are not rediscovered; Nuvia never edits an
    /// existing group's description to tag it.
    /// </summary>
    Task<IReadOnlyList<DiscoveredStorageGroup>> DiscoverManagedStorageGroupsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One document found while importing a peer's history. <see cref="IsNuviaUpload"/> is true when the
/// message caption carries Nuvia's invisible upload marker (the file was uploaded by Nuvia), false when
/// it does not (a file added to the destination outside Nuvia — "external"). Saved Messages keeps only
/// Nuvia uploads unless the user opts to show external files; the managed group keeps everything. Every
/// identifier in <see cref="Reference"/> is read from the real server message.
/// <para>
/// <see cref="FolderPath"/> is the Nuvia folder path recovered from the invisible caption marker
/// (<see cref="NuviaMarkers.TryReadFolderPath"/>), or null when the file carries none (external files, root
/// uploads, and anything whose marker is missing or corrupt). It is what lets a reinstall restore each file
/// into its folder; it is only ever applied to a freshly inserted row, never to overwrite a folder the user
/// has since set locally.
/// </para>
/// </summary>
public sealed record ImportedDocument(SavedMessageRef Reference, string OriginalFileName, bool IsNuviaUpload, string? FolderPath = null);

/// <summary>
/// The outcome of one history-import pass over a single peer. <see cref="Documents"/> holds only the
/// messages that carried a resolvable document (text, service and non-document media are skipped).
/// <see cref="HighestScannedMessageId"/> is the largest message id the server returned during the pass,
/// or 0 when nothing new was scanned; the caller advances its import watermark to this value so a later
/// pass resumes above it. It advances past skipped messages too, so a peer full of non-Nuvia content is
/// never rescanned in full.
/// </summary>
public sealed record HistoryImportResult(IReadOnlyList<ImportedDocument> Documents, int HighestScannedMessageId);

/// <summary>
/// A private storage group discovered on Telegram by its Nuvia marker (its <c>about</c> text carries
/// <see cref="NuviaMarkers.GroupAboutMarker"/>). Used to rebuild the managed-group record after a
/// reinstall wiped the local index. Every field is read from the real server response.
/// </summary>
public sealed record DiscoveredStorageGroup(long ChannelId, long AccessHash, string Title);

/// <summary>
/// Identifies one remote message by the peer it lives in and its message id. <paramref name="PeerKind"/>
/// is <see cref="SavedMessageRef.PeerKindSelf"/> or <see cref="SavedMessageRef.PeerKindChannel"/>; for
/// self-chat deletions <paramref name="PeerId"/> is not meaningful (message ids are unique across the
/// account's own dialogs) and callers match self ids by <paramref name="MessageId"/> alone.
/// </summary>
public readonly record struct RemoteMessageId(string PeerKind, long PeerId, int MessageId);

/// <summary>Carries the batch of messages Telegram reported as deleted for <see cref="ITelegramStorage.RemoteFilesDeleted"/>.</summary>
public sealed class RemoteDeletionEventArgs : EventArgs
{
    public RemoteDeletionEventArgs(IReadOnlyList<RemoteMessageId> deletions)
        => Deletions = deletions ?? throw new ArgumentNullException(nameof(deletions));

    /// <summary>The deleted messages. May include ids Nuvia does not track; handlers filter to their own rows.</summary>
    public IReadOnlyList<RemoteMessageId> Deletions { get; }
}

/// <summary>
/// Telegram rejected the stored file reference. The file is still there — the reference just needs
/// to be re-read from the message before the download can be retried.
/// </summary>
public sealed class FileReferenceExpiredException : Exception
{
    public FileReferenceExpiredException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// The managed private storage group can no longer be reached — its identifiers are missing locally,
/// or the server refused access to it (left, deleted, or revoked). Nuvia surfaces this plainly and
/// never silently recreates the group.
/// </summary>
public sealed class StorageGroupUnavailableException : Exception
{
    public StorageGroupUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
