using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Nuvia.App.Services;

namespace Nuvia.App.QuickUpload;

/// <summary>
/// Drives a shell-launched quick upload without opening the full main window: it validates the file,
/// resolves the destination (Saved Messages, the managed group, or a chooser/create-group confirm), and
/// runs the transfer showing only a <see cref="TransferProgressWindow"/>.
/// <para>
/// It reuses the same <see cref="FileTransferCoordinator"/> the main window uses, so the pause/resume
/// back-pressure fix is inherited unchanged. It performs no fake success, creates nothing without the
/// explicit confirmation dialog, and uploads only the single file it was given (never a folder).
/// </para>
/// <para>
/// Used only when there is no main window in the process (a signed-in cold start via the shell menu).
/// When the main window is already open the request is routed there instead, so exactly one
/// coordinator — and one transfer at a time — exists per process.
/// </para>
/// </summary>
public sealed class QuickUploadController
{
    private readonly IndexStore _index;
    private readonly ITelegramStorage _storage;
    private readonly long _accountId;
    private readonly FileTransferCoordinator _transfers;

    private TransferProgressWindow? _window;
    private CancellationTokenSource? _cts;
    private bool _active;

    /// <summary>Raised once a submitted request has fully finished (uploaded, failed, or cancelled).</summary>
    public event Action? Finished;

    public QuickUploadController(IndexStore index, ITelegramStorage storage, long accountId)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _accountId = accountId;

        // One reporting channel, created on the UI thread — identical to the main window's wiring.
        _transfers = new FileTransferCoordinator(_index, _storage, new Progress<TransferSnapshot>(OnSnapshot));
    }

    /// <summary>True while a request is being resolved or uploaded (single-flight guard).</summary>
    public bool IsBusy => _active;

    /// <summary>
    /// Handles one quick-upload request on the UI thread. An activate-only request just surfaces the
    /// current progress window; a second request while one is in flight is politely refused rather than
    /// starting a concurrent transfer.
    /// </summary>
    public async void Submit(QuickUploadRequest request)
    {
        if (request is null)
            return;

        if (request.IsActivateOnly)
        {
            SurfaceWindow();
            return;
        }

        if (_active)
        {
            MessageBox.Show(
                "Nuvia is already uploading a file. Please wait for it to finish, then try again.",
                "Nuvia", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await RunAsync(request);
    }

    private async Task RunAsync(QuickUploadRequest request)
    {
        _active = true;
        try
        {
            var path = request.Path;

            // Only ever a single existing file. A folder is out of scope and must never widen into a
            // recursive upload.
            if (Directory.Exists(path))
            {
                ShowInfo("Folders can’t be uploaded",
                    "Nuvia uploads single files. Uploading a whole folder isn’t supported.");
                return;
            }

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                ShowInfo("File not found",
                    "Nuvia couldn’t find the file to upload. It may have been moved, renamed, or deleted.");
                return;
            }

            if (!_storage.IsAvailable)
            {
                ShowInfo("Not connected",
                    _storage.UnavailableReason ?? "Sign in to Nuvia first, then try the upload again.");
                return;
            }

            var plan = QuickUploadDestinationPlanner.PlanDestination(
                _window, path, request.To, _accountId, _index, _storage);

            StorageDestination destination;
            switch (plan)
            {
                case UseSavedMessages:
                    destination = StorageDestination.SavedMessages;
                    break;

                case UseExistingGroup existing:
                    destination = StorageDestination.ForGroup(existing.Group);
                    break;

                case CreateNewGroup create:
                    var created = await CreateGroupAsync(create.Title);
                    if (created is null)
                        return; // creation failed or nothing usable came back — already reported.
                    destination = StorageDestination.ForGroup(created);
                    break;

                case CancelQuickUpload:
                default:
                    return; // user backed out.
            }

            await UploadAsync(path, destination);
        }
        finally
        {
            _active = false;
            CloseWindow();
            Finished?.Invoke();
        }
    }

    private async Task<ManagedStorageGroup?> CreateGroupAsync(string title)
    {
        // A single short RPC, not a cancellable byte transfer: no Cancel button, indeterminate bar.
        ShowWindow($"Creating private storage group “{title}”…", cancellable: false, indeterminate: true);

        try
        {
            var group = await _storage.CreatePrivateStorageGroupAsync(title, CancellationToken.None);

            // Persist identifiers only after the remote create actually succeeded.
            _index.SaveStorageGroup(group);
            return group;
        }
        catch (Exception)
        {
            // No raw RPC text (it may name identifiers); a safe, generic message only.
            ShowInfo("Could not create the group",
                "The private storage group was not created, so nothing was uploaded. "
                + "Nothing was changed on your account.");
            return null;
        }
    }

    private async Task UploadAsync(string path, StorageDestination destination)
    {
        var name = Path.GetFileName(path);
        UploadResult result;

        _cts = new CancellationTokenSource();
        ShowWindow($"Uploading {name} to {destination.DisplayName}…", cancellable: true, indeterminate: false);

        try
        {
            // Null per-call progress keeps the coordinator's single reporting channel authoritative.
            result = await _transfers.UploadAsync(path, _accountId, destination, null, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Cancelling is a normal outcome, not a failure — no error dialog.
            return;
        }
        catch (Exception)
        {
            ShowInfo("Upload failed",
                $"{name} was not uploaded to {destination.DisplayName}. Nothing was added to the local index.");
            return;
        }

        if (result.Outcome == UploadOutcome.UploadedButNotIndexed)
        {
            // Remote succeeded, local indexing did not. Be explicit, and never re-upload.
            ShowInfo("Uploaded, but not indexed",
                $"{name} was uploaded to your Telegram {destination.DisplayName}, but Nuvia could not add it "
                + "to the local index. Do not upload it again — that would store a second copy. "
                + "It will appear in Nuvia once the index can be written.");
        }

        // On success there is no extra dialog: the progress window shows "complete" and then closes.
    }

    // ------------------------------------------------------------------ progress window

    private void ShowWindow(string header, bool cancellable, bool indeterminate)
    {
        if (_window is null)
        {
            _window = new TransferProgressWindow();
            _window.CancelRequested += OnCancelRequested;
            _window.PauseResumeRequested += OnPauseResumeRequested;
        }

        _window.Begin(header, cancellable, indeterminate);
        if (!_window.IsVisible)
            _window.Show();
        _window.Activate();
    }

    private void CloseWindow()
    {
        if (_window is null)
            return;

        var window = _window;
        _window = null;
        window.CancelRequested -= OnCancelRequested;
        window.PauseResumeRequested -= OnPauseResumeRequested;
        window.ForceClose();
    }

    private void SurfaceWindow()
    {
        if (_window is not { } window)
            return;

        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void OnCancelRequested(object? sender, EventArgs e)
    {
        if (_cts is null || _cts.IsCancellationRequested)
            return;
        _cts.Cancel();
    }

    private void OnPauseResumeRequested(object? sender, EventArgs e)
    {
        if (!_transfers.IsBusy)
            return;

        if (_transfers.IsPaused)
            _transfers.Resume();
        else
            _transfers.Pause();
    }

    private void OnSnapshot(TransferSnapshot snapshot)
    {
        if (_window is null)
            return;

        var line = DescribeSnapshot(snapshot);
        var header = HeaderForSnapshot(snapshot);

        var running = snapshot.State is TransferState.Preparing or TransferState.Uploading
            or TransferState.Downloading or TransferState.WaitingForFloodLimit
            or TransferState.Paused or TransferState.Cancelling;

        var indeterminate =
            snapshot.State is TransferState.Uploading or TransferState.Downloading
            && snapshot.TotalBytes is null or <= 0;

        var pausable = snapshot.State is TransferState.Uploading or TransferState.Downloading
            or TransferState.Paused;

        _window.Report(header, line, snapshot.Fraction ?? 0d, indeterminate);
        _window.SetCancellable(running, snapshot.State != TransferState.Cancelling);
        _window.SetPause(pausable, snapshot.State == TransferState.Paused);

        _window.SetFloodWait(snapshot.FloodWaitSecondsRemaining is int seconds
            ? $"FLOOD_WAIT {seconds}s — waiting as the server instructed"
            : null);
    }

    private void ShowInfo(string title, string message)
    {
        var owner = _window as Window;
        if (owner is not null && owner.IsVisible)
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ------------------------------------------------------------------ formatting (mirrors MainWindow)

    private static string HeaderForSnapshot(TransferSnapshot snapshot) => snapshot.State switch
    {
        TransferState.Preparing => $"{snapshot.Operation}: preparing…",
        TransferState.Uploading => "Uploading to Nuvia",
        TransferState.Downloading => "Downloading from Nuvia",
        TransferState.Paused => "Paused",
        TransferState.WaitingForFloodLimit => "Waiting for the server’s rate limit",
        TransferState.Cancelling => "Cancelling…",
        TransferState.Completed => $"{snapshot.Operation} complete",
        TransferState.Failed => $"{snapshot.Operation} failed",
        TransferState.Cancelled => $"{snapshot.Operation} cancelled",
        _ => snapshot.Operation,
    };

    private static string DescribeSnapshot(TransferSnapshot snapshot)
    {
        var bytes = snapshot.TotalBytes is > 0
            ? string.Create(CultureInfo.InvariantCulture,
                $"{FormatSize(snapshot.BytesTransferred)} of {FormatSize(snapshot.TotalBytes.Value)}")
            : FormatSize(snapshot.BytesTransferred);

        var percent = snapshot.Fraction is double fraction
            ? string.Create(CultureInfo.InvariantCulture, $" ({fraction * 100:0}%)")
            : string.Empty;

        return snapshot.State switch
        {
            TransferState.Idle => "Ready",
            TransferState.Preparing => $"{snapshot.Operation}: preparing…",
            TransferState.Uploading => $"Uploading — {bytes}{percent}",
            TransferState.Downloading => $"Downloading — {bytes}{percent}",
            TransferState.Paused => $"Paused — {bytes}{percent}",
            TransferState.WaitingForFloodLimit => $"FLOOD_WAIT — {snapshot.FloodWaitSecondsRemaining ?? 0}s remaining",
            TransferState.Cancelling => $"{snapshot.Operation}: cancelling…",
            TransferState.Completed => $"{snapshot.Operation} complete — {bytes}",
            TransferState.Failed => $"{snapshot.Operation} failed",
            TransferState.Cancelled => $"{snapshot.Operation} cancelled",
            _ => snapshot.Operation,
        };
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} B")
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }
}
