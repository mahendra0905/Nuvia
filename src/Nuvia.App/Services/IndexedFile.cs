using System;

namespace Nuvia.App.Services;

/// <summary>
/// A pointer to a single uploaded file living in the account's Saved Messages.
/// <para>
/// Every field here comes from a real server response (<c>messages.sendMedia</c> →
/// <c>TL.Message.media.document</c>) or from a metadata refresh. Nothing is ever synthesised:
/// if the remote call did not return an identifier, no record is written.
/// </para>
/// <para>
/// <see cref="FileReference"/> is short-lived by design — Telegram can invalidate it at any
/// time, which is why <c>TelegramStorageService.RefreshMetadataAsync</c> exists. It is the only
/// field expected to change after the record is written.
/// </para>
/// </summary>
public sealed record SavedMessageRef(
    string PeerKind,
    long PeerId,
    int MessageId,
    long DocumentId,
    long AccessHash,
    byte[] FileReference,
    int DcId,
    long SizeBytes,
    string? MimeType,
    DateTime UploadedUtc)
{
    /// <summary>Saved Messages — the account's own peer. The default destination.</summary>
    public const string PeerKindSelf = "self";

    /// <summary>A channel/supergroup peer — used by the one optional managed storage group.</summary>
    public const string PeerKindChannel = "channel";

    public bool LooksUsable()
        => MessageId > 0
           && DocumentId != 0
           && AccessHash != 0
           && FileReference is { Length: > 0 };
}

/// <summary>
/// One row of the local index, as stored in <c>%LOCALAPPDATA%\Nuvia\index.db</c>.
/// <para>
/// <see cref="OriginalFileName"/> is the name the file was sent with — kept verbatim so it can be
/// shown and compared. <see cref="DisplayName"/> is a purely local label used for the UI, which
/// keeps the original safe from being rewritten by future local-only edits.
/// </para>
/// </summary>
public sealed class IndexedFile
{
    public long Id { get; init; }
    public long OwnerId { get; init; }
    public SavedMessageRef Remote { get; init; } = null!;
    public string OriginalFileName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string? MimeType { get; init; }
    /// <summary>Server-side timestamp of the message that carries the file.</summary>
    public DateTime RemoteUploadUtc { get; init; }
    /// <summary>When this row was written locally.</summary>
    public DateTime IndexedUtc { get; init; }
    /// <summary>Last time the file_reference was re-fetched from the server, if ever.</summary>
    public DateTime? MetadataRefreshedUtc { get; init; }

    /// <summary>
    /// How this row entered the index: <see cref="IndexStore.SourceNuvia"/> for a file Nuvia itself
    /// uploaded (or recognised by its upload marker), or <see cref="IndexStore.SourceExternal"/> for a
    /// file that was added to the destination outside Nuvia and discovered during a remote import.
    /// Purely informational for Saved Messages (the "show external files" toggle keys off it); managed
    /// group rows always show regardless of source.
    /// </summary>
    public string Source { get; init; } = IndexStore.SourceNuvia;

    /// <summary>
    /// The Nuvia folder this file is filed under, or <c>null</c> for the root of its location. Purely a
    /// local label — Telegram has no folder concept, so this never travels with the file. What makes it
    /// survive a reinstall is the folder path hidden in the message caption (see
    /// <see cref="NuviaMarkers"/>): a rebuild writes it into a <b>new</b> row, while re-importing an
    /// existing row deliberately leaves this column alone so a local move is never undone.
    /// </summary>
    public long? FolderId { get; init; }
}

/// <summary>
/// One Nuvia folder, as stored in the <c>folders</c> table of the local index.
/// <para>
/// Folders are a purely local organisation layer <b>inside a single location</b> (Saved Messages, or one
/// managed group) — they are not disk paths, and Telegram has no equivalent. A folder belongs to exactly
/// one (account, location), and <see cref="ParentFolderId"/> gives nesting; null means a top-level folder
/// in that location.
/// </para>
/// </summary>
public sealed record LocalFolder(
    long Id,
    long AccountId,
    string LocationKind,
    long LocationPeerId,
    long? ParentFolderId,
    string Name,
    DateTime CreatedUtc);
