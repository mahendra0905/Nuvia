using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TL;
using WTelegram;

namespace Nuvia.App.Services;

/// <summary>
/// Real Saved Messages storage for the signed-in account.
/// <para>
/// Uploads use <c>UploadFileAsync</c> followed by <c>SendMessageAsync</c> with an
/// <c>InputMediaUploadedDocument</c>, so the file lands as a document (never as a photo or video
/// bubble) on the account's own Saved Messages peer. Every identifier in the returned
/// <see cref="SavedMessageRef"/> is read back out of the server's message object.
/// </para>
/// <para>
/// Downloads use <c>DownloadFileAsync</c> against a <c>TL.Document</c> reconstructed from the index.
/// Telegram can invalidate a stored file reference at any time; that surfaces as
/// <see cref="FileReferenceExpiredException"/> so the caller can refresh and retry once.
/// </para>
/// </summary>
public sealed class TelegramStorageService : ITelegramStorage
{
    private readonly TelegramAuthService _auth;

    public TelegramStorageService(TelegramAuthService auth)
    {
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
    }

    public bool IsAvailable => _auth.IsCompleted;

    public string? UnavailableReason => IsAvailable ? null : "Not signed in.";

    private Client RequireClient()
    {
        var client = _auth.SignedInClient;
        if (client is null)
            throw new InvalidOperationException("No account is signed in, so Saved Messages cannot be reached.");
        return client;
    }

    private long RequireAccountId()
        => _auth.AccountId
           ?? throw new InvalidOperationException("No account is signed in.");

    // ------------------------------------------------------- create group

    /// <inheritdoc />
    public async Task<ManagedStorageGroup> CreatePrivateStorageGroupAsync(
        string title,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("A group name is required.", nameof(title));

        var trimmed = title.Trim();
        // Telegram caps channel/group titles at 128 UTF-8 chars; keep well within and never send empty.
        if (trimmed.Length > 128)
            trimmed = trimmed[..128];

        var client = RequireClient();
        var accountId = RequireAccountId();

        cancellationToken.ThrowIfCancellationRequested();

        // Create a PRIVATE SUPERGROUP (megagroup):
        //   • megagroup: true  → a supergroup, per core.telegram.org/api/channel ("Supergroups can be
        //     created using the channels.createChannel method, by setting the megagroup flag").
        //   • broadcast is left false → this is explicitly NOT a broadcast channel.
        //   • messages.createChat (a basic group) is deliberately not used: it requires a non-empty
        //     users list, which would mean enumerating/adding contacts. This creates the group with the
        //     creator as its only member.
        //   • No username is set, so the group is private. No invite link is generated. No members are
        //     added. None of channels.updateUsername / messages.exportChatInvite / addChatUser are called.
        //   • The `about` (description) carries NuviaMarkers.GroupAboutText: an ordinary, honest sentence
        //     ending in the #NuviaStorage tag, so a later install with an empty index can recognise this
        //     group as Nuvia's (DiscoverManagedStorageGroupsAsync). It is set once, only here at creation.
        var updates = await client.Channels_CreateChannel(
            title: trimmed,
            about: NuviaMarkers.GroupAboutText,
            megagroup: true);

        var channel = ExtractCreatedChannel(updates);
        if (channel is null || channel.access_hash == 0)
            throw new InvalidOperationException(
                "Telegram accepted the request but did not return a usable group, so nothing was saved.");

        return new ManagedStorageGroup(
            AccountId: accountId,
            ChannelId: channel.id,
            AccessHash: channel.access_hash,
            Title: string.IsNullOrWhiteSpace(channel.title) ? trimmed : channel.title,
            CreatedUtc: DateTime.UtcNow);
    }

    /// <summary>
    /// Pull the newly created <see cref="Channel"/> out of the create call's Updates result.
    /// <para>
    /// <c>UpdatesBase.Chats</c> is WTelegramClient's helper that collates every chat carried by the
    /// update into a <c>Dictionary&lt;long, ChatBase&gt;</c> keyed by id (verified against the pinned
    /// 4.4.8 assembly). The create response carries exactly the one new channel, so the first
    /// <see cref="Channel"/> found is it.
    /// </para>
    /// </summary>
    private static Channel? ExtractCreatedChannel(UpdatesBase updates)
    {
        if (updates.Chats is not { } chats)
            return null;

        foreach (var chat in chats.Values)
            if (chat is Channel channel)
                return channel;

        return null;
    }

    // ------------------------------------------------------- rename / delete group

    /// <inheritdoc />
    public async Task<ManagedStorageGroup> RenameStorageGroupAsync(
        ManagedStorageGroup group,
        string newTitle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!group.LooksUsable())
            throw new StorageGroupUnavailableException(
                "The private storage group's identifiers are missing, so it could not be renamed.");
        if (string.IsNullOrWhiteSpace(newTitle))
            throw new ArgumentException("A group name is required.", nameof(newTitle));

        var trimmed = newTitle.Trim();
        // Same 128-char cap as creation; never send an empty title.
        if (trimmed.Length > 128)
            trimmed = trimmed[..128];

        var client = RequireClient();
        cancellationToken.ThrowIfCancellationRequested();

        // channels.editTitle changes ONLY the group's own title. It renames no stored message (Nuvia's
        // "rename" of a file is purely local and never reaches here), adds no member and creates no link.
        // InputChannel uses the positional (channel_id, access_hash) constructor in 4.4.8.
        var inputChannel = new InputChannel(group.ChannelId, group.AccessHash);
        UpdatesBase updates;
        try
        {
            updates = await client.Channels_EditTitle(inputChannel, trimmed);
        }
        catch (RpcException ex) when (IsChannelAccessLost(ex))
        {
            throw new StorageGroupUnavailableException(
                "The private storage group can no longer be reached, so its name was not changed.", ex);
        }

        // Prefer the title the server echoes back in the update; fall back to the trimmed request if the
        // response carried no channel object.
        var confirmed = ExtractCreatedChannel(updates);
        var title = confirmed is not null && !string.IsNullOrWhiteSpace(confirmed.title)
            ? confirmed.title
            : trimmed;

        return new ManagedStorageGroup(
            AccountId: group.AccountId,
            ChannelId: group.ChannelId,
            AccessHash: group.AccessHash,
            Title: title,
            CreatedUtc: group.CreatedUtc);
    }

    /// <inheritdoc />
    public async Task DeleteStorageGroupAsync(ManagedStorageGroup group, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!group.LooksUsable())
            throw new StorageGroupUnavailableException(
                "The private storage group's identifiers are missing, so there is nothing to delete remotely.");

        var client = RequireClient();
        cancellationToken.ThrowIfCancellationRequested();

        // channels.deleteChannel permanently deletes the supergroup and every message in it. This is
        // irreversible and only reached from an explicit, confirmed user action.
        var inputChannel = new InputChannel(group.ChannelId, group.AccessHash);
        try
        {
            await client.Channels_DeleteChannel(inputChannel);
        }
        catch (RpcException ex) when (IsChannelAccessLost(ex))
        {
            // Already gone or unreachable. Surface it plainly; the caller still purges the local rows so
            // Nuvia stops pointing at a group that no longer exists.
            throw new StorageGroupUnavailableException(
                "The private storage group is already gone or cannot be reached on Telegram.", ex);
        }
    }

    // ------------------------------------------------------------------ upload

    /// <inheritdoc />
    public async Task<SavedMessageRef> UploadDocumentAsync(
        string localPath,
        StorageDestination destination,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        TransferGate? pauseGate = null,
        string? folderPath = null)
    {
        if (string.IsNullOrWhiteSpace(localPath))
            throw new ArgumentException("A file to upload is required.", nameof(localPath));
        if (!File.Exists(localPath))
            throw new FileNotFoundException("The selected file no longer exists.", localPath);
        ArgumentNullException.ThrowIfNull(destination);

        // Build the caption up front so a too-long folder path is refused before any bytes are uploaded,
        // rather than after a full transfer. BuildUploadCaption throws FolderPathTooLongException here.
        var caption = NuviaMarkers.BuildUploadCaption(folderPath);

        var client = RequireClient();
        var accountId = RequireAccountId();

        // Resolve the target peer up front. Saved Messages is the account's own peer; the managed group
        // is reached through an InputPeerChannel built from its stored id + access hash. Nothing else is
        // ever a valid target here.
        var peer = ResolveUploadPeer(destination);

        var info = new FileInfo(localPath);
        if (info.Length <= 0)
            throw new InvalidOperationException("The selected file is empty; nothing was uploaded.");

        var fileName = Path.GetFileName(localPath);
        var mimeType = GuessMimeType(fileName);

        cancellationToken.ThrowIfCancellationRequested();

        // 1. Upload the bytes to Telegram's file servers.
        //    Note: UploadFileAsync has no cancellation parameter. Cancellation is honoured inside
        //    the progress callback (WTelegramClient's documented mechanism), so a cancel is seen
        //    mid-transfer rather than only between stages.
        void ReportUpload(long transmitted, long total)
        {
            // Throwing from the progress callback is the only way to abort an in-flight upload.
            cancellationToken.ThrowIfCancellationRequested();

            if (total > 0)
                progress?.Report(Math.Clamp((double)transmitted / total, 0d, 1d));
        }

        InputFileBase inputFile;
        if (pauseGate is null)
        {
            // No pause capability requested: the plain path overload keeps the original behaviour.
            // Still run it on a thread-pool thread (no captured UI SynchronizationContext) so the
            // library's read loop never lands on the WPF UI thread.
            inputFile = await Task.Run(() => client.UploadFileAsync(localPath, ReportUpload))
                .ConfigureAwait(false);
        }
        else
        {
            // Pause capability requested: read the file through a PausableStream so a pause genuinely
            // stalls the upload's own Read loop (a condition WTelegramClient tolerates) instead of
            // blocking its progress callback. We own the FileStream, so leaveOpen keeps disposal here.
            //
            // The whole read loop MUST run off the UI thread. PausableStream.Read blocks while paused;
            // if that Read ran on the UI thread the dispatcher would freeze (stuck cursor, nothing
            // clickable) and Resume — which also needs the UI thread — could never fire, deadlocking the
            // app. Task.Run starts the loop on the thread pool, where SynchronizationContext.Current is
            // null, so neither the library's stream reads nor the pause wait are ever posted back to the
            // UI thread.
            inputFile = await Task.Run(async () =>
            {
                await using var source = new FileStream(
                    localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                await using var pausable = new PausableStream(source, pauseGate, cancellationToken, leaveOpen: true);
                return await client.UploadFileAsync(pausable, fileName, ReportUpload).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 2. Post it into Saved Messages as a document.
        var media = new InputMediaUploadedDocument
        {
            file = inputFile,
            mime_type = mimeType,
            attributes = new DocumentAttribute[]
            {
                new DocumentAttributeFilename { file_name = fileName },
            },
            // force_file keeps Telegram from re-classifying the upload as a photo/video item,
            // so the message always carries a Dokument we can resolve again later.
            flags = InputMediaUploadedDocument.Flags.force_file,
        };

        // The caption carries NuviaMarkers.BuildUploadCaption(folderPath): an invisible zero-width
        // signature (plus the file's folder path when it has one) and no visible text, so the message
        // still renders as a bare document in Telegram while a later install can tell a Nuvia upload from a
        // file added outside Nuvia (ImportSavedMessagesAsync classifies by it) and restore its folder.
        var sent = await client.SendMessageAsync(peer, caption, media);

        if (sent is null)
            throw new InvalidOperationException(
                "Telegram accepted the upload but returned no message. The file may still exist remotely.");

        if (!TryExtractDocument(sent, accountId, out var reference, out _))
            throw new InvalidOperationException(
                "Telegram returned a message that did not contain a document. " +
                "The file may still exist in Saved Messages, but it cannot be indexed for download.");

        progress?.Report(1d);
        return reference;
    }

    // ---------------------------------------------------------------- download

    /// <inheritdoc />
    public async Task DownloadDocumentAsync(
        SavedMessageRef reference,
        string destinationPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        TransferGate? pauseGate = null)
    {
        if (reference is null) throw new ArgumentNullException(nameof(reference));
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException("A destination path is required.", nameof(destinationPath));
        if (!reference.LooksUsable())
            throw new InvalidOperationException("This record is missing the remote identifiers needed to download it.");

        var client = RequireClient();
        var document = ToDocument(reference);

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var target = new FileStream(
                destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            // When a pause gate is supplied, write through a PausableStream so a pause stalls the
            // download's own Write loop rather than blocking the progress callback. leaveOpen keeps the
            // underlying FileStream alive for the flush below and the enclosing await using.
            Stream sink = pauseGate is null
                ? target
                : new PausableStream(target, pauseGate, cancellationToken, leaveOpen: true);

            // Run the library's write loop on the thread pool (no captured UI SynchronizationContext).
            // A paused PausableStream.Write blocks its calling thread; keeping that off the WPF UI thread
            // is what stops a pause from freezing the app (stuck cursor, nothing clickable) and
            // deadlocking against Resume, which also needs the UI thread.
            await Task.Run(() => client.DownloadFileAsync(
                document,
                sink,
                thumbSize: null,
                progress: (transmitted, total) =>
                {
                    // Same cancellation rule as upload: abort by throwing from the callback.
                    cancellationToken.ThrowIfCancellationRequested();

                    if (total > 0)
                        progress?.Report(Math.Clamp((double)transmitted / total, 0d, 1d));
                })).ConfigureAwait(false);

            await target.FlushAsync(cancellationToken);
        }
        catch (RpcException ex) when (IsFileReferenceExpired(ex))
        {
            throw new FileReferenceExpiredException(
                "The saved file reference has expired; metadata needs to be refreshed before downloading.", ex);
        }

        progress?.Report(1d);
    }

    // ---------------------------------------------------------------- refresh

    /// <inheritdoc />
    public async Task<SavedMessageRef> RefreshMetadataAsync(
        SavedMessageRef reference,
        ManagedStorageGroup? channelGroup,
        CancellationToken cancellationToken)
    {
        if (reference is null) throw new ArgumentNullException(nameof(reference));
        if (reference.MessageId <= 0)
            throw new InvalidOperationException("This record has no message id, so its metadata cannot be refreshed.");

        var client = RequireClient();
        var accountId = RequireAccountId();

        cancellationToken.ThrowIfCancellationRequested();

        Messages_MessagesBase result;
        if (reference.PeerKind == SavedMessageRef.PeerKindChannel)
        {
            // A channel/supergroup message must be re-read through the channel itself — messages.getMessages
            // would not resolve it. That requires the group's access hash, held only by the managed-group
            // record. If it is missing, or points at a different channel, the group is effectively lost;
            // Nuvia says so plainly and never recreates it.
            if (channelGroup is null || !channelGroup.LooksUsable() || channelGroup.ChannelId != reference.PeerId)
                throw new StorageGroupUnavailableException(
                    "This file is stored in the private group, but that group can no longer be reached, "
                    + "so its reference could not be refreshed.");

            // InputChannel in WTelegramClient 4.4.8 has a positional constructor (channel_id, access_hash)
            // and no parameterless ctor, so it is built positionally rather than with an object initializer.
            var inputChannel = new InputChannel(channelGroup.ChannelId, channelGroup.AccessHash);

            try
            {
                result = await client.Channels_GetMessages(inputChannel, new InputMessageID { id = reference.MessageId });
            }
            catch (RpcException ex) when (IsChannelAccessLost(ex))
            {
                throw new StorageGroupUnavailableException(
                    "Telegram refused access to the private storage group, so the file could not be refreshed.", ex);
            }
        }
        else
        {
            // Saved Messages: the peer is this account, so messages.getMessages resolves it directly.
            result = await client.Messages_GetMessages(new InputMessageID { id = reference.MessageId });
        }

        var message = result?.Messages?.OfType<Message>().FirstOrDefault();
        if (message is null)
            throw new InvalidOperationException(
                "The file's message could not be found. It may have been deleted remotely.");

        if (!TryExtractDocument(message, accountId, out var refreshed, out _))
            throw new InvalidOperationException("The message no longer carries a downloadable document.");

        return refreshed;
    }

    // ----------------------------------------------------------------- delete

    /// <inheritdoc />
    public async Task DeleteDocumentAsync(
        SavedMessageRef reference,
        ManagedStorageGroup? channelGroup,
        CancellationToken cancellationToken)
    {
        if (reference is null) throw new ArgumentNullException(nameof(reference));
        if (reference.MessageId <= 0)
            throw new InvalidOperationException("This record has no message id, so its remote message cannot be deleted.");

        var client = RequireClient();

        cancellationToken.ThrowIfCancellationRequested();

        if (reference.PeerKind == SavedMessageRef.PeerKindChannel)
        {
            // A channel/supergroup message is deleted through the channel itself, which needs the group's
            // access hash — held only by the managed-group record. Mirrors RefreshMetadataAsync exactly:
            // if the record is missing or points at a different channel, the group is effectively lost and
            // Nuvia says so plainly rather than deleting from the wrong peer.
            if (channelGroup is null || !channelGroup.LooksUsable() || channelGroup.ChannelId != reference.PeerId)
                throw new StorageGroupUnavailableException(
                    "This file is stored in the private group, but that group can no longer be reached, "
                    + "so its remote message could not be deleted.");

            // InputChannel in 4.4.8 has a positional (channel_id, access_hash) constructor only.
            var inputChannel = new InputChannel(channelGroup.ChannelId, channelGroup.AccessHash);

            try
            {
                await client.Channels_DeleteMessages(inputChannel, reference.MessageId);
            }
            catch (RpcException ex) when (IsChannelAccessLost(ex))
            {
                throw new StorageGroupUnavailableException(
                    "Telegram refused access to the private storage group, so the file could not be deleted.", ex);
            }
        }
        else
        {
            // Saved Messages: the peer is this account. revoke:true deletes the message for the account
            // (there is no other party in a self-chat), matching how it was uploaded.
            await client.Messages_DeleteMessages(new[] { reference.MessageId }, revoke: true);
        }
    }

    // ------------------------------------------------------ folder marker edit

    /// <inheritdoc />
    public async Task UpdateFolderMarkerAsync(
        SavedMessageRef reference,
        ManagedStorageGroup? channelGroup,
        string? folderPath,
        CancellationToken cancellationToken)
    {
        if (reference is null) throw new ArgumentNullException(nameof(reference));
        if (reference.MessageId <= 0)
            throw new InvalidOperationException("This record has no message id, so its caption cannot be edited.");

        // Build the new caption up front so a too-long folder path is refused (FolderPathTooLongException)
        // before any remote call. For a Nuvia upload the caption IS only the invisible marker, so this
        // rewrites nothing a human can see — it only moves the hidden folder tag.
        var caption = NuviaMarkers.BuildUploadCaption(folderPath);

        var client = RequireClient();

        cancellationToken.ThrowIfCancellationRequested();

        // Resolve the peer exactly as RefreshMetadataAsync / DeleteDocumentAsync do: a channel message is
        // edited through the channel itself (needs the managed group's access hash), Saved Messages through
        // the account's own peer. A missing or mismatched group record is a plain failure, never a wrong peer.
        InputPeer peer;
        if (reference.PeerKind == SavedMessageRef.PeerKindChannel)
        {
            if (channelGroup is null || !channelGroup.LooksUsable() || channelGroup.ChannelId != reference.PeerId)
                throw new StorageGroupUnavailableException(
                    "This file is stored in the private group, but that group can no longer be reached, "
                    + "so its folder marker could not be updated.");

            peer = new InputPeerChannel(channelGroup.ChannelId, channelGroup.AccessHash);
        }
        else
        {
            peer = InputPeer.Self;
        }

        try
        {
            // Named arguments, media left unset: messages.editMessage with no media keeps the existing
            // document attached and only replaces the message text (here, the invisible caption). Every
            // other optional parameter takes its default. Confirmed against the pinned WTelegramClient 4.4.8
            // signature (SchemaExtensions.Messages_EditMessage) — not an invented overload.
            await client.Messages_EditMessage(peer: peer, id: reference.MessageId, message: caption);
        }
        catch (RpcException ex) when (IsChannelAccessLost(ex))
        {
            throw new StorageGroupUnavailableException(
                "Telegram refused access to the private storage group, so the folder marker could not be updated.", ex);
        }
    }

    // ------------------------------------------------------------ live watch

    /// <inheritdoc />
    public event EventHandler<RemoteDeletionEventArgs>? RemoteFilesDeleted;

    private readonly object _watchLock = new();
    private Client? _watchedClient;
    private Func<UpdatesBase, Task>? _updateHandler;
    private bool _disposed;

    /// <inheritdoc />
    public void StartWatching()
    {
        lock (_watchLock)
        {
            if (_disposed)
                return;

            var client = _auth.SignedInClient;
            if (client is null)
                return; // not signed in yet — nothing to watch

            if (ReferenceEquals(_watchedClient, client))
                return; // already watching this client instance (idempotent)

            // A resume can hand us a fresh client; detach from any previous one first.
            DetachHandler();

            _updateHandler = HandleUpdatesAsync;
            // OnUpdates carries pushed changes (a delete from another device / the Telegram app);
            // OnOwnUpdates carries changes this account makes. Watching both means a deletion from any
            // source is caught. Re-pruning an already-gone row is harmless, so overlap is not a problem.
            client.OnUpdates += _updateHandler;
            client.OnOwnUpdates += _updateHandler;
            _watchedClient = client;
        }
    }

    /// <summary>
    /// Runs on WTelegramClient's own callback thread. It ONLY parses the batch for message deletions and
    /// raises <see cref="RemoteFilesDeleted"/> — it never touches the UI or the index, and never throws
    /// back into the update pump. Subscribers marshal to their own thread and match against the index.
    /// </summary>
    private Task HandleUpdatesAsync(UpdatesBase updates)
    {
        try
        {
            var gone = new List<RemoteMessageId>();

            foreach (var update in updates.UpdateList ?? Array.Empty<Update>())
            {
                switch (update)
                {
                    // UpdateDeleteChannelMessages DERIVES from UpdateDeleteMessages, so it must be matched
                    // first — otherwise channel deletions fall into the self-chat case and are misfiled.
                    case UpdateDeleteChannelMessages channelDelete:
                        foreach (var id in channelDelete.messages)
                            gone.Add(new RemoteMessageId(SavedMessageRef.PeerKindChannel, channelDelete.channel_id, id));
                        break;

                    case UpdateDeleteMessages selfDelete:
                        foreach (var id in selfDelete.messages)
                            gone.Add(new RemoteMessageId(SavedMessageRef.PeerKindSelf, 0, id));
                        break;
                }
            }

            if (gone.Count > 0)
                RemoteFilesDeleted?.Invoke(this, new RemoteDeletionEventArgs(gone));
        }
        catch
        {
            // The update pump must keep running no matter what a handler does; swallow parsing hiccups.
        }

        return Task.CompletedTask;
    }

    private void DetachHandler()
    {
        if (_watchedClient is { } client && _updateHandler is { } handler)
        {
            client.OnUpdates -= handler;
            client.OnOwnUpdates -= handler;
        }

        _watchedClient = null;
        _updateHandler = null;
    }

    // --------------------------------------------------------- reconcile (catch-up)

    /// <inheritdoc />
    public async Task<IReadOnlyList<RemoteMessageId>> FindMissingRemoteMessagesAsync(
        IReadOnlyList<SavedMessageRef> references,
        ManagedStorageGroup? channelGroup,
        CancellationToken cancellationToken)
    {
        var missing = new List<RemoteMessageId>();
        if (references is null || references.Count == 0)
            return missing;

        var client = _auth.SignedInClient;
        if (client is null)
            return missing; // not signed in — report nothing rather than guessing

        // Saved Messages: peer is this account, so messages.getMessages resolves the ids directly.
        var selfIds = references
            .Where(r => r.PeerKind != SavedMessageRef.PeerKindChannel && r.MessageId > 0)
            .Select(r => r.MessageId)
            .Distinct()
            .ToList();
        await ProbeSelfAsync(client, selfIds, missing, cancellationToken);

        // Managed group: only the one channel that matches the supplied usable group is probed. Refs
        // pointing at any other / unreachable channel are left alone so a temporarily lost group can
        // never be mistaken for a mass deletion.
        if (channelGroup is not null && channelGroup.LooksUsable())
        {
            var channelIds = references
                .Where(r => r.PeerKind == SavedMessageRef.PeerKindChannel
                            && r.PeerId == channelGroup.ChannelId
                            && r.MessageId > 0)
                .Select(r => r.MessageId)
                .Distinct()
                .ToList();
            await ProbeChannelAsync(client, channelGroup, channelIds, missing, cancellationToken);
        }

        return missing;
    }

    /// <summary>Probe Saved Messages ids in ≤100-id batches (Telegram's getMessages cap); absent ids = deleted.</summary>
    private static async Task ProbeSelfAsync(
        Client client, List<int> ids, List<RemoteMessageId> missing, CancellationToken cancellationToken)
    {
        foreach (var chunk in ids.Chunk(100))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await client.Messages_GetMessages(
                    chunk.Select(id => (InputMessage)new InputMessageID { id = id }).ToArray());
                var alive = AliveIds(result);
                foreach (var id in chunk)
                    if (!alive.Contains(id))
                        missing.Add(new RemoteMessageId(SavedMessageRef.PeerKindSelf, 0, id));
            }
            catch (RpcException)
            {
                // Best-effort: a rejected or flood-limited probe skips this batch rather than forcing a prune.
            }
        }
    }

    /// <summary>Probe managed-group ids through the channel in ≤100-id batches; absent ids = deleted.</summary>
    private static async Task ProbeChannelAsync(
        Client client, ManagedStorageGroup group, List<int> ids, List<RemoteMessageId> missing, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
            return;

        var inputChannel = new InputChannel(group.ChannelId, group.AccessHash);
        foreach (var chunk in ids.Chunk(100))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await client.Channels_GetMessages(
                    inputChannel,
                    chunk.Select(id => (InputMessage)new InputMessageID { id = id }).ToArray());
                var alive = AliveIds(result);
                foreach (var id in chunk)
                    if (!alive.Contains(id))
                        missing.Add(new RemoteMessageId(SavedMessageRef.PeerKindChannel, group.ChannelId, id));
            }
            catch (RpcException)
            {
                // Channel refused/unreachable right now: skip, never prune files that may still exist.
            }
        }
    }

    /// <summary>The ids still present in a getMessages result (MessageEmpty/absent entries are excluded).</summary>
    private static HashSet<int> AliveIds(Messages_MessagesBase? result)
        => new(result?.Messages?.OfType<Message>().Select(m => m.id) ?? Enumerable.Empty<int>());

    // ---------------------------------------------------- import (rebuild from Telegram)

    /// <inheritdoc />
    public async Task<HistoryImportResult> ImportSavedMessagesAsync(int afterMessageId, CancellationToken cancellationToken)
    {
        var client = RequireClient();
        var accountId = RequireAccountId();
        // Saved Messages is the account's own peer, so the imported refs classify as 'self'.
        return await ImportHistoryAsync(client, InputPeer.Self, accountId, afterMessageId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<HistoryImportResult> ImportGroupHistoryAsync(
        ManagedStorageGroup group, int afterMessageId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!group.LooksUsable())
            throw new StorageGroupUnavailableException(
                "The private storage group's identifiers are missing, so its history could not be imported.");

        var client = RequireClient();
        var accountId = RequireAccountId();
        var peer = new InputPeerChannel(group.ChannelId, group.AccessHash);

        try
        {
            return await ImportHistoryAsync(client, peer, accountId, afterMessageId, cancellationToken);
        }
        catch (RpcException ex) when (IsChannelAccessLost(ex))
        {
            throw new StorageGroupUnavailableException(
                "The private storage group can no longer be reached, so its history could not be imported.", ex);
        }
    }

    /// <summary>
    /// Page backwards through a peer's history (messages.getHistory) reading only messages newer than
    /// <paramref name="afterMessageId"/> (the <c>min_id</c> filter, "IDs more than min_id"), newest first,
    /// in 100-message pages (Telegram's cap). Every message that carries a resolvable document becomes an
    /// <see cref="ImportedDocument"/>; the highest message id seen across all pages (documents or not)
    /// becomes the new watermark, so skipped content still advances it. Honours cancellation between pages.
    /// </summary>
    private static async Task<HistoryImportResult> ImportHistoryAsync(
        Client client, InputPeer peer, long accountId, int afterMessageId, CancellationToken cancellationToken)
    {
        const int pageSize = 100;

        var documents = new List<ImportedDocument>();
        var highestScanned = 0;
        var offsetId = 0; // 0 = start from the most recent message and walk older each page.

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await client.Messages_GetHistory(
                peer,
                offset_id: offsetId,
                limit: pageSize,
                min_id: afterMessageId);

            var batch = page?.Messages;
            if (batch is null || batch.Length == 0)
                break;

            foreach (var mb in batch)
            {
                if (mb.ID > highestScanned)
                    highestScanned = mb.ID;

                if (!TryExtractDocument(mb, accountId, out var reference, out _))
                    continue;

                var caption = (mb as Message)?.message;
                var isNuvia = NuviaMarkers.CaptionIndicatesNuvia(caption);

                // The folder path is hidden in the same caption. It is read for every message (not just
                // Nuvia uploads) but is only non-null when the invisible marker is present and well-formed;
                // an external file, a root upload or a corrupt marker all read as null (the location root).
                var folderPath = NuviaMarkers.TryReadFolderPath(caption);
                documents.Add(new ImportedDocument(reference, ExtractDocumentFileName(mb), isNuvia, folderPath));
            }

            // A short page is the last one. Otherwise, continue below the smallest id in this batch;
            // combined with min_id the server stops once it reaches the watermark (an empty next page).
            if (batch.Length < pageSize)
                break;
            offsetId = batch.Min(m => m.ID);
        }

        return new HistoryImportResult(documents, highestScanned);
    }

    // ------------------------------------------------ discover managed groups (rebuild)

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveredStorageGroup>> DiscoverManagedStorageGroupsAsync(
        CancellationToken cancellationToken)
    {
        var client = RequireClient();
        cancellationToken.ThrowIfCancellationRequested();

        var found = new List<DiscoveredStorageGroup>();

        // Enumerate the account's dialogs to see which chats it belongs to. This reads only chat metadata
        // (id / access_hash / title / flags) — it browses no message history and exports no contacts.
        var dialogs = await client.Messages_GetAllDialogs();

        foreach (var chat in dialogs.chats.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Candidates are only private supergroups this account created, with a usable access hash.
            // Broadcast channels and groups created by others can never be a Nuvia storage group.
            if (chat is not Channel channel)
                continue;
            if (!channel.flags.HasFlag(Channel.Flags.megagroup))
                continue;
            if (!channel.flags.HasFlag(Channel.Flags.creator))
                continue;
            if (channel.access_hash == 0)
                continue;

            // The marker lives in the group's `about`, which only the full-channel call returns. Read it
            // and match the tag; any group that is not marked is left completely untouched.
            var about = await FetchChannelAboutAsync(client, channel, cancellationToken);
            if (!NuviaMarkers.AboutIndicatesNuviaGroup(about))
                continue;

            found.Add(new DiscoveredStorageGroup(channel.id, channel.access_hash, channel.title ?? string.Empty));
        }

        return found;
    }

    /// <summary>Read a channel's <c>about</c> text; returns null if the full-channel details can't be fetched.</summary>
    private static async Task<string?> FetchChannelAboutAsync(
        Client client, Channel channel, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var full = await client.Channels_GetFullChannel(new InputChannel(channel.id, channel.access_hash));
            return (full.full_chat as ChannelFull)?.about;
        }
        catch (RpcException)
        {
            // A group whose full details are refused cannot be matched; skip it rather than fail the
            // whole discovery pass.
            return null;
        }
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>
    /// Resolve the one legitimate target peer for an upload: the account's own Saved Messages, or the
    /// managed private group. Any other destination is impossible by construction.
    /// </summary>
    private static InputPeer ResolveUploadPeer(StorageDestination destination) => destination.Kind switch
    {
        StorageDestinationKind.ManagedGroup => destination.Group is { } g && g.LooksUsable()
            // InputPeerChannel also uses a positional constructor (channel_id, access_hash) in 4.4.8.
            ? new InputPeerChannel(g.ChannelId, g.AccessHash)
            : throw new StorageGroupUnavailableException(
                "The private storage group is not available, so the file was not uploaded."),
        _ => InputPeer.Self,
    };

    /// <summary>
    /// Telegram signals a lost/inaccessible channel with CHANNEL_INVALID / CHANNEL_PRIVATE and similar.
    /// Matching on the error text keeps this working across library updates that may renumber codes.
    /// </summary>
    private static bool IsChannelAccessLost(RpcException ex)
    {
        var text = ex.Message ?? string.Empty;
        return text.Contains("CHANNEL_INVALID", StringComparison.OrdinalIgnoreCase)
            || text.Contains("CHANNEL_PRIVATE", StringComparison.OrdinalIgnoreCase)
            || text.Contains("CHANNEL_ID_INVALID", StringComparison.OrdinalIgnoreCase)
            || text.Contains("PEER_ID_INVALID", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Build a <see cref="SavedMessageRef"/> from a real message, or return false when the message
    /// does not carry a resolvable document. Nothing is invented here — every field is read from
    /// the server's own object.
    /// </summary>
    private static bool TryExtractDocument(
        TL.MessageBase? message,
        long ownAccountId,
        out SavedMessageRef reference,
        out DateTime uploadedUtc)
    {
        reference = null!;
        uploadedUtc = default;

        if (message is not Message m)
            return false;
        if (m.media is not MessageMediaDocument media)
            return false;
        if (media.document is not Document doc)
            return false;

        var fileReference = doc.file_reference;
        if (fileReference is not { Length: > 0 })
            return false;

        var (peerKind, peerId) = DescribePeer(m.peer_id, ownAccountId);

        // Server-side timestamp of the message, so the index stores the server's value rather than
        // the local clock. WTelegramClient has already converted the wire int to a UTC DateTime.
        uploadedUtc = m.date.ToUniversalTime();

        reference = new SavedMessageRef(
            PeerKind: peerKind,
            PeerId: peerId,
            MessageId: m.id,
            DocumentId: doc.id,
            AccessHash: doc.access_hash,
            FileReference: fileReference,
            DcId: doc.dc_id,
            SizeBytes: doc.size,
            MimeType: doc.mime_type,
            UploadedUtc: uploadedUtc);
        return true;
    }

    /// <summary>Classify the peer so a record knows where it lives. Phase 4 only ever writes to self.</summary>
    private static (string Kind, long Id) DescribePeer(Peer? peer, long ownAccountId) => peer switch
    {
        PeerUser user when user.user_id == ownAccountId => (SavedMessageRef.PeerKindSelf, ownAccountId),
        PeerUser user => ("user", user.user_id),
        PeerChat chat => ("chat", chat.chat_id),
        PeerChannel channel => ("channel", channel.channel_id),
        _ => (SavedMessageRef.PeerKindSelf, ownAccountId),
    };

    /// <summary>Reconstruct the minimal <see cref="Document"/> needed to fetch the bytes again.</summary>
    private static Document ToDocument(SavedMessageRef reference) => new()
    {
        id = reference.DocumentId,
        access_hash = reference.AccessHash,
        file_reference = reference.FileReference,
        dc_id = reference.DcId,
        size = reference.SizeBytes,
        mime_type = reference.MimeType ?? "application/octet-stream",
        date = reference.UploadedUtc,
        attributes = Array.Empty<DocumentAttribute>(),
    };

    /// <summary>
    /// Telegram signals a stale reference with FILE_REFERENCE_EXPIRED / FILE_REFERENCE_* errors.
    /// Matching on the error text keeps this working across library updates that may renumber codes.
    /// </summary>
    private static bool IsFileReferenceExpired(RpcException ex)
        => ex.Message?.Contains("FILE_REFERENCE", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Small extension table — enough to file things sensibly, no external dependency.</summary>
    private static string GuessMimeType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".txt" or ".log" or ".md" => "text/plain",
        ".csv" => "text/csv",
        ".json" => "application/json",
        ".xml" => "application/xml",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        ".7z" => "application/x-7z-compressed",
        ".rar" => "application/vnd.rar",
        ".gz" or ".tgz" => "application/gzip",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".ppt" => "application/vnd.ms-powerpoint",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".m4a" => "audio/mp4",
        ".ogg" => "audio/ogg",
        ".mp4" => "video/mp4",
        ".mkv" => "video/x-matroska",
        ".mov" => "video/quicktime",
        ".exe" => "application/vnd.microsoft.portable-executable",
        ".msi" => "application/x-msi",
        ".iso" => "application/x-iso9660-image",
        _ => "application/octet-stream",
    };

    /// <summary>
    /// The display filename for an imported document. Nuvia's own uploads always carry a
    /// <see cref="DocumentAttributeFilename"/>, so that name is used verbatim. A file added outside Nuvia
    /// may have none (a pasted image, a voice note); for those a stable, honest placeholder is built from
    /// the document id plus an extension guessed from the mime type. This is a display label only — never a
    /// remote identifier, and nothing here is invented that a download would depend on.
    /// </summary>
    private static string ExtractDocumentFileName(TL.MessageBase mb)
    {
        if (mb is Message m && m.media is MessageMediaDocument { document: Document doc })
        {
            var named = doc.attributes?.OfType<DocumentAttributeFilename>().FirstOrDefault();
            if (named is not null && !string.IsNullOrWhiteSpace(named.file_name))
                return named.file_name.Trim();

            return "file_" + doc.id.ToString(CultureInfo.InvariantCulture) + GuessExtensionFromMime(doc.mime_type);
        }

        return "file";
    }

    /// <summary>Reverse of the common <see cref="GuessMimeType"/> entries — an extension for a placeholder name.</summary>
    private static string GuessExtensionFromMime(string? mimeType) => (mimeType ?? string.Empty).ToLowerInvariant() switch
    {
        "text/plain" => ".txt",
        "text/csv" => ".csv",
        "application/json" => ".json",
        "application/xml" => ".xml",
        "application/pdf" => ".pdf",
        "application/zip" => ".zip",
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "audio/mpeg" => ".mp3",
        "audio/mp4" => ".m4a",
        "audio/ogg" => ".ogg",
        "video/mp4" => ".mp4",
        "video/quicktime" => ".mov",
        _ => string.Empty,
    };

    /// <summary>
    /// Detach from the client's update streams. The client itself is owned and disposed by the auth
    /// service, not here, so this only unsubscribes. Idempotent and safe to call more than once.
    /// </summary>
    public void Dispose()
    {
        lock (_watchLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            DetachHandler();
        }
    }
}
