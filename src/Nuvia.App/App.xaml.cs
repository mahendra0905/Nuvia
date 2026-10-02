using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Nuvia.App.QuickUpload;
using Nuvia.App.Services;

namespace Nuvia.App;

public partial class App : Application
{
    private LoginWindow? _loginWindow;
    private IAuthService? _auth;

    /// <summary>
    /// The remote storage layer for the signed-in account. Owned here (not by the main window) so it can
    /// be disposed — detaching its live update-stream watch — before the auth service disposes the
    /// underlying Telegram client, on both normal exit and logout.
    /// </summary>
    private ITelegramStorage? _storage;

    /// <summary>
    /// One process per user + the pipe that carries a shell-launched quick upload to it. Created in
    /// <see cref="OnStartup"/>; a second launch hands its request to the primary and exits.
    /// </summary>
    private SingleInstanceCoordinator? _singleInstance;

    /// <summary>
    /// True when THIS process was launched as a shell quick upload (<c>--upload …</c>). In that case the
    /// signed-in surface is a transfer window driven by <see cref="_quickController"/>, not the main window.
    /// Never flipped by a pipe hand-off — a normal launch stays a normal launch.
    /// </summary>
    private bool _quickUploadMode;

    /// <summary>
    /// A quick-upload request waiting for sign-in to finish: either this process's launch argument, or one
    /// that arrived over the pipe while signing in. Applied once signed in, then cleared (last request wins).
    /// </summary>
    private QuickUploadRequest? _pendingQuickUpload;

    /// <summary>The standalone quick-upload flow (no main window), alive only in <see cref="_quickUploadMode"/>.</summary>
    private QuickUploadController? _quickController;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Capture any unhandled failure (secret-safe) so a freeze/crash on sign-in is diagnosable.
        // These never write credentials, phone numbers, codes or session material.
        DispatcherUnhandledException += (_, args) =>
        {
            DiagnosticLog.WriteException("crash.log", "DispatcherUnhandledException", args.Exception);
            // Let the app keep running where possible rather than dying silently on a UI-thread error.
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            DiagnosticLog.WriteException("crash.log", "AppDomain.UnhandledException", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            DiagnosticLog.WriteException("crash.log", "UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        // --- shell quick upload + single instance --------------------------------------------------
        // A shell-launched "Upload to Nuvia" starts Nuvia with --upload "<path>" [--to saved|group|ask].
        // Parse it (pure, no filesystem access here), then enforce one process per user: the primary owns
        // the Telegram session; any later launch hands its request over a local named pipe and exits, so a
        // second process never opens the .session file.
        QuickUploadRequest? request = QuickUploadCommandLine.Parse(e.Args);

        // "--upload with no usable path" is malformed input, not a normal launch: report it safely (never
        // echo the arguments) and stop, rather than silently opening the app or widening scope.
        if (request is null && QuickUploadCommandLine.ContainsUploadFlag(e.Args))
        {
            MessageBox.Show(
                "Nuvia could not start the upload because no file was provided.",
                "Nuvia", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _singleInstance = new SingleInstanceCoordinator();
        if (!_singleInstance.TryAcquirePrimary())
        {
            // Another instance already owns the session. Hand off the request (or a plain "surface
            // yourself" for a no-argument launch) and exit at once. TrySendAsync never throws.
            await _singleInstance.TrySendAsync(request ?? QuickUploadRequest.Activate);
            Shutdown();
            return;
        }

        // We are the primary: accept hand-offs from future launches for the rest of our lifetime.
        _singleInstance.StartServer(OnInstanceSignal);

        if (request is not null)
        {
            _quickUploadMode = true;
            _pendingQuickUpload = request;
        }

        try
        {
            await StartAsync();
        }
        catch (Exception)
        {
            // Never surface an exception body here: it can name credentials or paths.
            ShowConfigurationError(
                "Nuvia could not start. Check your configuration in " +
                @"%LOCALAPPDATA%\Nuvia\config.json and try again.",
                retry: StartAsync);
        }
    }

    /// <summary>
    /// Demo mode is opt-in only (NUVIA_DEMO=1). It is never selected automatically, so a
    /// missing configuration can never masquerade as a working sign-in.
    /// </summary>
    private static bool IsDemoRequested()
        => string.Equals(Environment.GetEnvironmentVariable("NUVIA_DEMO"), "1", StringComparison.Ordinal);

    private async Task StartAsync()
    {
        if (IsDemoRequested())
        {
            // Explicit, clearly-labelled development stand-in.
            ShowLogin(new DemoAuthSimulator(), "Nuvia — Sign In (Demo Mode)");
            return;
        }

        if (!NuviaConfig.TryLoad(out var config, out var configError))
        {
            // Safe configuration error. No credential form is shown to the user.
            ShowConfigurationError(
                configError + "\n\nNuvia never asks for developer credentials. " +
                "See docs/CREDENTIALS.md for how to supply them.",
                retry: StartAsync);
            return;
        }

        IAuthService auth;
        try
        {
            auth = new TelegramAuthService(config);
        }
        catch (Exception)
        {
            ShowConfigurationError(
                "Nuvia could not initialise the Telegram client. " +
                "Check your configuration and try again.",
                retry: StartAsync);
            return;
        }

        _auth = auth;

        // A returning user (a stored session file exists) should NOT see the full sign-in window while
        // we resume. Show a small splash, try a silent resume, and on success go straight to the main
        // window. A first-ever run (no session file) skips both splash and probe and shows sign-in
        // immediately. ShutdownMode is OnLastWindowClose, so the next window is always shown BEFORE the
        // splash is closed — the app can never fall through a windowless gap and exit.
        if (auth.HasStoredSession)
        {
            var splash = new SplashWindow();
            MainWindow = splash; // keep the app alive during the async resume
            splash.Show();

            bool resumed;
            try
            {
                resumed = await auth.TryResumeSessionAsync();
            }
            catch (Exception)
            {
                resumed = false;
            }

            if (resumed)
            {
                OnSignedIn();
                splash.Close();
                return;
            }

            // Resume failed (dead session, offline, etc.) — show the real sign-in window, then close
            // the splash so a window is always present.
            var login = ShowLogin(auth, "Nuvia — Sign In");
            MainWindow = login; // repoint off the splash before it closes
            splash.Close();
            return;
        }

        // No stored session: straight to the sign-in window, no splash, no probe.
        ShowLogin(auth, "Nuvia — Sign In");
    }

    private LoginWindow ShowLogin(IAuthService auth, string title)
    {
        // Close any window left over from a failed startup attempt so a retry never stacks windows.
        if (_loginWindow is { } previous)
        {
            _loginWindow = null;
            previous.Closed -= OnAnyLoginClosed;
            previous.Close();
        }

        // Show the window before any await so the app cannot shut down between windows.
        _auth = auth;
        _loginWindow = new LoginWindow(auth)
        {
            Title = title
        };
        Attach(_loginWindow);
        _loginWindow.Show();
        return _loginWindow;
    }

    private LoginWindow Attach(LoginWindow window)
    {
        _loginWindow = window;
        window.AuthenticationCompleted += OnAuthenticationCompleted;
        // A window closed before completing ends the attempt cleanly.
        window.Closed += OnAnyLoginClosed;
        return window;
    }

    private void OnAnyLoginClosed(object? sender, EventArgs e)
    {
        if (sender is LoginWindow w)
        {
            w.Closed -= OnAnyLoginClosed;
        }

        if (!_completed)
        {
            _auth?.Dispose();
        }
    }

    private bool _completed;

    private void OnAuthenticationCompleted()
    {
        // Guard against a duplicate AuthenticationCompleted: bringing up a second signed-in surface (and a
        // second IndexStore on the same file) must never happen even if the event fires twice.
        if (_completed)
            return;

        OnSignedIn();

        // Close the login window only after the signed-in surface is up.
        _loginWindow?.Close();
        _loginWindow = null;
    }

    /// <summary>
    /// The single point every successful sign-in flows through (silent resume or interactive login).
    /// A shell quick-upload launch opens only a transfer window; every other launch opens the main window.
    /// Idempotent: a second call after a session is up is a no-op.
    /// </summary>
    private void OnSignedIn()
    {
        if (_completed)
            return;
        _completed = true;

        if (_quickUploadMode)
        {
            BeginQuickUpload();
        }
        else
        {
            LaunchMainWindow();
            // Apply any shell upload that arrived over the pipe while this normal launch was signing in.
            DrainPendingQuickUploadToMainWindow();
        }
    }

    /// <summary>
    /// Build and show the main window for the signed-in account. Reached only through
    /// <see cref="OnSignedIn"/>, which owns the single-completion guard, so this always builds a window.
    /// </summary>
    private void LaunchMainWindow()
    {
        var accountName = _auth?.UserDisplayName;

        // The index and the storage layer are both bound to this one signed-in account. A window
        // built for account A can therefore never show account B's rows.
        IndexStore index;
        try
        {
            index = new IndexStore(NuviaConfig.GetIndexDatabasePath());
            index.Migrate();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Nuvia could not open its local file index, so the file list cannot be shown.\n\n"
                + ex.Message + "\n\nIndex file: " + NuviaConfig.GetIndexDatabasePath(),
                "Nuvia — index unavailable", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        ITelegramStorage storage = _auth is TelegramAuthService telegram && telegram.SignedInClient is not null
            ? new TelegramStorageService(telegram)
            : new UnavailableStorageService(
                "Demo mode is active, so Saved Messages is not connected. " +
                "No file was uploaded and no example data is shown.");

        // Dispose any storage from a prior signed-in session before replacing it (e.g. a re-login after
        // logout), so its update-stream watch is detached rather than left dangling on an old client.
        _storage?.Dispose();
        _storage = storage;

        var accountId = (_auth as TelegramAuthService)?.AccountId ?? 0;

        // Local, non-sensitive preferences (default upload destination), scoped per account. Never holds
        // credentials, codes, 2FA passwords or session material.
        var settings = new SettingsStore(NuviaConfig.GetSettingsPath());

        // Dev-pushed announcements are read over the signed-in account's own connection. In demo/unavailable
        // mode there is no client, so the feed is simply inactive (null) and the bell shows updates only.
        var announcements = _auth is TelegramAuthService announceAuth && announceAuth.SignedInClient is not null
            ? new AnnouncementService(announceAuth)
            : null;

        var mainWindow = new MainWindow(index, storage, settings, accountId, accountName, announcements)
        {
            Title = string.IsNullOrWhiteSpace(accountName)
                ? "Nuvia"
                : $"Nuvia — {accountName}"
        };

        // The App layer owns the auth service and login windows, so it performs the logout and the
        // in-process return to the sign-in screen.
        mainWindow.LogoutRequested += () => _ = OnLogoutRequestedAsync(mainWindow);

        MainWindow = mainWindow;
        mainWindow.Show();
    }

    // --------------------------------------------------------------- shell quick upload

    /// <summary>
    /// Runs the shell quick upload with no main window: only a transfer window. Reuses the same per-account
    /// services the main window would build. The app is held alive with explicit shutdown for the whole
    /// flow (there are windowless gaps — a chooser closing before the transfer window opens) and exits when
    /// the controller reports it has finished.
    /// </summary>
    private void BeginQuickUpload()
    {
        var request = _pendingQuickUpload ?? QuickUploadRequest.Activate;
        _pendingQuickUpload = null;

        // Defensive: quick-upload mode is only entered with a real --upload request, so an activate-only
        // request means there is nothing to do — exit rather than sit windowless under explicit shutdown.
        if (request.IsActivateOnly)
        {
            Shutdown();
            return;
        }

        // From here the process has no main window; a plain last-window-close must not race it to exit.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        IndexStore index;
        try
        {
            index = new IndexStore(NuviaConfig.GetIndexDatabasePath());
            index.Migrate();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Nuvia could not open its local file index, so the upload cannot be recorded.\n\n"
                + ex.Message,
                "Nuvia — index unavailable", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        ITelegramStorage storage = _auth is TelegramAuthService telegram && telegram.SignedInClient is not null
            ? new TelegramStorageService(telegram)
            : new UnavailableStorageService(
                "Nuvia is not connected to Telegram, so nothing was uploaded.");

        _storage?.Dispose();
        _storage = storage;

        var accountId = (_auth as TelegramAuthService)?.AccountId ?? 0;

        _quickController = new QuickUploadController(index, storage, accountId);
        _quickController.Finished += OnQuickUploadFinished;

        // Defer the actual run to the next dispatcher turn so the splash/login teardown completes first and
        // no window lingers behind the chooser or transfer window.
        Dispatcher.BeginInvoke(new Action(() => _quickController!.Submit(request)));
    }

    /// <summary>The standalone quick upload has fully finished (uploaded, failed, or cancelled) — exit.</summary>
    private void OnQuickUploadFinished() => Shutdown();

    /// <summary>Applies a shell upload that was stashed while a normal launch was still signing in.</summary>
    private void DrainPendingQuickUploadToMainWindow()
    {
        var pending = _pendingQuickUpload;
        _pendingQuickUpload = null;

        if (pending is not null && !pending.IsActivateOnly && MainWindow is MainWindow main)
            main.QuickUpload(pending);
    }

    /// <summary>
    /// Pipe callback for a hand-off from a second launch. Runs on a background thread, so it only marshals
    /// to the UI thread — all window work happens in <see cref="HandleInstanceSignal"/>.
    /// </summary>
    private void OnInstanceSignal(QuickUploadRequest request)
        => Dispatcher.BeginInvoke(new Action(() => HandleInstanceSignal(request)));

    private void HandleInstanceSignal(QuickUploadRequest request)
    {
        // A main window is up → route the upload there; it owns the one coordinator (one transfer at a time).
        if (MainWindow is MainWindow main)
        {
            main.QuickUpload(request);
            return;
        }

        // A standalone quick upload is running → hand it the request (its own single-flight guard applies).
        if (_quickController is not null)
        {
            _quickController.Submit(request);
            return;
        }

        // Otherwise we are still signing in: stash a real upload to run once sign-in completes (last one
        // wins). A bare activate has nothing to surface yet, so it is dropped.
        if (!request.IsActivateOnly)
            _pendingQuickUpload = request;
    }

    /// <summary>
    /// Handle an explicit logout from the main window: revoke the server-side session when reachable,
    /// dispose the client and clear in-memory secrets, remove the local session file, and return to the
    /// sign-in screen without spawning a second app process. The local file index and all remote data
    /// (messages, files, groups, the account) are left untouched.
    /// </summary>
    private async Task OnLogoutRequestedAsync(Window mainWindow)
    {
        var old = _auth;

        LogoutOutcome outcome = LogoutOutcome.LocalOnlyServerUnconfirmed;
        try
        {
            if (old is not null)
                outcome = await old.LogOutAsync();
        }
        catch
        {
            // LogOutAsync already removes the local session on failure; treat anything unexpected here as
            // an offline/local-only logout rather than surfacing a raw error.
            outcome = LogoutOutcome.LocalOnlyServerUnconfirmed;
        }

        // Returning to login must not let ShutdownMode=OnLastWindowClose terminate the app: bring up the
        // fresh sign-in window BEFORE closing the old main window.
        _completed = false;
        try
        {
            await StartAsync();
        }
        catch (Exception)
        {
            ShowConfigurationError(
                "Nuvia could not return to the sign-in screen. Check your configuration in " +
                @"%LOCALAPPDATA%\Nuvia\config.json and restart Nuvia.",
                retry: StartAsync);
        }

        // Detach the storage layer's live watch and release it before the auth service disposes the
        // Telegram client it depends on.
        _storage?.Dispose();
        _storage = null;

        old?.Dispose();

        // The old session is gone; close the now-stale main window. The sign-in window is already open,
        // so the process stays alive.
        mainWindow.Close();

        ShowLogoutOutcome(outcome);
    }

    /// <summary>Tell the user honestly whether the server-side session was revoked or only the local one.</summary>
    private void ShowLogoutOutcome(LogoutOutcome outcome)
    {
        var (message, icon) = outcome == LogoutOutcome.ServerConfirmed
            ? ("You are logged out. This session was revoked on Telegram and the local session was removed "
               + "from this PC.\n\nYour Telegram messages, files, groups and account are unchanged, and "
               + "Nuvia's local file index was kept.",
               MessageBoxImage.Information)
            : ("The local session was removed from this PC, but Nuvia could not reach Telegram to revoke "
               + "the session on the server. It may stay active until you revoke it from another device "
               + "(Telegram → Settings → Devices) or it expires.\n\nYour Telegram messages, files, groups "
               + "and account are unchanged, and Nuvia's local file index was kept.",
               MessageBoxImage.Warning);

        var owner = _loginWindow as Window ?? MainWindow;
        if (owner is not null)
            MessageBox.Show(owner, message, "Logged out", MessageBoxButton.OK, icon);
        else
            MessageBox.Show(message, "Logged out", MessageBoxButton.OK, icon);
    }

    private void ShowConfigurationError(string message, Func<Task> retry)
    {
        // Reuse the open login window when there is one; otherwise create a dedicated one.
        var window = _loginWindow ?? new LoginWindow(new NullAuthService());
        if (_loginWindow is null)
        {
            Attach(window);
            window.Show();
        }

        window.ShowConfigurationError(message, () => _ = retry());
    }

    /// <summary>
    /// Dispose the signed-in auth service (and its Telegram client) when the app exits normally.
    /// <para>
    /// Without this, a completed sign-in left the client alive until process teardown: the TCP
    /// connection to Telegram was never closed cleanly and the session file lock was held until the
    /// OS reclaimed it. A lingering connection can make the next launch look like a second, duplicate
    /// client — exactly the kind of thing that can invalidate a freshly-stored session. Disposing here
    /// closes the connection, flushes and releases the session file, so the next launch resumes cleanly.
    /// </para>
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            DiagnosticLog.Write("signin.log", $"OnExit: disposing auth (present={_auth is not null})");
            // Stop accepting pipe hand-offs and release the single-instance mutex first, so a fresh launch
            // can immediately become the new primary.
            _singleInstance?.Dispose();
            // Dispose storage next: it only detaches its update-stream watch, and must do so before the
            // auth service closes the underlying client.
            _storage?.Dispose();
            _auth?.Dispose();
        }
        catch
        {
            // Shutdown must never throw.
        }
        base.OnExit(e);
    }
}
