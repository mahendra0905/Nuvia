using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TL;
using WTelegram;

namespace Nuvia.App.Services;

/// <summary>
/// Reads the app-owned <b>broadcast announcements channel</b> and turns its recent posts into
/// <see cref="NotificationItem"/>s for the in-app notification center.
/// <para>
/// This is a <i>no-join background read</i>: it resolves one <b>public</b> channel by its username and pages
/// its history over the already-signed-in account. It never joins the channel (the user's dialog list is
/// untouched), never posts, and never reads any personal chat — so it stays within "no chat browsing". The
/// dev pushes a notice simply by posting to that channel (from Telegram, or later a bot/dashboard); it
/// arrives here on the next poll. Blank <see cref="AppInfo.AnnouncementsChannel"/> ⇒ a silent no-op.
/// </para>
/// <para>
/// Best-effort throughout: any network/parse failure yields an empty list rather than throwing, so a flaky
/// read can never crash the window. The message→notification mapping is a pure static helper
/// (<see cref="ToNotification"/>) so it is unit-testable on any platform without a live client.
/// </para>
/// </summary>
public sealed class AnnouncementService
{
    /// <summary>How many of the newest channel posts to surface. Caps work and keeps the center readable.</summary>
    public const int MaxItems = 15;

    private readonly TelegramAuthService _auth;

    // Resolved once then reused so each poll is a single getHistory, not resolve+getHistory. Cleared on any
    // failure so a stale/expired access hash is re-resolved on the next poll.
    private InputPeerChannel? _cachedPeer;

    public AnnouncementService(TelegramAuthService auth)
    {
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
    }

    /// <summary>True when a channel handle is configured. False keeps the whole feed dormant.</summary>
    public bool IsConfigured => AppInfo.AnnouncementsConfigured;

    /// <summary>
    /// Fetches the newest posts (up to <see cref="MaxItems"/>) and maps each text post to a notification,
    /// newest first. Returns an empty list — never throws, never a partial fake — when the feed is not
    /// configured, no account is signed in, the channel can't be resolved, or anything goes wrong.
    /// </summary>
    public async Task<IReadOnlyList<NotificationItem>> FetchAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            return Array.Empty<NotificationItem>();

        var client = _auth.SignedInClient;
        if (client is null)
            return Array.Empty<NotificationItem>();

        try
        {
            var peer = _cachedPeer ?? await ResolveAsync(client).ConfigureAwait(false);
            if (peer is null)
                return Array.Empty<NotificationItem>();
            _cachedPeer = peer;

            cancellationToken.ThrowIfCancellationRequested();

            // Read-only: newest page of history, no min_id watermark. Dedupe/"already seen" is handled by the
            // caller via the persisted seen-id set, so re-reading the same posts each poll is harmless.
            var page = await client.Messages_GetHistory(peer, limit: MaxItems).ConfigureAwait(false);
            var batch = page?.Messages;
            if (batch is null || batch.Length == 0)
                return Array.Empty<NotificationItem>();

            var items = new List<NotificationItem>(batch.Length);
            foreach (var mb in batch)
            {
                if (mb is not Message m)
                    continue;

                var item = ToNotification(m.ID, m.message, ToTimestamp(m.date));
                if (item is not null)
                    items.Add(item);
            }

            return items;
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<NotificationItem>();
        }
        catch
        {
            // Any RPC/parse failure: drop the cached peer so a changed access hash is re-resolved next time.
            _cachedPeer = null;
            return Array.Empty<NotificationItem>();
        }
    }

    private static async Task<InputPeerChannel?> ResolveAsync(Client client)
    {
        var resolved = await client.Contacts_ResolveUsername(AppInfo.AnnouncementsChannelHandle).ConfigureAwait(false);
        var channel = resolved?.Channel;
        if (channel is null || channel.access_hash == 0)
            return null;
        return new InputPeerChannel(channel.id, channel.access_hash);
    }

    private static DateTimeOffset ToTimestamp(DateTime date)
    {
        if (date == default)
            return DateTimeOffset.Now;
        // Telegram timestamps are UTC; force the kind so DateTimeOffset never throws on an Unspecified value.
        var utc = date.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(date, DateTimeKind.Utc) : date.ToUniversalTime();
        return new DateTimeOffset(utc);
    }

    private static readonly Regex UrlPattern =
        new(@"https?://[^\s<>""]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>First http/https URL in the text with trailing sentence punctuation trimmed, or null.</summary>
    public static string? ExtractFirstUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var match = UrlPattern.Match(text);
        if (!match.Success)
            return null;
        return match.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '"', '\'');
    }

    /// <summary>
    /// Pure mapping of one channel post to a notification row (exposed for testing). The first non-blank line
    /// becomes the title; the rest becomes the body; the first URL (if any) becomes an "Open" action. A post
    /// with no text at all (e.g. a bare media upload) maps to null and is skipped.
    /// </summary>
    public static NotificationItem? ToNotification(int messageId, string? text, DateTimeOffset timestamp)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var title = string.Empty;
        var bodyStart = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]))
            {
                title = lines[i].Trim();
                bodyStart = i + 1;
                break;
            }
        }
        if (title.Length == 0)
            return null;

        var body = string.Join("\n", lines[bodyStart..]).Trim();

        var url = ExtractFirstUrl(text);

        return new NotificationItem
        {
            Id = "chan:" + messageId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Title = Clamp(title, 140),
            Message = Clamp(body, 400),
            Timestamp = timestamp,
            Kind = NotificationKind.Announcement,
            ActionUrl = url,
            ActionLabel = url is null ? null : "Open",
        };
    }

    private static string Clamp(string value, int max) =>
        value.Length <= max ? value : value[..max].TrimEnd() + "…";
}
