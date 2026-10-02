using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nuvia.App.Services;

/// <summary>
/// Outcome of an update/announcement check. Tri-state: <see cref="Configured"/> distinguishes "no endpoint
/// set, silent no-op" from a real attempt, and <see cref="CheckedOk"/> distinguishes a reached source from
/// an offline/parse failure. The UI uses both to stay honest — it never shows a fake "up to date".
/// </summary>
public sealed class NotificationCheckResult
{
    public bool Configured { get; }
    public bool CheckedOk { get; }
    public UpdateInfo? Update { get; }
    public IReadOnlyList<NotificationItem> Announcements { get; }

    public NotificationCheckResult(
        bool configured, bool checkedOk, UpdateInfo? update, IReadOnlyList<NotificationItem>? announcements)
    {
        Configured = configured;
        CheckedOk = checkedOk;
        Update = update;
        Announcements = announcements ?? Array.Empty<NotificationItem>();
    }

    /// <summary>No source configured — a silent no-op (don't nag, don't fake, don't hit the network).</summary>
    public static NotificationCheckResult NotConfigured { get; } = new(false, false, null, null);

    /// <summary>Configured, but every source failed (offline/parse) — show an honest "couldn't check".</summary>
    public static NotificationCheckResult Failed { get; } = new(true, false, null, null);
}

/// <summary>The newest release the check resolved. Surfaced only when strictly newer than the running build.</summary>
public sealed class UpdateInfo
{
    public Version Version { get; init; } = new(0, 0);
    public string VersionDisplay { get; init; } = string.Empty;
    public string? DownloadUrl { get; init; }
    public string? Notes { get; init; }
}

/// <summary>Parsed GitHub "releases/latest" payload (pure parse output, no network).</summary>
public sealed class GitHubRelease
{
    public string? TagName { get; init; }
    public string? HtmlUrl { get; init; }
    public string? Body { get; init; }
    public bool Draft { get; init; }
    public bool Prerelease { get; init; }
}

/// <summary>Parsed website version manifest (pure parse output, no network).</summary>
public sealed class WebsiteManifest
{
    public string? LatestVersion { get; init; }
    public string? DownloadUrl { get; init; }
    public string? Notes { get; init; }
    public IReadOnlyList<WebsiteAnnouncement> Announcements { get; init; } = Array.Empty<WebsiteAnnouncement>();
}

/// <summary>One dev-pushed announcement from the website manifest.</summary>
public sealed class WebsiteAnnouncement
{
    public string? Id { get; init; }
    public string? Title { get; init; }
    public string? Message { get; init; }
    public string? Url { get; init; }
    public string? Severity { get; init; }
    public DateTimeOffset? Date { get; init; }
}

/// <summary>
/// Checks for a newer Nuvia version and dev announcements. Best-effort and side-effect-free toward the
/// user's data: one outbound HTTPS GET per source (GitHub Releases first, website manifest as the fallback
/// and the only announcements source), never a server, and nothing about the account, files or phone on the
/// wire. Any failure returns a result the UI renders honestly — it never throws to the caller and never
/// fakes "up to date".
/// </summary>
public sealed class UpdateService
{
    private static readonly HttpClient Http = CreateClient();

    private readonly SettingsStore _settings;

    public UpdateService(SettingsStore settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // GitHub rejects requests without a User-Agent. We send only the product name + version — no
        // account, file, phone or session information ever leaves the machine.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Nuvia/{AppInfo.VersionDisplay}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return http;
    }

    /// <summary>
    /// Resolve the newest version + announcements. Returns <see cref="NotificationCheckResult.NotConfigured"/>
    /// when no endpoint is set (silent no-op), <see cref="NotificationCheckResult.Failed"/> when configured
    /// but every source failed, otherwise a success result whose <see cref="NotificationCheckResult.Update"/>
    /// is non-null only when the latest version is strictly newer than the running build. On a successful
    /// reach it records the "last checked" timestamp; it never throws to the caller.
    /// </summary>
    public async Task<NotificationCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        if (!AppInfo.UpdateCheckConfigured)
            return NotificationCheckResult.NotConfigured;

        try
        {
            UpdateInfo? gitHubLatest = null;
            UpdateInfo? websiteLatest = null;
            var announcements = new List<NotificationItem>();
            var anyOk = false;

            if (!string.IsNullOrWhiteSpace(AppInfo.GitHubOwner) && !string.IsNullOrWhiteSpace(AppInfo.GitHubRepo))
            {
                var result = await TryGitHubAsync(cancellationToken).ConfigureAwait(false);
                if (result.Succeeded)
                {
                    anyOk = true;
                    gitHubLatest = result.Latest;
                }
            }

            if (!string.IsNullOrWhiteSpace(AppInfo.WebsiteManifestUrl))
            {
                var result = await TryWebsiteAsync(cancellationToken).ConfigureAwait(false);
                if (result.Succeeded)
                {
                    anyOk = true;
                    websiteLatest = result.Latest;
                    if (result.Announcements is not null)
                        announcements.AddRange(result.Announcements);
                }
            }

            if (!anyOk)
                return NotificationCheckResult.Failed;

            // GitHub is authoritative for the version; the website only decides the version when GitHub
            // could not be reached.
            var latest = gitHubLatest ?? websiteLatest;
            var update = latest is not null && IsNewer(AppInfo.CurrentVersion, latest.Version) ? latest : null;

            _settings.SetLastUpdateCheckUtc(DateTimeOffset.UtcNow);
            return new NotificationCheckResult(true, true, update, announcements);
        }
        catch (OperationCanceledException)
        {
            return NotificationCheckResult.Failed;
        }
        catch
        {
            return NotificationCheckResult.Failed;
        }
    }

    private readonly struct SourceResult
    {
        public SourceResult(bool succeeded, UpdateInfo? latest, IReadOnlyList<NotificationItem>? announcements)
        {
            Succeeded = succeeded;
            Latest = latest;
            Announcements = announcements;
        }

        public bool Succeeded { get; }
        public UpdateInfo? Latest { get; }
        public IReadOnlyList<NotificationItem>? Announcements { get; }

        public static readonly SourceResult Fail = new(false, null, null);
    }

    private async Task<SourceResult> TryGitHubAsync(CancellationToken ct)
    {
        try
        {
            var url = $"https://api.github.com/repos/{AppInfo.GitHubOwner}/{AppInfo.GitHubRepo}/releases/latest";
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return SourceResult.Fail;

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var release = ParseGitHubLatest(json);
            // Reached GitHub but nothing usable to offer (draft/prerelease/unparseable tag) still counts as a
            // successful check — just with no update.
            if (release is null || release.Draft || release.Prerelease)
                return new SourceResult(true, null, null);
            if (!TryParseVersion(release.TagName, out var version))
                return new SourceResult(true, null, null);

            var download = !string.IsNullOrWhiteSpace(AppInfo.DownloadPageUrl)
                ? AppInfo.DownloadPageUrl
                : release.HtmlUrl;

            var info = new UpdateInfo
            {
                Version = version,
                VersionDisplay = FormatVersion(version),
                DownloadUrl = NormalizeUrl(download),
                Notes = Clip(release.Body),
            };
            return new SourceResult(true, info, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // genuine user cancellation — abort the whole check
        }
        catch
        {
            return SourceResult.Fail; // timeout / network / unexpected → let the website fallback try
        }
    }

    private async Task<SourceResult> TryWebsiteAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await Http.GetAsync(AppInfo.WebsiteManifestUrl, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return SourceResult.Fail;

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var manifest = ParseWebsiteManifest(json);
            if (manifest is null)
                return SourceResult.Fail;

            UpdateInfo? info = null;
            if (TryParseVersion(manifest.LatestVersion, out var version))
            {
                var download = !string.IsNullOrWhiteSpace(AppInfo.DownloadPageUrl)
                    ? AppInfo.DownloadPageUrl
                    : manifest.DownloadUrl;
                info = new UpdateInfo
                {
                    Version = version,
                    VersionDisplay = FormatVersion(version),
                    DownloadUrl = NormalizeUrl(download),
                    Notes = Clip(manifest.Notes),
                };
            }

            var items = new List<NotificationItem>();
            foreach (var ann in manifest.Announcements)
            {
                if (string.IsNullOrWhiteSpace(ann.Title) && string.IsNullOrWhiteSpace(ann.Message))
                    continue;

                var actionUrl = NormalizeUrl(ann.Url);
                items.Add(new NotificationItem
                {
                    Id = !string.IsNullOrWhiteSpace(ann.Id)
                        ? ann.Id!
                        : "ann:" + StableId(ann.Title, ann.Message, ann.Date),
                    Title = string.IsNullOrWhiteSpace(ann.Title) ? "Announcement" : ann.Title!,
                    Message = Clip(ann.Message) ?? string.Empty,
                    Timestamp = ann.Date ?? DateTimeOffset.Now,
                    Kind = NotificationKind.Announcement,
                    ActionUrl = actionUrl,
                    ActionLabel = string.IsNullOrWhiteSpace(actionUrl) ? null : "Open",
                });
            }

            return new SourceResult(true, info, items);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return SourceResult.Fail;
        }
    }

    // --- Pure helpers (unit-testable, no network) --------------------------------------------------------

    /// <summary>Parse GitHub's "releases/latest" JSON into the fields we use. Null on malformed input.</summary>
    public static GitHubRelease? ParseGitHubLatest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            return new GitHubRelease
            {
                TagName = GetString(root, "tag_name"),
                HtmlUrl = GetString(root, "html_url"),
                Body = GetString(root, "body"),
                Draft = GetBool(root, "draft"),
                Prerelease = GetBool(root, "prerelease"),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parse the website manifest: <c>{ "latest": { "version", "downloadUrl", "notes" },
    /// "announcements": [ { "id", "title", "message", "url", "severity", "date" } ] }</c>. Null on malformed
    /// input; missing fields are tolerated.
    /// </summary>
    public static WebsiteManifest? ParseWebsiteManifest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            string? version = null, downloadUrl = null, notes = null;
            if (root.TryGetProperty("latest", out var latest) && latest.ValueKind == JsonValueKind.Object)
            {
                version = GetString(latest, "version");
                downloadUrl = GetString(latest, "downloadUrl");
                notes = GetString(latest, "notes");
            }

            var announcements = new List<WebsiteAnnouncement>();
            if (root.TryGetProperty("announcements", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object)
                        continue;
                    announcements.Add(new WebsiteAnnouncement
                    {
                        Id = GetString(el, "id"),
                        Title = GetString(el, "title"),
                        Message = GetString(el, "message"),
                        Url = GetString(el, "url"),
                        Severity = GetString(el, "severity"),
                        Date = GetDate(el, "date"),
                    });
                }
            }

            return new WebsiteManifest
            {
                LatestVersion = version,
                DownloadUrl = downloadUrl,
                Notes = notes,
                Announcements = announcements,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Parse a release tag like "v1.2.0", "1.2.0-beta", "1.2" into a <see cref="Version"/>.</summary>
    public static bool TryParseVersion(string? raw, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var s = raw.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V'))
            s = s.Substring(1);

        var cut = s.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0)
            s = s.Substring(0, cut);
        if (s.Length == 0)
            return false;
        if (!s.Contains('.'))
            s += ".0";

        if (Version.TryParse(s, out var parsed) && parsed is not null)
        {
            version = parsed;
            return true;
        }
        return false;
    }

    /// <summary>True when <paramref name="candidate"/> is strictly newer than <paramref name="current"/>.</summary>
    public static bool IsNewer(Version current, Version candidate) => Normalize(candidate) > Normalize(current);

    private static Version Normalize(Version v)
        => new(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build), Math.Max(0, v.Revision));

    private static string FormatVersion(Version v)
    {
        if (v.Revision > 0)
            return v.ToString(4);
        if (v.Build > 0)
            return v.ToString(3);
        return $"{Math.Max(0, v.Major)}.{Math.Max(0, v.Minor)}.{Math.Max(0, v.Build)}";
    }

    private static string? Clip(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var t = text.Trim();
        const int max = 400;
        return t.Length <= max ? t : t.Substring(0, max).TrimEnd() + "…";
    }

    private static string? NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.AbsoluteUri
            : null;
    }

    /// <summary>Deterministic (process-stable) id for an announcement that didn't ship its own id.</summary>
    private static string StableId(string? title, string? message, DateTimeOffset? date)
    {
        var basis = $"{title}|{message}|{date?.UtcDateTime:O}";
        unchecked
        {
            const uint fnvPrime = 16777619;
            var hash = 2166136261u;
            foreach (var ch in basis)
            {
                hash ^= ch;
                hash *= fnvPrime;
            }
            return hash.ToString("X8", CultureInfo.InvariantCulture);
        }
    }

    private static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool GetBool(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? GetDate(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
            return null;
        return DateTimeOffset.TryParse(
            v.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var dt)
            ? dt
            : null;
    }
}
