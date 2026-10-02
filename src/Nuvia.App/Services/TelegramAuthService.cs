using System;
using System.Threading;
using System.Threading.Tasks;
using TL;
using WTelegram;

namespace Nuvia.App.Services;

/// <summary>
/// Production Telegram authentication using WTelegramClient.
/// Uses the step-by-step client.Login() pattern suitable for GUI applications.
/// All credentials/config are injected via NuviaConfig — never hardcoded or prompted from user.
/// </summary>
public sealed class TelegramAuthService : IAuthService
{
    private readonly NuviaConfig _config;
    private Client? _client;
    private string? _loginStepValue; // transient: phone, then code, then password — cleared immediately after each use
    private bool _disposed;

    // Login state
    private bool _codeRequested;
    private bool _codeVerified;
    private bool _requiresPassword;
    private string? _userDisplayName;

    // Resend / delivery tracking. Populated from the Auth_SentCode the library raises through OnOther,
    // so the UI can tell the user where the code went and whether a resend (via another medium) is
    // possible — without ever inventing a "code sent" state that did not happen.
    private Auth_SentCode? _lastSentCode;
    private int _sentCodeCount;            // increments each time Telegram reports a (re)sent code
    private DateTime _resendAvailableUtc;  // earliest UTC time a resend will actually be honoured

    // QR-login 2FA password bridge. During QR sign-in the library asks for the 2FA password directly
    // through ConfigCallback (there is no Login() wrapper), so ConfigCallback must block and return the
    // password the user types. This is safe: the library calls Config on a threadpool thread (ConfigAsync
    // wraps it in Task.Run), never the UI thread.
    private volatile bool _qrLoginActive;
    private volatile TaskCompletionSource<string>? _qrPasswordTcs;
    private int _qrPasswordAsk;            // 1-based count of password prompts within one QR attempt

    /// <inheritdoc />
    public event Action<int>? QrPasswordRequired;

    /// <summary>
    /// Silence WTelegramClient's diagnostic logging. The library otherwise writes
    /// connection/session detail to the console, which must not reach logs or a console.
    /// </summary>
    static TelegramAuthService()
    {
        WTelegram.Helpers.Log = (_, _) => { };
    }

    public bool IsDemoMode => false;

    /// <summary>
    /// A resumable session exists when the fixed session file is present and non-empty. This is a
    /// pure file-system check (no network, no client), safe to call on the UI thread at startup.
    /// </summary>
    public bool HasStoredSession => HasUsableSessionFile();

    public bool RequiresPassword => _codeVerified && _requiresPassword;
    public bool IsCompleted => _client?.User != null;
    public string? UserDisplayName => _userDisplayName;

    /// <summary>
    /// The signed-in client, or null when no account is signed in.
    /// Only ever handed out once authentication has completed, so the storage layer can never act
    /// on a half-authenticated session.
    /// </summary>
    internal Client? SignedInClient => IsCompleted ? _client : null;

    /// <summary>
    /// Stable Telegram account id, used to scope every local record.
    /// Null until signed in — callers must not fall back to a placeholder id.
    /// </summary>
    public long? AccountId => IsCompleted ? _client!.User.id : null;

    public TelegramAuthService(NuviaConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Initialize the WTelegramClient with config callback. Must be called before any login attempt.
    /// </summary>
    private void EnsureClient()
    {
        if (_client != null) return;

        // Suppress per-client writers as well as the static logger.
        WTelegram.Helpers.Log = (_, _) => { };
        _client = new Client(ConfigCallback);

        // Watch the "other" object stream for Auth_SentCode. The library raises it through OnOther
        // (it is an IObject, not an UpdatesBase) every time a code is sent or resent, carrying the
        // delivery medium, the next medium and the resend timeout. This is the honest source for the
        // resend UI — nothing here is a secret (the phone_code_hash is never read or logged).
        _client.OnOther += OnClientOther;

        // Configure — rather than merely assume — the library's flood-wait behaviour. WTelegramClient
        // waits out a short FLOOD_WAIT itself and only surfaces one longer than this threshold as an
        // RpcException(420). Pinning it to the coordinator's known value keeps that contract explicit,
        // so a future library default change cannot silently create nested waits or a retry storm.
        _client.FloodRetryThreshold = FileTransferCoordinator.LibraryFloodRetryThresholdSeconds;

        _disposed = false;
    }

    /// <summary>
    /// WTelegramClient configuration callback.
    /// Called by the library whenever it needs a configuration value.
    /// Keys: api_id, api_hash, phone_number, session_pathname, verification_code, password, first_name, last_name, email, email_verification_code
    /// </summary>
    private string? ConfigCallback(string what)
    {
        switch (what)
        {
            case "api_id":
                return _config.ApiId.ToString();
            case "api_hash":
                return _config.ApiHash;
            case "session_pathname":
                return _config.GetSessionPath();
            case "user_id":
                // CRITICAL for persistent login. When resuming a stored session, LoginUserIfNeeded
                // fetches the signed-in account and validates it with an expression of the form:
                //     (TryParse(Config("user_id")) && (id == -1 || self.id == id)) ||
                //     self.phone == digits(Config("phone_number"))
                // "-1" means "accept whichever account this session file belongs to", which makes the
                // first branch true and short-circuits the ||, so the library NEVER evaluates the
                // phone_number branch. Returning null here instead let it fall through to
                // Config("phone_number") — which we also return null for — throwing
                // "You must provide a config value for phone_number" and bouncing a perfectly good
                // session back to the phone screen on every reopen. Each Nuvia session file holds
                // exactly one account, so "match any logged-in user" is exactly the right check.
                return "-1";
            case "phone_number":
                // Returned only when library needs phone — we handle phone via Login()
                return null;
            case "password":
                // During QR sign-in the library asks us for the 2FA password HERE (there is no Login()
                // wrapper to intercept it), so we must block and return the password the user typed. During
                // phone sign-in the Login() config wrapper supplies it instead, so we must stay out of the
                // way and return null — returning a value here would short-circuit the phone 2FA step.
                return _qrLoginActive ? WaitForQrPassword() : null;
            case "verification_code":
            case "first_name":
            case "last_name":
            case "email":
            case "email_verification_code":
                // These are provided interactively via Login() return values
                return null;
            default:
                // Let WTelegramClient decide defaults for anything else
                return null;
        }
    }

    /// <summary>
    /// Try to resume an existing valid session. Runs entirely off the UI thread.
    /// A revoked or expired session is discarded so the next attempt starts a fresh login.
    /// </summary>
    public async Task<bool> TryResumeSessionAsync(CancellationToken ct = default)
    {
        // Unconditional start line so EVERY reopen leaves a trace — proves this build's resume path ran.
        LogResumeNote($"resume start: sessionFile {DescribeSessionFile()}");

        // A missing or zero-length session file is not resumable — don't spend a round trip on it.
        if (!HasUsableSessionFile())
        {
            LogResumeNote("no usable session file at startup — showing login (nothing to delete)");
            DiscardSession();
            return false;
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            EnsureClient();
            // Resume the saved session WITHOUT falling back to a fresh interactive login.
            //
            // Two things make resume work here:
            //  1. ConfigCallback returns "-1" for "user_id" — that is what actually stops the library
            //     asking for "phone_number" while validating the resumed account (see ConfigCallback).
            //  2. reloginOnFailedResume:false. With the library default (true), a genuinely failed
            //     resume is turned SILENTLY into a brand-new login, which then asks for "phone_number".
            //     With false, a valid session returns the user directly and a dead session throws its
            //     REAL error (e.g. AUTH_KEY_UNREGISTERED) which IsDeadSession can act on.
            var user = await _client!.LoginUserIfNeeded(reloginOnFailedResume: false).ConfigureAwait(false);
            if (user != null)
            {
                _userDisplayName = GetUserDisplayName(user);
                return true;
            }
            // No user came back but nothing failed hard — keep the session file and let the
            // user sign in this run; do NOT delete a session that may still be valid.
            LogResumeNote("resume returned no user (session present but not authorized)");
            DisposeClient();
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Record WHY resume failed (secret-safe) so a persistent-login problem is diagnosable.
            LogResumeFailure(ex);

            // Only throw away the session when Telegram tells us it is genuinely dead (revoked,
            // unregistered, expired, or the account is gone). A transient failure — no network,
            // a timeout, DC hiccup — must NOT log the user out: keep the file and just show login.
            if (IsDeadSession(ex))
            {
                DiscardSession();
            }
            else
            {
                DisposeClient();
            }
            return false;
        }
    }

    /// <summary>
    /// True only for failures that mean the stored session can never work again, so it is safe to
    /// delete. Transient/offline errors return false so the session is preserved for the next run.
    /// </summary>
    private static bool IsDeadSession(Exception ex)
    {
        var m = ex.Message ?? string.Empty;
        return m.Contains("AUTH_KEY_UNREGISTERED", StringComparison.OrdinalIgnoreCase)
            || m.Contains("AUTH_KEY_DUPLICATED", StringComparison.OrdinalIgnoreCase)
            || m.Contains("SESSION_REVOKED", StringComparison.OrdinalIgnoreCase)
            || m.Contains("SESSION_EXPIRED", StringComparison.OrdinalIgnoreCase)
            || m.Contains("USER_DEACTIVATED", StringComparison.OrdinalIgnoreCase)
            || m.Contains("AUTH_KEY_PERM_EMPTY", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Append a single sanitized line describing a resume failure to
    /// <c>%LOCALAPPDATA%\Nuvia\logs\resume.log</c>. Never writes credentials, phone numbers, codes
    /// or session material (reuses <see cref="SanitizeException"/>). Best-effort — failures ignored.
    /// </summary>
    private void LogResumeFailure(Exception ex)
    {
        LogResumeNote($"resume failed: {ex.GetType().Name}: {SanitizeException(ex)}");
    }

    /// <summary>
    /// Append a single secret-safe diagnostic line to <c>%LOCALAPPDATA%\Nuvia\logs\resume.log</c>.
    /// Best-effort — never throws, never writes credentials/session material.
    /// </summary>
    private static void LogResumeNote(string message)
    {
        try
        {
            var logDir = System.IO.Path.Combine(NuviaConfig.LocalDataDirectory, "logs");
            System.IO.Directory.CreateDirectory(logDir);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}";
            System.IO.File.AppendAllText(System.IO.Path.Combine(logDir, "resume.log"), line);
        }
        catch
        {
            // Diagnostics must never break startup.
        }
    }

    /// <summary>A session file must exist and be non-empty to be worth resuming.</summary>
    private bool HasUsableSessionFile()
    {
        try
        {
            var path = _config.GetSessionPath();
            var info = new System.IO.FileInfo(path);
            return info.Exists && info.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Secret-safe one-liner about the session file for the diagnostic log: existence and byte
    /// length only. The path is the fixed, non-secret <c>%LOCALAPPDATA%\Nuvia\session</c> location.
    /// </summary>
    private string DescribeSessionFile()
    {
        try
        {
            var info = new System.IO.FileInfo(_config.GetSessionPath());
            return info.Exists ? $"exists=true size={info.Length}B" : "exists=false";
        }
        catch
        {
            return "exists=unknown";
        }
    }

    /// <summary>Delete an unusable session file so the user is not stuck on a dead session.</summary>
    private void DiscardSession()
    {
        DisposeClient();
        try
        {
            var path = _config.GetSessionPath();
            if (System.IO.File.Exists(path))
                System.IO.File.Delete(path);
        }
        catch
        {
            // Best effort — a locked/absent file is not fatal.
        }
    }

    /// <summary>
    /// Request a verification code for the given phone number.
    /// </summary>
    public async Task<string?> RequestCodeAsync(string phone, CancellationToken ct = default)
    {
        EnsureClient();
        ResetState();

        try
        {
            // Normalize phone: keep + and digits only
            var normalizedPhone = NormalizePhone(phone);
            if (string.IsNullOrWhiteSpace(normalizedPhone) || !normalizedPhone.StartsWith("+"))
                return "Enter a valid international phone number (e.g., +1 555 123 4567).";

            // Start the login process — returns what's needed next
            _loginStepValue = normalizedPhone;
            DiagnosticLog.Write("signin.log", "RequestCode: calling Login (connecting to Telegram)…");
            var result = await _client!.Login(_loginStepValue!).ConfigureAwait(true);
            _loginStepValue = null; // phone no longer needed in memory
            DiagnosticLog.Write("signin.log", $"RequestCode: Login returned step='{result ?? "null"}'");

            // First call with phone returns "verification_code" (or "password" if 2FA already set and session exists but that's rare here)
            if (result == "verification_code")
            {
                _codeRequested = true;
                return null; // success — code sent
            }
            else if (result == "password")
            {
                // Rare: existing session but needs 2FA password directly
                _codeRequested = true;
                _codeVerified = true;
                _requiresPassword = true;
                return null;
            }
            else
            {
                return $"Unexpected login response: {result}";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _loginStepValue = null;
            DiagnosticLog.WriteException("signin.log", "RequestCode failed", ex);
            return "Network error or invalid phone: " + SanitizeException(ex);
        }
    }

    /// <summary>
    /// Verify the submitted code.
    /// </summary>
    public async Task<string?> VerifyCodeAsync(string code, CancellationToken ct = default)
    {
        if (!_codeRequested)
            return "No code was requested. Please start again.";

        var trimmedCode = code?.Trim() ?? string.Empty;
        // Telegram code length varies by delivery method (4–8 digits) — never assume SMS/6.
        if (trimmedCode.Length < 4 || trimmedCode.Length > 8 || !trimmedCode.All(char.IsDigit))
            return "Invalid code. Enter the code you received.";

        try
        {
            _loginStepValue = trimmedCode;
            var result = await _client!.Login(_loginStepValue!).ConfigureAwait(true);
            _loginStepValue = null; // code must not linger in memory

            if (result == "password")
            {
                // 2FA enabled — need password next
                _codeVerified = true;
                _requiresPassword = true;
                return null;
            }
            else if (result == "first_name")
            {
                // New user sign-up (unlikely for existing users)
                return "Account registration required. Please contact support.";
            }
            else if (result == null || _client.User != null)
            {
                // Login complete — no 2FA needed
                _codeVerified = true;
                _requiresPassword = false;
                _userDisplayName = GetUserDisplayName(_client.User!);
                // Secret-safe: confirms login set an authorized user in memory, and how big the
                // on-disk session is at this moment (no id, phone or credential — just a bool + size).
                DiagnosticLog.Write("signin.log",
                    $"VerifyCode: login complete authorized={_client.User != null} sessionFile {DescribeSessionFile()}");
                return null;
            }
            else
            {
                return $"Unexpected verification response: {result}";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "Invalid code or network error: " + SanitizeException(ex);
        }
    }

    /// <summary>
    /// Submit 2FA password.
    /// </summary>
    public async Task<string?> SubmitPasswordAsync(string password, CancellationToken ct = default)
    {
        if (!_codeVerified || !_requiresPassword)
            return "No password step is active.";

        if (string.IsNullOrWhiteSpace(password))
            return "Enter your 2FA password.";

        try
        {
            _loginStepValue = password;
            var result = await _client!.Login(_loginStepValue!).ConfigureAwait(true);
            _loginStepValue = null; // password must not linger in memory

            if (result == null || _client.User != null)
            {
                _userDisplayName = GetUserDisplayName(_client.User!);
                DiagnosticLog.Write("signin.log",
                    $"SubmitPassword: login complete authorized={_client.User != null} sessionFile {DescribeSessionFile()}");
                return null; // success
            }
            else if (result == "password")
            {
                return "Incorrect password. Try again.";
            }
            else
            {
                return $"Unexpected password response: {result}";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _loginStepValue = null;
            return "Network error: " + SanitizeException(ex);
        }
    }

    public void Reset()
    {
        ResetState();
    }

    // ----- Resend / SMS (or call) fallback -----

    /// <summary>Where the current code was delivered, in plain words, or null when none has been sent.</summary>
    public string? CurrentCodeMedium => _lastSentCode is { } sc ? DescribeSentType(sc.type) : null;

    /// <summary>The medium a resend would use next, or null when Telegram offered no alternative.</summary>
    public string? NextCodeMedium
        => _lastSentCode is { } sc && sc.next_type != 0 ? DescribeCodeType(sc.next_type) : null;

    /// <summary>True when a code is outstanding, unverified, and Telegram offers another delivery medium.</summary>
    public bool CanResendCode
        => _codeRequested && !_codeVerified && !_qrLoginActive
           && _lastSentCode is { } sc && sc.next_type != 0;

    /// <summary>Seconds left before a resend will actually be honoured (0 when ready).</summary>
    public int ResendCooldownSeconds
    {
        get
        {
            if (!CanResendCode) return 0;
            var s = (int)Math.Ceiling((_resendAvailableUtc - DateTime.UtcNow).TotalSeconds);
            return s > 0 ? s : 0;
        }
    }

    /// <summary>
    /// Ask Telegram to resend the login code via the next medium.
    /// <para>
    /// Mechanism (verified against WTelegramClient 4.4.8): feeding an empty verification code into the
    /// active <c>Login()</c> flow makes the library call <c>Auth_ResendCode</c> — but only when a
    /// <c>next_type</c> exists AND its <c>timeout</c> has elapsed; otherwise it silently re-asks for the
    /// code without resending. To stay honest we (a) only offer this once <see cref="CanResendCode"/> is
    /// true and the cooldown is up, and (b) confirm a fresh <c>Auth_SentCode</c> actually arrived (via the
    /// OnOther stream) before reporting success. If none arrived, we tell the user to wait rather than
    /// claim a resend that did not occur.
    /// </para>
    /// </summary>
    public async Task<string?> ResendCodeAsync(CancellationToken ct = default)
    {
        if (_client is null || !_codeRequested || _codeVerified)
            return "There is no code to resend right now. Please start again.";
        if (_lastSentCode is not { } sc || sc.next_type == 0)
            return "Telegram did not offer another way to send the code.";

        var wait = ResendCooldownSeconds;
        if (wait > 0)
            return $"Please wait {wait}s, then you can get the code by {DescribeCodeType(sc.next_type)}.";

        var before = _sentCodeCount;
        try
        {
            DiagnosticLog.Write("signin.log", $"Resend: requesting resend via {DescribeCodeType(sc.next_type)}");
            // Empty code → library resends when eligible. We resume on the UI thread for the caller.
            var result = await _client!.Login(string.Empty).ConfigureAwait(true);

            if (result == "password")
            {
                // Unlikely on a resend, but handle it: 2FA is now required.
                _codeVerified = true;
                _requiresPassword = true;
                return null;
            }
            if (result == "verification_code")
            {
                if (_sentCodeCount > before)
                    return null; // a new Auth_SentCode really arrived — the code was resent

                // The library re-asked without resending (still inside its own timeout window).
                var w = ResendCooldownSeconds;
                return w > 0
                    ? $"Please wait {w}s before requesting another code."
                    : "Telegram is not ready to resend the code yet. Please wait a moment and try again.";
            }
            return $"Unexpected response while resending: {result}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DiagnosticLog.WriteException("signin.log", "Resend failed", ex);
            return "Could not resend the code: " + SanitizeException(ex);
        }
    }

    // ----- QR-code login -----

    /// <inheritdoc />
    public bool SupportsQrLogin => true;

    /// <inheritdoc />
    public async Task<string?> LoginWithQrAsync(Action<string> onQrUrl, CancellationToken ct = default)
    {
        if (onQrUrl is null) throw new ArgumentNullException(nameof(onQrUrl));

        // Begin from a pristine client: dispose any half-started phone Login() loop (which also leaves the
        // library's config wrapper installed) so QR talks to the raw ConfigCallback and no stale flow lingers.
        RecreateClientForFreshLogin();
        ResetState();
        _qrLoginActive = true;
        _qrPasswordAsk = 0;

        // If the QR attempt is cancelled while we are blocked waiting for the 2FA password, release the
        // block so the flow can unwind instead of hanging on a password that will never come.
        using var reg = ct.Register(() =>
        {
            try { _qrPasswordTcs?.TrySetCanceled(); } catch { /* best effort */ }
        });

        try
        {
            DiagnosticLog.Write("signin.log", "QR: starting LoginWithQRCode");
            var user = await _client!
                .LoginWithQRCode(onQrUrl, except_ids: null, logoutFirst: true, ct)
                .ConfigureAwait(true);

            if (user != null)
            {
                _codeRequested = true;
                _codeVerified = true;
                _requiresPassword = false;
                _userDisplayName = GetUserDisplayName(user);
                DiagnosticLog.Write("signin.log",
                    $"QR: login complete authorized={_client.User != null} sessionFile {DescribeSessionFile()}");
                return null; // success
            }

            return "QR sign-in did not complete. Please try again.";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteException("signin.log", "QR login failed", ex);
            return "QR sign-in failed: " + SanitizeException(ex);
        }
        finally
        {
            _qrLoginActive = false;
            _qrPasswordTcs = null;
        }
    }

    /// <inheritdoc />
    public void ProvideQrPassword(string password)
    {
        // Hand the typed password to the blocked ConfigCallback. Never logged.
        _qrPasswordTcs?.TrySetResult(password ?? string.Empty);
    }

    /// <summary>
    /// Block the calling (threadpool) thread until the UI supplies the 2FA password for QR sign-in.
    /// Raises <see cref="QrPasswordRequired"/> first; the attempt number lets the UI show a
    /// "wrong password" hint on a retry.
    /// </summary>
    private string WaitForQrPassword()
    {
        var attempt = Interlocked.Increment(ref _qrPasswordAsk);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _qrPasswordTcs = tcs;
        QrPasswordRequired?.Invoke(attempt);
        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>Dispose any current client (and its parked login loop) and create a fresh one.</summary>
    private void RecreateClientForFreshLogin()
    {
        DisposeClient();
        EnsureClient();
    }

    /// <summary>
    /// Handle objects the library raises outside the update stream. We only care about
    /// <see cref="Auth_SentCode"/>: it tells us the delivery medium, the next medium and the resend
    /// timeout. Runs synchronously (no await) so the state is up to date before Login() returns.
    /// </summary>
    private Task OnClientOther(IObject obj)
    {
        if (obj is Auth_SentCode sc)
        {
            _lastSentCode = sc;
            _sentCodeCount++;
            var timeout = sc.timeout > 0 ? sc.timeout : 0;
            _resendAvailableUtc = DateTime.UtcNow.AddSeconds(timeout);
            DiagnosticLog.Write("signin.log",
                $"SentCode: via {DescribeSentType(sc.type)}; next={(sc.next_type != 0 ? DescribeCodeType(sc.next_type) : "none")}; timeout={timeout}s");
        }
        return Task.CompletedTask;
    }

    /// <summary>Plain-language name for the medium a code was sent through. Never a secret.</summary>
    private static string DescribeSentType(Auth_SentCodeType? type) => type switch
    {
        Auth_SentCodeTypeApp => "the Telegram app",
        Auth_SentCodeTypeSms => "SMS",              // also covers Auth_SentCodeTypeFragmentSms (derived)
        Auth_SentCodeTypeCall => "a phone call",    // also covers Auth_SentCodeTypeMissedCall (derived)
        Auth_SentCodeTypeFlashCall => "a phone call",
        _ => "your phone"
    };

    /// <summary>Plain-language name for the medium a resend would use next.</summary>
    private static string DescribeCodeType(Auth_CodeType type) => type switch
    {
        Auth_CodeType.Sms => "SMS",
        Auth_CodeType.Call => "a phone call",
        Auth_CodeType.FlashCall => "a phone call",
        Auth_CodeType.MissedCall => "a phone call",
        Auth_CodeType.FragmentSms => "Fragment",
        _ => "another method"
    };


    /// <summary>
    /// Log out at the user's explicit request.
    /// <para>
    /// When a signed-in client exists and Telegram is reachable, this calls the library's supported
    /// server-side logout (<c>Auth_LogOut</c>), which revokes this session on Telegram's side. Whether or
    /// not that succeeds, the client is then disposed, all transient in-memory secrets are cleared, and
    /// the local session file is deleted — because deleting the local session is exactly what the user
    /// asked for.
    /// </para>
    /// <para>
    /// It never deletes remote messages, files, groups or the account, and it never touches the local
    /// index database. If the server could not be reached, the returned outcome says so, so the caller
    /// can be honest that the server-side session may still be active.
    /// </para>
    /// </summary>
    public async Task<LogoutOutcome> LogOutAsync(CancellationToken ct = default)
    {
        var outcome = LogoutOutcome.LocalOnlyServerUnconfirmed;

        // Only a genuinely signed-in client has a server-side session worth revoking.
        var client = _client;
        if (client is not null && client.User is not null)
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                // Supported server-side logout. This revokes the current authorization on Telegram's
                // side; it does not delete any messages, media, groups or the account itself.
                await client.Auth_LogOut().ConfigureAwait(false);
                outcome = LogoutOutcome.ServerConfirmed;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Offline, or the revoke call failed. We still remove the local session below, but the
                // server-side session may remain until it is revoked elsewhere or expires.
                outcome = LogoutOutcome.LocalOnlyServerUnconfirmed;
            }
        }

        // Clear transient secrets and login state first, then dispose the client and remove the local
        // session file. DiscardSession() disposes the client and deletes only the session file — it does
        // not touch index.db or any remote data.
        ResetState();
        DiscardSession();

        return outcome;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            DisposeClient();
            _disposed = true;
        }
    }

    private void DisposeClient()
    {
        if (_client != null)
        {
            // Secret-safe: records whether the client we are about to dispose was authorized.
            // If login persists to disk only on Dispose, this proves whether an authorized client
            // ever reached disposal on this run (no id/phone/credential is written).
            bool wasAuthorized = _client.User != null;

            // Release any threadpool thread blocked waiting for a QR 2FA password, so disposal never
            // leaves it parked forever.
            try { _qrPasswordTcs?.TrySetCanceled(); } catch { /* best effort */ }

            try
            {
                _client.OnOther -= OnClientOther;
            }
            catch
            {
                // Ignore — the client may already be tearing down.
            }
            try
            {
                _client.Dispose();
            }
            catch
            {
                // Ignore disposal errors
            }
            _client = null;
            DiagnosticLog.Write("signin.log", $"DisposeClient: disposed wasAuthorized={wasAuthorized}");
        }
    }

    private void ResetState()
    {
        _loginStepValue = null;
        _codeRequested = false;
        _codeVerified = false;
        _requiresPassword = false;
        _userDisplayName = null;

        // Resend tracking is per-attempt — clear it so a new phone entry starts from a clean slate.
        _lastSentCode = null;
        _sentCodeCount = 0;
        _resendAvailableUtc = DateTime.MinValue;

        // QR password bridge (defensive; the QR flow manages its own lifetime via LoginWithQrAsync).
        _qrLoginActive = false;
        _qrPasswordAsk = 0;
    }

    private static string NormalizePhone(string phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return string.Empty;
        var cleaned = phone.Trim();
        // Keep + and digits only
        var result = new System.Text.StringBuilder();
        bool hasPlus = false;
        foreach (var c in cleaned)
        {
            if (c == '+' && !hasPlus && result.Length == 0)
            {
                result.Append(c);
                hasPlus = true;
            }
            else if (char.IsDigit(c))
            {
                result.Append(c);
            }
        }
        return result.ToString();
    }

    private static string GetUserDisplayName(TL.User user)
    {
        if (!string.IsNullOrWhiteSpace(user.username))
            return "@" + user.username;
        var name = string.Join(" ", user.first_name, user.last_name).Trim();
        return string.IsNullOrWhiteSpace(name) ? "User " + user.id : name;
    }

    private static string SanitizeException(Exception ex)
    {
        var msg = ex.Message ?? string.Empty;

        // Redact credential VALUES if a library error ever embeds them, while still surfacing the
        // Telegram error CODE (e.g. API_ID_INVALID, PHONE_NUMBER_INVALID). The code names a field but
        // is not itself a secret, and it is exactly what the user needs to fix a bad configuration.

        // Long opaque hex tokens: 32-char api_hash, session keys, auth-key material.
        msg = System.Text.RegularExpressions.Regex.Replace(msg, @"\b[A-Fa-f0-9]{16,}\b", "[redacted]");

        // Phone numbers, api_id, verification codes and other 5+ digit runs.
        msg = System.Text.RegularExpressions.Regex.Replace(msg, @"\+?\d[\d\s\-]{4,}", "[redacted]");

        return msg.Length > 300 ? msg[..300] : msg;
    }
}