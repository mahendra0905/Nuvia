using System;

namespace Nuvia.App.Services;

/// <summary>
/// Where an upload is sent. Nuvia supports exactly two destinations and nothing else:
/// the account's own Saved Messages (the default), or the single optional managed private
/// storage group the user chose to create. There is deliberately no arbitrary chat picking here.
/// </summary>
public enum StorageDestinationKind
{
    /// <summary>The account's own Saved Messages — always available, and the default.</summary>
    SavedMessages,

    /// <summary>The one managed private storage group, if the user created it.</summary>
    ManagedGroup,
}

/// <summary>
/// The identifiers of the one optional private storage group, scoped to the account that created it.
/// <para>
/// This is a <b>private supergroup (megagroup)</b> created with <c>channels.createChannel</c> and the
/// <c>megagroup</c> flag: the creator is its only member, it has no public username, and no invite link
/// is ever generated. It is chosen over a basic group (which cannot be created without adding other
/// users) and over a broadcast channel (which the product forbids). Every field comes from the real
/// server response to the create call — nothing is synthesised.
/// </para>
/// <para>
/// <see cref="AccessHash"/> is stored because reaching a channel again (to send to it, or to re-read a
/// message for a file-reference refresh) requires an <c>InputPeerChannel</c>/<c>InputChannel</c> built
/// from the channel id and this hash.
/// </para>
/// </summary>
public sealed record ManagedStorageGroup(
    long AccountId,
    long ChannelId,
    long AccessHash,
    string Title,
    DateTime CreatedUtc)
{
    public bool LooksUsable() => AccountId != 0 && ChannelId != 0 && AccessHash != 0;
}

/// <summary>
/// A resolved upload target: either Saved Messages, or the managed group (carrying the group it
/// resolved to). Built by the UI from the account's persisted preference and handed to the coordinator
/// for a single upload; it is never inferred from anything the server sends back.
/// </summary>
public sealed record StorageDestination(StorageDestinationKind Kind, ManagedStorageGroup? Group)
{
    /// <summary>The default destination. Used unless the user has explicitly chosen the group.</summary>
    public static StorageDestination SavedMessages { get; } =
        new(StorageDestinationKind.SavedMessages, null);

    /// <summary>The managed group as a destination. Requires a usable group.</summary>
    public static StorageDestination ForGroup(ManagedStorageGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!group.LooksUsable())
            throw new ArgumentException("The managed group is missing usable identifiers.", nameof(group));
        return new StorageDestination(StorageDestinationKind.ManagedGroup, group);
    }

    /// <summary>Short label for the status bar / file list. Never contains credentials.</summary>
    public string DisplayName => Kind == StorageDestinationKind.ManagedGroup
        ? (string.IsNullOrWhiteSpace(Group?.Title) ? "Private group" : Group!.Title)
        : "Saved Messages";
}
