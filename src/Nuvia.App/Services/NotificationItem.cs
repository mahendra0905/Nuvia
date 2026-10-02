using System;

namespace Nuvia.App.Services;

/// <summary>What a notification is about. Decides the glyph and the default action wording.</summary>
public enum NotificationKind
{
    /// <summary>A newer app version is available.</summary>
    Update,

    /// <summary>A dev-pushed announcement from the website manifest.</summary>
    Announcement,
}

/// <summary>
/// One row in the notification center. A plain display model — no WPF types, no network — so it is unit
/// testable on any platform. The display-only helpers (<see cref="Glyph"/>, <see cref="RelativeTime"/>,
/// <see cref="HasAction"/>) are pure, which lets the XAML bind to them directly without value converters.
/// </summary>
public sealed class NotificationItem
{
    /// <summary>Stable id used for the "already seen" set. For updates it is "update:{version}".</summary>
    public string Id { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public NotificationKind Kind { get; init; }

    /// <summary>HTTPS URL opened in the default browser when the action button is clicked. Null = no action.</summary>
    public string? ActionUrl { get; init; }

    /// <summary>Label on the action button, e.g. "Download" or "Open". Null = no action button.</summary>
    public string? ActionLabel { get; init; }

    /// <summary>True when there is a URL and a label to show an action button for.</summary>
    public bool HasAction =>
        !string.IsNullOrWhiteSpace(ActionUrl) && !string.IsNullOrWhiteSpace(ActionLabel);

    /// <summary>Segoe Fluent / MDL2 glyph: Sync (E895) for updates, Info (E946) for announcements.</summary>
    public string Glyph => Kind == NotificationKind.Update ? "" : "";

    /// <summary>Friendly "just now / 5 minutes ago / 2 days ago / Oct 1, 2026" string.</summary>
    public string RelativeTime => FormatRelative(Timestamp, DateTimeOffset.Now);

    /// <summary>Pure relative-time formatter (exposed for testing). Clamps future timestamps to "just now".</summary>
    public static string FormatRelative(DateTimeOffset when, DateTimeOffset now)
    {
        var delta = now - when;
        if (delta < TimeSpan.Zero)
            delta = TimeSpan.Zero;

        if (delta.TotalMinutes < 1)
            return "just now";
        if (delta.TotalMinutes < 60)
        {
            var m = (int)delta.TotalMinutes;
            return m == 1 ? "1 minute ago" : $"{m} minutes ago";
        }
        if (delta.TotalHours < 24)
        {
            var h = (int)delta.TotalHours;
            return h == 1 ? "1 hour ago" : $"{h} hours ago";
        }
        if (delta.TotalDays < 7)
        {
            var d = (int)delta.TotalDays;
            return d == 1 ? "1 day ago" : $"{d} days ago";
        }

        return when.ToLocalTime().ToString("MMM d, yyyy");
    }
}
