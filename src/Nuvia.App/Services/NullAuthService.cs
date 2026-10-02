using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nuvia.App.Services;

/// <summary>
/// Placeholder used only when Nuvia cannot start (e.g. missing developer configuration).
/// Every operation refuses: the UI shows a configuration error and no sign-in can occur,
/// so a misconfigured install can never present a working login form.
/// </summary>
public sealed class NullAuthService : IAuthService
{
    private const string Message =
        "Nuvia is not configured. Sign-in is unavailable until the configuration is fixed.";

    public bool IsDemoMode => false;

    /// <summary>An unconfigured app has no session to resume.</summary>
    public bool HasStoredSession => false;

    public bool RequiresPassword => false;
    public bool IsCompleted => false;
    public string? UserDisplayName => null;

    public Task<bool> TryResumeSessionAsync(CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<string?> RequestCodeAsync(string phone, CancellationToken ct = default)
        => Task.FromResult<string?>(Message);

    public Task<string?> VerifyCodeAsync(string code, CancellationToken ct = default)
        => Task.FromResult<string?>(Message);

    public Task<string?> SubmitPasswordAsync(string password, CancellationToken ct = default)
        => Task.FromResult<string?>(Message);

    // Nothing is configured, so there is nothing to resend and no way to sign in by QR.
    public string? CurrentCodeMedium => null;
    public string? NextCodeMedium => null;
    public bool CanResendCode => false;
    public int ResendCooldownSeconds => 0;

    public Task<string?> ResendCodeAsync(CancellationToken ct = default)
        => Task.FromResult<string?>(Message);

    public bool SupportsQrLogin => false;

    // Empty accessors satisfy the interface without an unused backing field (never raised here).
    public event Action<int>? QrPasswordRequired { add { } remove { } }

    public Task<string?> LoginWithQrAsync(Action<string> onQrUrl, CancellationToken ct = default)
        => Task.FromResult<string?>(Message);

    public void ProvideQrPassword(string password) { }

    public void Reset() { }

    /// <summary>Nothing was ever signed in, so there is nothing to revoke or remove.</summary>
    public Task<LogoutOutcome> LogOutAsync(CancellationToken ct = default)
        => Task.FromResult(LogoutOutcome.LocalOnlyServerUnconfirmed);

    public void Dispose() { }
}
