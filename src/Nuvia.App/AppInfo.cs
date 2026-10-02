using System;
using System.Reflection;

namespace Nuvia.App;

/// <summary>
/// Single source of truth for the app's version and (optional) update endpoints.
/// <para>
/// The version is read from the running assembly, so <c>&lt;Version&gt;</c> in the csproj is the only place
/// it is set — the About screens and the update comparison all read it from here instead of a hardcoded
/// string.
/// </para>
/// <para>
/// The four endpoint fields below are deliberately left blank in the committed source. The update check is
/// a <b>silent no-op</b> until a build fills them in (see <see cref="UpdateCheckConfigured"/>) — it never
/// invents a release or shows a fake "up to date". The dev sets the real GitHub owner/repo and/or the
/// website manifest URL at build time.
/// </para>
/// </summary>
public static class AppInfo
{
    /// <summary>Product name — matches the window title and the installer. English, never localized.</summary>
    public const string ProductName = "Nuvia";

    // --- Update endpoints (FILL IN at build time) --------------------------------------------------------
    // Leave blank to disable the update check entirely. Static readonly (not const) so filling these in
    // never produces "unreachable code" warnings in the guards that read them.

    /// <summary>GitHub account that owns the releases repo, e.g. "octocat". Blank = skip the GitHub check.</summary>
    public static readonly string GitHubOwner = "";

    /// <summary>GitHub repository name that publishes releases. Blank = skip the GitHub check.</summary>
    public static readonly string GitHubRepo = "";

    /// <summary>
    /// HTTPS URL of the website version manifest (JSON). Serves as the GitHub fallback for the update check
    /// and as the only source of dev announcements. Blank = skip the website check.
    /// </summary>
    public static readonly string WebsiteManifestUrl = "";

    /// <summary>
    /// HTTPS download/landing page opened in the browser when the user clicks "Download". When blank, the
    /// per-source URL (GitHub release page or the manifest's downloadUrl) is used instead.
    /// </summary>
    public static readonly string DownloadPageUrl = "";

    // --- Telegram-pushed announcements + support (FILL IN at build time) ---------------------------------
    // Both are blank in the committed source so the mechanism ships dormant: no channel is read and no
    // "Contact support" button is shown until a build fills them in. They are handles only — never a secret.

    /// <summary>
    /// Public @username (with or without the leading @) of the app-owned <b>broadcast announcements
    /// channel</b>. Nuvia only ever <i>reads</i> this one public channel over the already-signed-in account
    /// to surface dev-pushed notices in the in-app notification center — it never joins it, posts to it, or
    /// touches any personal chat. Blank = the announcement feed is disabled (silent no-op).
    /// </summary>
    public static readonly string AnnouncementsChannel = "NuviaApp";

    /// <summary>
    /// Public @username (with or without the leading @) of the support bot/account that the Settings →
    /// About "Contact support" button opens in Telegram. Blank = the button is hidden.
    /// </summary>
    public static readonly string SupportContact = "NuviaAppSupportBot";

    /// <summary>The running assembly version (always 4-part; defaults to 1.0.0 if somehow unavailable).</summary>
    public static Version CurrentVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    /// <summary>Human "X.Y.Z" version shown in About / Settings.</summary>
    public static string VersionDisplay { get; } = CurrentVersion.ToString(3);

    /// <summary>
    /// True when at least one update source is configured. When false the whole check short-circuits to a
    /// silent no-op so an unconfigured build never nags, never fakes a result, and never hits the network.
    /// </summary>
    public static bool UpdateCheckConfigured =>
        (!string.IsNullOrWhiteSpace(GitHubOwner) && !string.IsNullOrWhiteSpace(GitHubRepo))
        || !string.IsNullOrWhiteSpace(WebsiteManifestUrl);

    /// <summary>True once an announcements channel handle is set. Blank keeps the feed a silent no-op.</summary>
    public static bool AnnouncementsConfigured => !string.IsNullOrWhiteSpace(AnnouncementsChannel);

    /// <summary>The announcements channel username with any leading '@' stripped (empty when unconfigured).</summary>
    public static string AnnouncementsChannelHandle => AnnouncementsChannel.Trim().TrimStart('@');

    /// <summary>True once a support contact handle is set. Blank hides the "Contact support" button.</summary>
    public static bool SupportConfigured => !string.IsNullOrWhiteSpace(SupportContact);

    /// <summary>Deep link that opens the support chat in Telegram, or null when no support handle is set.</summary>
    public static string? SupportUrl =>
        SupportConfigured ? "https://t.me/" + SupportContact.Trim().TrimStart('@') : null;
}
