using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nuvia.App.Services;

/// <summary>
/// The result of a logout, distinguishing what actually happened on the server from what happened
/// locally. This matters when the machine is offline: the local session is always removed as the user
/// asked, but the server-side session can only be revoked when Telegram is reachable.
/// </summary>
public enum LogoutOutcome
{
    /// <summary>
    /// Telegram was reached and the server-side session was revoked, and the local session was removed.
    /// </summary>
    ServerConfirmed,

    /// <summary>
    /// The local session was removed as requested, but the server could not be reached to revoke the
    /// session (e.g. offline). The server-side session may therefore still be active until it is revoked
    /// from another device or expires. Nothing remote — messages, files, groups, the account — is deleted.
    /// </summary>
    LocalOnlyServerUnconfirmed,
}

/// <summary>
/// Abstraction for the Telegram authentication flow.
/// The production implementation uses WTelegramClient.
/// A demo simulator exists for development/testing only.
/// </summary>
public interface IAuthService
{
    /// <summary>True when this service is a non-networking development stand-in.</summary>
    bool IsDemoMode { get; }

    /// <summary>Request a verification code for the given phone number. Returns null on success, or an error message.</summary>
    Task<string?> RequestCodeAsync(string phone, CancellationToken ct = default);

    /// <summary>Verify the submitted code. Returns null on success (move to next step), or an error message.</summary>
    Task<string?> VerifyCodeAsync(string code, CancellationToken ct = default);

    /// <summary>Submit 2FA password. Returns null on success, or an error message.</summary>
    Task<string?> SubmitPasswordAsync(string password, CancellationToken ct = default);

    // ----- Resend / SMS (or call) fallback for the login code -----

    /// <summary>
    /// Where the current login code was delivered, in plain words (e.g. "the Telegram app", "SMS"),
    /// or null when no code has been sent yet. Used only to tell the user where to look — never a secret.
    /// </summary>
    string? CurrentCodeMedium { get; }

    /// <summary>
    /// The delivery medium a resend would use next (e.g. "SMS", "a phone call"), or null when Telegram
    /// has not offered an alternative. Drives the "Resend via …" label.
    /// </summary>
    string? NextCodeMedium { get; }

    /// <summary>
    /// True when a code has been sent, is not yet verified, and Telegram offers another delivery medium,
    /// so a resend is meaningful. The UI shows the resend option only when this is true.
    /// </summary>
    bool CanResendCode { get; }

    /// <summary>
    /// Seconds the user must still wait before a resend is allowed (0 when ready now). Poll this to drive
    /// a countdown — Telegram silently ignores a resend requested before this elapses.
    /// </summary>
    int ResendCooldownSeconds { get; }

    /// <summary>
    /// Resend the login code via <see cref="NextCodeMedium"/>. Returns null once a fresh code is actually
    /// on its way, or a human-readable status/error otherwise (e.g. still within the cooldown). Never
    /// reports success unless Telegram really sent a new code.
    /// </summary>
    Task<string?> ResendCodeAsync(CancellationToken ct = default);

    // ----- QR-code login -----

    /// <summary>True when this service can sign in by scanning a QR code.</summary>
    bool SupportsQrLogin { get; }

    /// <summary>
    /// Sign in by QR code. <paramref name="onQrUrl"/> is invoked (possibly several times, on a background
    /// thread) with a <c>tg://login?token=…</c> URL to render as a QR image for the user to scan in the
    /// Telegram app. Returns null on success, or an error message. If the account has two-factor auth,
    /// <see cref="QrPasswordRequired"/> is raised and the caller must supply the password via
    /// <see cref="ProvideQrPassword"/>.
    /// </summary>
    Task<string?> LoginWithQrAsync(Action<string> onQrUrl, CancellationToken ct = default);

    /// <summary>
    /// Raised (on a background thread) during QR sign-in when the account's 2FA password is needed. The
    /// argument is the 1-based attempt number: a value greater than 1 means the previous password was
    /// wrong. Handlers must marshal to the UI thread themselves.
    /// </summary>
    event Action<int>? QrPasswordRequired;

    /// <summary>Supply the 2FA password requested by <see cref="QrPasswordRequired"/> during QR sign-in.</summary>
    void ProvideQrPassword(string password);


    /// <summary>Whether the current flow requires a 2FA password step to proceed.</summary>
    bool RequiresPassword { get; }

    /// <summary>Whether authentication has completed successfully (client.User is non-null).</summary>
    bool IsCompleted { get; }

    /// <summary>The signed-in user's display name (available after IsCompleted).</summary>
    string? UserDisplayName { get; }

    /// <summary>
    /// Check for a valid saved session without requiring user interaction.
    /// Runs off the UI thread; returns true if already logged in.
    /// </summary>
    Task<bool> TryResumeSessionAsync(CancellationToken ct = default);

    /// <summary>
    /// True when a resumable session file exists on disk, determined synchronously with no network
    /// call. Startup uses this to decide whether to show a lightweight "Restoring…" splash and try a
    /// silent resume (returning user) or go straight to the full sign-in window (first run / logged out).
    /// </summary>
    bool HasStoredSession { get; }

    /// <summary>Reset all authentication state — called when leaving the flow or on cancellation.</summary>
    void Reset();

    /// <summary>
    /// Log out at the user's explicit request: revoke the server-side session when Telegram is reachable,
    /// dispose the client, clear sensitive in-memory state, and remove the local session file.
    /// <para>
    /// This never deletes remote messages, files, groups or the Telegram account, and it never touches
    /// the local file index. The returned <see cref="LogoutOutcome"/> tells the caller whether the
    /// server-side session was actually revoked or only the local session was removed (offline).
    /// </para>
    /// </summary>
    Task<LogoutOutcome> LogOutAsync(CancellationToken ct = default);

    /// <summary>Dispose any resources (e.g., the Telegram client).</summary>
    void Dispose();
}
