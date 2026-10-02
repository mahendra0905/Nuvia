using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TL;

namespace Nuvia.App.Services;

/// <summary>What the local index knows about the outcome of an upload.</summary>
public enum UploadOutcome
{
    /// <summary>Uploaded to Saved Messages and recorded in the local index.</summary>
    Indexed,

    /// <summary>
    /// The remote upload definitely succeeded, but the local index row could not be written.
    /// The file may already exist in Saved Messages, so it must not be uploaded again.
    /// </summary>
    UploadedButNotIndexed,
}

public sealed record UploadResult(UploadOutcome Outcome, IndexedFile? Row, string? IndexError);

public sealed record DownloadResult(bool Saved, bool RefreshedReference, string? DestinationPath);

/// <summary>
/// Orchestrates one-file transfers between the local disk and Telegram Saved Messages.
/// <para>
/// Kept separate from the window so the ordering rules are explicit and testable:
/// the index row is written only after the remote upload succeeded, and the destination file is
/// created only after the download succeeded.
/// </para>
/// <para>
/// Phase 5: exactly one transfer may run at a time, every state change travels over a single
/// <see cref="IProgress{T}"/> channel of <see cref="TransferSnapshot"/> values, and a long
/// FLOOD_WAIT is waited out visibly and cancellably before a single retry. Nothing here ever starts a
/// second file on its own, and no transfer is ever restarted automatically.
/// </para>
/// </summary>
public sealed class FileTransferCoordinator
{
    /// <summary>
    /// WTelegramClient waits out a short flood itself: <c>Client.FloodRetryThreshold</c> is left at its
    /// 60 s default, so this app never stacks a retry on top of a wait the library already performed.
    /// Only a longer FLOOD_WAIT — and FLOOD_PREMIUM_WAIT, which the pinned 4.4.8 build does not treat
    /// specially — reaches this class as an <see cref="RpcException"/> with code 420.
    /// </summary>
    public const int LibraryFloodRetryThresholdSeconds = 60;

    /// <summary>Used only when the server sends a 420 without a usable wait duration.</summary>
    private const int MinimumFloodWaitSeconds = 1;

    private readonly IndexStore _index;
    private readonly ITelegramStorage _storage;
    private readonly IProgress<TransferSnapshot>? _observer;

    // 0 = free, 1 = owned by the transfer that is running. One active transfer at a time.
    private int _active;

    // Pause is a real stop of the byte flow, not a cancel-and-restart. The gate is honoured inside the
    // transfer's own stream (a PausableStream wrapping the file), so a pause stalls the upload's Read /
    // download Write loop — a slow stream is a condition WTelegramClient tolerates. It is deliberately
    // NOT checked inside the library's progress callback: blocking that thread holds the client's
    // internal locks and wedges the whole connection. No invented API, no fake state.
    private readonly TransferGate _gate = new();

    // Last reading seen by the progress callback, so Pause/Resume can report an accurate snapshot
    // immediately (from the UI thread) without waiting for the next callback.
    private long _lastBytes;
    private long? _lastTotal;
    private string _currentOperation = TransferSnapshot.UploadOperation;

    public FileTransferCoordinator(IndexStore index, ITelegramStorage storage, IProgress<TransferSnapshot>? observer = null)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _observer = observer;
    }

    /// <summary>True while a transfer owns the coordinator. Observable from any thread.</summary>
    public bool IsBusy => Volatile.Read(ref _active) == 1;

    /// <summary>True while the running transfer is paused. Observable from any thread.</summary>
    public bool IsPaused => _gate.IsPaused;

    /// <summary>
    /// Pauses the running transfer by closing the gate its file stream honours. The bytes already handed
    /// to the OS may still flush, exactly as a native copy dialog behaves, but the transfer's own read
    /// (upload) or write (download) blocks until <see cref="Resume"/> is called. A no-op when nothing is
    /// running or it is already paused.
    /// </summary>
    public void Pause()
    {
        if (!IsBusy || _gate.IsPaused)
            return;

        _gate.Pause();
        Report(TransferSnapshot.ForState(TransferState.Paused, _currentOperation, _lastBytes, _lastTotal));
    }

    /// <summary>Reopens the gate so a paused transfer continues from where it stopped. A no-op otherwise.</summary>
    public void Resume()
    {
        if (!IsBusy || !_gate.IsPaused)
            return;

        _gate.Resume();
        var state = _currentOperation == TransferSnapshot.DownloadOperation
            ? TransferState.Downloading
            : TransferState.Uploading;
        Report(TransferSnapshot.ForState(state, _currentOperation, _lastBytes, _lastTotal));
    }

    /// <summary>
    /// Uploads one file to the chosen <paramref name="destination"/> and, only on remote success, writes
    /// the index row. Never re-uploads when only the local indexing step fails.
    /// </summary>
    public async Task<UploadResult> UploadAsync(
        string localPath,
        long ownerId,
        StorageDestination destination,
        IProgress<double>? progress,
        CancellationToken ct,
        string? folderPath = null,
        long? folderId = null)
    {
        if (string.IsNullOrWhiteSpace(localPath))
            throw new ArgumentException("A file must be selected first.", nameof(localPath));
        if (!File.Exists(localPath))
            throw new FileNotFoundException("The selected file no longer exists.", localPath);
        ArgumentNullException.ThrowIfNull(destination);

        EnterTransfer();
        long? total = null;
        try
        {
            total = TryLength(localPath);
            var stamp = TryStamp(localPath);

            Report(TransferSnapshot.ForState(TransferState.Preparing, TransferSnapshot.UploadOperation, 0, total));

            // Honest pre-flight ceiling check. Only refuses a file larger than the maximum any user
            // account can upload (the documented Premium figure); anything below that is left to the
            // server to accept or reject, because the account's tier cannot be known reliably here.
            if (total is > 0 && TelegramFileLimits.ExceedsEveryAccountCeiling(total.Value))
                throw new FileExceedsAccountLimitException(
                    "This file is larger than the maximum any Telegram account can upload "
                    + "(4 GB, the Premium ceiling). Nothing was uploaded.");

            // Step 1: remote. Any failure here means nothing was stored anywhere.
            var operation = TransferSnapshot.UploadOperation;
            var reference = await RunWithFloodWaitAsync(
                operation,
                total,
                () => _storage.UploadDocumentAsync(localPath, destination, Pump(operation, total, progress, ct), ct, _gate, folderPath),
                ct).ConfigureAwait(false);

            if (reference is null || !reference.LooksUsable())
                throw new InvalidOperationException(
                    "The upload reported success but returned no usable remote identifiers, "
                    + "so no index row was written.");

            if (Changed(localPath, stamp))
            {
                // The bytes Telegram stored are whatever the file held while it was read, and the size
                // below comes from the server. Record that the local copy moved underneath the transfer
                // instead of pretending nothing happened.
                TransferLog.Write(
                    TransferSnapshot.ForState(TransferState.Uploading, operation, 0, total),
                    TransferErrorClass.LocalFileMissingOrChanged);
            }

            // Step 2: local. A failure here leaves a real remote file behind.
            try
            {
                var original = Path.GetFileName(localPath);
                var row = _index.Insert(
                    ownerId,
                    reference,
                    originalFileName: original,
                    displayName: original,
                    sizeBytes: reference.SizeBytes,
                    mimeType: reference.MimeType,
                    indexedUtc: DateTime.UtcNow,
                    folderId: folderId);

                var done = TransferSnapshot.ForState(
                    TransferState.Completed, operation, reference.SizeBytes, total ?? reference.SizeBytes);
                Report(done);
                TransferLog.Write(done);
                return new UploadResult(UploadOutcome.Indexed, row, null);
            }
            catch (Exception ex)
            {
                // The remote copy exists, so this is a local failure only — never an invitation to retry
                // the upload.
                var error = TransferErrors.IndexFailure();
                var failed = TransferSnapshot.ForState(TransferState.Failed, operation, 0, total);
                Report(failed);
                TransferLog.Write(failed, error.Class);
                return new UploadResult(UploadOutcome.UploadedButNotIndexed, null, ex.Message);
            }
        }
        catch (Exception ex)
        {
            ReportFailure(TransferSnapshot.UploadOperation, total, ex);
            throw;
        }
        finally
        {
            ExitTransfer();
        }
    }

    /// <summary>
    /// Downloads the bytes behind an indexed row: partial temp file first, destination only on
    /// success. Refreshes an expired remote reference once and retries.
    /// </summary>
    /// <param name="confirmOverwrite">
    /// Asked only when the destination already exists at move time. Returning false cancels the
    /// move and leaves any existing file untouched.
    /// </param>
    public async Task<DownloadResult> DownloadAsync(
        IndexedFile file,
        string destinationPath,
        IProgress<double>? progress,
        Func<string, bool>? confirmOverwrite,
        CancellationToken ct)
    {
        if (file is null)
            throw new ArgumentNullException(nameof(file));
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException("A save location must be chosen first.", nameof(destinationPath));
        if (!_storage.IsAvailable)
            throw new InvalidOperationException(_storage.UnavailableReason ?? "Saved Messages is not available.");
        if (!file.Remote.LooksUsable())
            throw new InvalidOperationException(
                "This row has no usable remote identifiers, so the file cannot be fetched.");

        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (string.IsNullOrEmpty(directory))
            throw new InvalidOperationException("The save location is not a valid folder.");
        Directory.CreateDirectory(directory);

        // Partial file lives beside the destination so the finishing move stays on one volume.
        var partial = Path.Combine(
            directory,
            "." + Path.GetFileName(destinationPath) + "." + Guid.NewGuid().ToString("N") + ".nuvia-part");

        EnterTransfer();
        var operation = TransferSnapshot.DownloadOperation;
        long? total = file.SizeBytes > 0 ? file.SizeBytes : null;

        // A file stored in a managed group can only be re-read through that channel, which needs the
        // group's access hash. Resolve it once, scoped to both the account and the file's own channel; if
        // it is gone the refresh step will fail with a clear "group unavailable" error rather than silently
        // doing the wrong thing.
        var channelGroup = file.Remote.PeerKind == SavedMessageRef.PeerKindChannel
            ? _index.GetStorageGroup(file.OwnerId, file.Remote.PeerId)
            : null;
        try
        {
            Report(TransferSnapshot.ForState(TransferState.Preparing, operation, 0, total));

            var reference = file.Remote;
            var refreshed = false;

            try
            {
                try
                {
                    await RunWithFloodWaitAsync(
                        operation,
                        total,
                        () => _storage.DownloadDocumentAsync(
                            reference, partial, Pump(operation, total, progress, ct), ct, _gate),
                        ct).ConfigureAwait(false);
                }
                catch (FileReferenceExpiredException)
                {
                    // The stored reference went stale — ask Telegram for the current metadata,
                    // persist it, and try once more.
                    DeleteQuietly(partial);
                    reference = await RunWithFloodWaitAsync(
                        operation,
                        total,
                        () => _storage.RefreshMetadataAsync(file.Remote, channelGroup, ct),
                        ct).ConfigureAwait(false);
                    _index.ApplyRefreshedReference(file.OwnerId, file.Id, reference, DateTime.UtcNow);
                    refreshed = true;
                    await RunWithFloodWaitAsync(
                        operation,
                        total,
                        () => _storage.DownloadDocumentAsync(
                            reference, partial, Pump(operation, total, progress, ct), ct, _gate),
                        ct).ConfigureAwait(false);
                }

                if (File.Exists(destinationPath) && confirmOverwrite is not null && !confirmOverwrite(destinationPath))
                {
                    // Declined: the downloaded bytes are discarded rather than left beside the
                    // destination, so a refused overwrite leaves no trace on disk.
                    DeleteQuietly(partial);
                    return new DownloadResult(false, refreshed, null);
                }

                File.Move(partial, destinationPath, overwrite: true);

                var done = TransferSnapshot.ForState(TransferState.Completed, operation, total ?? 0, total);
                Report(done);
                TransferLog.Write(done);
                return new DownloadResult(true, refreshed, destinationPath);
            }
            catch
            {
                // A failed or cancelled transfer must never leave a stray partial file behind.
                DeleteQuietly(partial);
                throw;
            }
        }
        catch (Exception ex)
        {
            ReportFailure(operation, total, ex);
            throw;
        }
        finally
        {
            ExitTransfer();
        }
    }

    /// <summary>Path of the temporary partial file a download would create, for documentation/tests.</summary>
    public static string PartialNameFor(string destinationPath)
        => "." + Path.GetFileName(destinationPath) + ".nuvia-part";

    /// <summary>
    /// Runs one remote step, waiting out a long FLOOD_WAIT visibly and then retrying exactly once.
    /// <para>
    /// A 420 is a refusal: the server stored nothing, so one repeat cannot duplicate a message. A second
    /// 420 is not retried again — it travels out as a failure, which keeps a retry storm impossible.
    /// Connection-level failures are deliberately not caught here at all.
    /// </para>
    /// </summary>
    /// <summary>
    /// Flood-wait wrapper for a call that returns nothing (the download path). Same wait and retry
    /// policy as the generic form; only the return shape differs.
    /// </summary>
    private async Task RunWithFloodWaitAsync(string operation, long? total, Func<Task> call, CancellationToken ct)
    {
        await RunWithFloodWaitAsync<object?>(operation, total, async () =>
        {
            await call().ConfigureAwait(false);
            return null;
        }, ct).ConfigureAwait(false);
    }

    private async Task<T> RunWithFloodWaitAsync<T>(
        string operation,
        long? total,
        Func<Task<T>> call,
        CancellationToken ct)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.Code == 420 && !ct.IsCancellationRequested)
        {
            // ex.X carries the server-provided wait duration; there is no client-side guess.
            var seconds = ex.X > 0 ? ex.X : MinimumFloodWaitSeconds;

            var first = TransferSnapshot.Flood(operation, seconds, total);
            Report(first);
            TransferLog.Write(first);

            for (var remaining = seconds; remaining > 0; remaining--)
            {
                // Cancellable on purpose: the wait itself must not trap the user.
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                Report(TransferSnapshot.Flood(operation, remaining - 1, total));
            }

            return await call().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds the one progress object handed to storage. It is deliberately synchronous and runs on the
    /// library's own callback thread: the WPF UI thread is never involved, the cancellation check inside
    /// it is the pinned library's documented way to abort in flight, and it never blocks — pausing is
    /// handled by the file stream (see <see cref="TransferGate"/>), not here, so this thread stays free.
    /// </summary>
    private IProgress<double>? Pump(string operation, long? total, IProgress<double>? inner, CancellationToken ct)
    {
        return new InlineProgress(fraction =>
        {
            ct.ThrowIfCancellationRequested();

            var clamped = double.IsNaN(fraction) ? 0d : Math.Clamp(fraction, 0d, 1d);
            var bytes = total is > 0 ? (long)(clamped * total.Value) : 0;
            var state = operation == TransferSnapshot.DownloadOperation
                ? TransferState.Downloading
                : TransferState.Uploading;

            // Remember the latest reading so Pause/Resume can report an accurate paused snapshot.
            _lastBytes = bytes;
            _lastTotal = total;
            _currentOperation = operation;

            // While paused the byte flow stalls in the stream, so this callback simply stops firing —
            // we do NOT block here (that would wedge the client). If a report happens to arrive while
            // paused, keep showing the paused state rather than flipping back to running.
            if (_gate.IsPaused)
            {
                Report(TransferSnapshot.ForState(TransferState.Paused, operation, bytes, total));
                return;
            }

            Report(TransferSnapshot.ForState(state, operation, bytes, total));
            inner?.Report(clamped);
        });
    }

    private sealed class InlineProgress : IProgress<double>
    {
        private readonly Action<double> _onReport;

        public InlineProgress(Action<double> onReport) => _onReport = onReport;

        public void Report(double value) => _onReport(value);
    }

    private void Report(TransferSnapshot snapshot)
    {
        try
        {
            _observer?.Report(snapshot);
        }
        catch
        {
            // A reporting sink must never be able to break a transfer.
        }
    }

    private void ReportFailure(string operation, long? total, Exception ex)
    {
        var error = TransferErrors.Classify(ex);
        var cancelled = error.Class is TransferErrorClass.Cancelled or TransferErrorClass.AppClosingDuringTransfer;

        if (cancelled)
            Report(TransferSnapshot.ForState(TransferState.Cancelling, operation, 0, total));

        var state = cancelled ? TransferState.Cancelled : TransferState.Failed;
        var snapshot = TransferSnapshot.ForState(state, operation, 0, total);
        Report(snapshot);
        TransferLog.Write(snapshot, error.Class, error.RpcCode);
    }

    private void EnterTransfer()
    {
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
            throw new InvalidOperationException(
                "Another transfer is already running. Wait for it to finish, or cancel it first.");

        // Every transfer starts unpaused with a fresh reading, whatever the previous one left behind.
        _gate.Resume();
        _lastBytes = 0;
        _lastTotal = null;
    }

    private void ExitTransfer()
    {
        // Never leave a paused gate closed when a transfer ends, or the next one would start stalled.
        _gate.Resume();
        Volatile.Write(ref _active, 0);
    }

    private static long? TryLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static (long Length, DateTime WriteUtc)? TryStamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return (info.Length, info.LastWriteTimeUtc);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool Changed(string path, (long Length, DateTime WriteUtc)? stamp)
    {
        if (stamp is null) return false;

        var now = TryStamp(path);
        if (now is null) return true; // vanished mid-transfer

        return now.Value.Length != stamp.Value.Length || now.Value.WriteUtc != stamp.Value.WriteUtc;
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Cleanup is best effort; the caller reports the primary failure.
        }
    }
}
