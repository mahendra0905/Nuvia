using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nuvia.App.Services;

/// <summary>
/// Deterministic, non-networking authentication stand-in for development and testing only.
/// It is NEVER selected silently: the app must explicitly opt in (Debug build or NUVIA_DEMO=1).
///
/// Demo credentials:
///   Phone: +999 999 999 999  — no 2FA (any 4–8 digit code works)
///   Phone: +888 888 888 888  — requires 2FA (password: "demo")
///   Any other phone — rejected with an error.
/// </summary>
public sealed class DemoAuthSimulator : IAuthService
{
    private enum FlowState { Idle, CodeSent, CodeVerified, Completed }
    private FlowState _state = FlowState.Idle;

    public bool IsDemoMode => true;

    /// <summary>Demo never persists a session, so there is never one to resume.</summary>
    public bool HasStoredSession => false;

    public bool RequiresPassword { get; private set; }
    public bool IsCompleted => _state == FlowState.Completed;
    public string? UserDisplayName { get; private set; }

    /// <summary>Demo never persists a session, so there is nothing to resume.</summary>
    public Task<bool> TryResumeSessionAsync(CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<string?> RequestCodeAsync(string phone, CancellationToken ct = default)
    {
        Reset();
        var normalized = phone.Replace(" ", "").Replace("-", "").Replace("+", "");
        if (normalized == "999999999999" || normalized == "888888888888")
        {
            _state = FlowState.CodeSent;
            RequiresPassword = normalized == "888888888888";
            return Task.FromResult<string?>(null);
        }
        return Task.FromResult<string?>("Demo only accepts +999 999 999 999 or +888 888 888 888.");
    }

    public Task<string?> VerifyCodeAsync(string code, CancellationToken ct = default)
    {
        if (_state != FlowState.CodeSent)
            return Task.FromResult<string?>("No code was requested. Please start again.");

        var trimmed = code.Trim();
        if (trimmed.Length < 4 || trimmed.Length > 8 || !trimmed.All(char.IsDigit))
            return Task.FromResult<string?>("Invalid code. Enter the code you received.");

        if (RequiresPassword)
        {
            _state = FlowState.CodeVerified;
            return Task.FromResult<string?>(null);
        }

        _state = FlowState.Completed;
        UserDisplayName = "Demo User";
        return Task.FromResult<string?>(null);
    }

    public Task<string?> SubmitPasswordAsync(string password, CancellationToken ct = default)
    {
        if (_state != FlowState.CodeVerified)
            return Task.FromResult<string?>("No password step is active.");

        if (password != "demo")
            return Task.FromResult<string?>("Incorrect password. Try again.");

        _state = FlowState.Completed;
        UserDisplayName = "Demo User (2FA)";
        return Task.FromResult<string?>(null);
    }

    // Resend and QR are real-network features with no honest demo equivalent, so the stand-in reports
    // them as unavailable — the login window simply hides the resend link and the QR option in demo mode.
    public string? CurrentCodeMedium => _state == FlowState.CodeSent ? "the demo channel" : null;
    public string? NextCodeMedium => null;
    public bool CanResendCode => false;
    public int ResendCooldownSeconds => 0;

    public Task<string?> ResendCodeAsync(CancellationToken ct = default)
        => Task.FromResult<string?>("Resend is not available in demo mode.");

    public bool SupportsQrLogin => false;

    // Empty accessors satisfy the interface without an unused backing field (never raised in demo).
    public event Action<int>? QrPasswordRequired { add { } remove { } }

    public Task<string?> LoginWithQrAsync(Action<string> onQrUrl, CancellationToken ct = default)
        => Task.FromResult<string?>("QR sign-in is not available in demo mode.");

    public void ProvideQrPassword(string password) { }

    public void Reset()
    {
        _state = FlowState.Idle;
        RequiresPassword = false;
        UserDisplayName = null;
    }

    /// <summary>
    /// Demo never networks and never persists a session, so there is nothing to revoke server-side and
    /// no session file to remove. It simply clears its in-memory state and reports a local-only logout.
    /// </summary>
    public Task<LogoutOutcome> LogOutAsync(CancellationToken ct = default)
    {
        Reset();
        return Task.FromResult(LogoutOutcome.LocalOnlyServerUnconfirmed);
    }

    public void Dispose() { }
}
