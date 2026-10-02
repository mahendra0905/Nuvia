using System;

namespace Nuvia.App.Services;

/// <summary>
/// Documented Telegram upload ceilings for a <b>user account</b> (MTProto), used for an honest
/// pre-flight check before a byte ever leaves the machine.
/// <para>
/// <b>Verified limits (source + date):</b>
/// free user accounts may upload up to <b>2 GB</b> per file; Telegram Premium accounts up to
/// <b>4 GB</b> per file. Verified 2026-09-25 against Telegram's own announcement
/// (<c>https://telegram.org/blog/700-million-and-premium</c>) and the MTProto file API
/// (<c>https://core.telegram.org/api/files</c>, which describes the part-count mechanism —
/// <c>upload_max_fileparts_default</c> / <c>upload_max_fileparts_premium</c> — that enforces it
/// server-side).
/// </para>
/// <para>
/// <b>These are NOT the Bot API limits.</b> The Bot API caps uploads far lower (tens of MB); Nuvia
/// signs in as a user, so those figures must never be applied here.
/// </para>
/// <para>
/// <b>The server is the final authority.</b> Nuvia cannot reliably determine from the client whether
/// the signed-in account is Premium, and the real ceiling is server configuration that can change.
/// So this class only rejects a file up front when it exceeds the <i>largest</i> ceiling any account
/// could have (the Premium figure) — above that, no user account can store it regardless of tier.
/// Anything at or below that is sent, and a genuine over-limit is reported honestly from the actual
/// server response (<c>FILE_PARTS_INVALID</c> / <c>FILE_TOO_BIG</c>), classified as
/// <see cref="TransferErrorClass.FileTooLarge"/>.
/// </para>
/// </summary>
public static class TelegramFileLimits
{
    /// <summary>Free user-account ceiling: 2 GiB. Documented, server-enforced, may change.</summary>
    public const long FreeAccountMaxBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Premium user-account ceiling: 4 GiB. The largest any user account can upload.</summary>
    public const long PremiumAccountMaxBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>UTC date the ceilings above were last verified against Telegram's own sources.</summary>
    public const string VerifiedOnUtc = "2026-09-25";

    /// <summary>
    /// True only when the file is larger than the maximum <i>any</i> user account can upload, so it can
    /// be refused before the transfer starts without guessing the account's tier.
    /// </summary>
    public static bool ExceedsEveryAccountCeiling(long sizeBytes)
        => sizeBytes > PremiumAccountMaxBytes;
}

/// <summary>
/// Thrown before a transfer starts when the local file is larger than the largest documented
/// user-account upload ceiling (see <see cref="TelegramFileLimits"/>). Carries no file name or path,
/// so it is safe to surface and log. Classified as <see cref="TransferErrorClass.FileTooLarge"/>.
/// </summary>
public sealed class FileExceedsAccountLimitException : Exception
{
    public FileExceedsAccountLimitException(string message) : base(message)
    {
    }
}
