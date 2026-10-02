using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using Microsoft.Data.Sqlite;
using TL;

namespace Nuvia.App.Services;

/// <summary>
/// The failure classes Phase 5 names out loud. Each one maps to something the user can act on, and none
/// of them ever carries a local path, file name, phone number, code or message content.
/// </summary>
public enum TransferErrorClass
{
    None,
    Cancelled,
    AppClosingDuringTransfer,
    NetworkUnavailable,
    SessionRevoked,
    FileTooLarge,
    LocalFileMissingOrChanged,
    AccessDenied,
    DiskFull,
    RemoteMessageDeleted,
    StorageGroupUnavailable,
    IndexFailure,
    FloodLimitExceeded,
    Unknown,
}

/// <summary>One classified failure: the class, a safe message, and the RPC code when there was one.</summary>
public sealed record TransferError(TransferErrorClass Class, string UserMessage, int? RpcCode = null);

/// <summary>Thrown when a transfer stopped because a stop was requested (user cancel or app close).</summary>
public sealed class TransferCancelledException : OperationCanceledException
{
    public TransferCancelledException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Classifies transfer failures. Order matters: a full disk and a denied file both arrive as ordinary IO
/// failures, so they are named before the generic buckets.
/// </summary>
public static class TransferErrors
{
    // The two Windows errors a full disk surfaces as:
    //   ERROR_HANDLE_DISK_FULL (39  = 0x27) -> 0x80070027
    //   ERROR_DISK_FULL        (112 = 0x70) -> 0x80070070
    private const int HrHandleDiskFull = unchecked((int)0x80070027);
    private const int HrDiskFull = unchecked((int)0x80070070);

    private const string DiskFullMessage =
        "There is not enough free space on the destination disk. Free some space and try again.";
    private const string AccessDeniedMessage = "Windows denied access to the local file or folder.";
    private const string MissingFileMessage = "The local file is no longer there. Re-select it and try again.";

    public static bool IsCancellation(Exception? ex)
    {
        if (ex is null) return false;
        if (ex is OperationCanceledException) return true; // TransferCancelledException is one of these
        return ex is AggregateException aggregate && aggregate.Flatten().InnerExceptions.Any(IsCancellation);
    }

    public static TransferError Classify(Exception ex, bool appClosing = false)
    {
        if (IsCancellation(ex))
        {
            return appClosing
                ? new TransferError(TransferErrorClass.AppClosingDuringTransfer, "Nuvia closed while the transfer was running.")
                : new TransferError(TransferErrorClass.Cancelled, "Transfer cancelled.");
        }

        // A pre-flight size refusal. This is decided from the documented ceiling only; a real
        // over-limit is still confirmed by the server (FILE_PARTS_INVALID / FILE_TOO_BIG below).
        if (Find<FileExceedsAccountLimitException>(ex, _ => true) is { } tooLarge)
            return new TransferError(TransferErrorClass.FileTooLarge, tooLarge.Message);

        // The managed storage group is gone or access to it was refused. Never a prompt to recreate it.
        if (Find<StorageGroupUnavailableException>(ex, _ => true) is { } groupGone)
            return new TransferError(TransferErrorClass.StorageGroupUnavailable, groupGone.Message);

        if (Find<IOException>(ex, io => io.HResult == HrHandleDiskFull || io.HResult == HrDiskFull) is not null)
            return new TransferError(TransferErrorClass.DiskFull, DiskFullMessage);

        if (Find<UnauthorizedAccessException>(ex, _ => true) is not null)
            return new TransferError(TransferErrorClass.AccessDenied, AccessDeniedMessage);

        if (ex is FileNotFoundException or DirectoryNotFoundException)
            return new TransferError(TransferErrorClass.LocalFileMissingOrChanged, MissingFileMessage);

        if (Find<SqliteException>(ex, _ => true) is not null)
            return new TransferError(TransferErrorClass.IndexFailure, IndexFailure().UserMessage);

        if (Find<RpcException>(ex, _ => true) is { } rpc)
            return ClassifyRpc(rpc);

        if (Find<SocketException>(ex, _ => true) is not null || ex is TimeoutException)
            return new TransferError(TransferErrorClass.NetworkUnavailable,
                "The connection to Telegram failed. Check your network and try again — nothing was stored.");

        return new TransferError(TransferErrorClass.Unknown, "The transfer did not finish.");
    }

    /// <summary>Safe, user-facing text for any failure — built from the class, never from <c>ex.Message</c>.</summary>
    public static string Describe(Exception ex, bool appClosing = false) => Classify(ex, appClosing).UserMessage;

    /// <summary>
    /// The local index step. A failure here never means the remote copy is missing, so the message must
    /// never suggest re-uploading.
    /// </summary>
    public static TransferError IndexFailure()
        => new(TransferErrorClass.IndexFailure, "Nuvia could not write its local index.");

    private static TransferError ClassifyRpc(RpcException rpc)
    {
        var text = rpc.Message ?? string.Empty;

        if (rpc.Code == 420)
            return new TransferError(TransferErrorClass.FloodLimitExceeded,
                "Telegram is still rate-limiting this transfer. Try again later.", rpc.Code);

        if (rpc.Code == 401)
            return new TransferError(TransferErrorClass.SessionRevoked,
                "The Telegram session is no longer valid. Sign in again to continue.", rpc.Code);

        // An upload past the account's ceiling comes back as too many parts: the parts value must sit in
        // 1..upload_max_fileparts_* (https://core.telegram.org/api/files, checked 2026-09-24). The size
        // ceiling itself is server configuration, so Nuvia never hard-codes a byte figure.
        if (Has(text, "FILE_PARTS_INVALID") || Has(text, "FILE_TOO_BIG") || Has(text, "TOO_LARGE"))
            return new TransferError(TransferErrorClass.FileTooLarge,
                "Telegram refused the file because it is larger than this account's upload limit. Nothing was stored.",
                rpc.Code);

        if (Has(text, "MESSAGE_ID_INVALID") || Has(text, "MSG_ID_INVALID"))
            return new TransferError(TransferErrorClass.RemoteMessageDeleted,
                "That message is no longer in Saved Messages, so the file cannot be fetched.", rpc.Code);

        // The managed storage group can no longer be reached (left, deleted, or access revoked).
        if (Has(text, "CHANNEL_INVALID") || Has(text, "CHANNEL_PRIVATE")
            || Has(text, "CHANNEL_ID_INVALID") || Has(text, "PEER_ID_INVALID"))
            return new TransferError(TransferErrorClass.StorageGroupUnavailable,
                "The private storage group can no longer be reached. Nuvia will not recreate it — "
                + "choose Saved Messages, or create a new group.", rpc.Code);

        if (Has(text, "FILE_REFERENCE"))
            return new TransferError(TransferErrorClass.Unknown,
                "The stored Telegram file reference is no longer usable.", rpc.Code);

        return new TransferError(TransferErrorClass.Unknown, "Telegram refused the request.", rpc.Code);
    }

    private static bool Has(string haystack, string needle)
        => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static T? Find<T>(Exception? ex, Func<T, bool> predicate) where T : Exception
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is T typed && predicate(typed)) return typed;
        }

        return null;
    }
}

/// <summary>
/// Append-only, sanitised transfer log at <c>%LOCALAPPDATA%\Nuvia\logs\transfers.log</c>.
/// <para>
/// Every line is built from fixed fields only — state, operation token, byte counts, flood seconds, error
/// class, RPC code. No exception message, file name, local path, phone number, code or message content is
/// ever accepted, so none of them can reach the file.
/// </para>
/// </summary>
public static class TransferLog
{
    /// <summary>Once the log passes this size it is replaced rather than allowed to grow without bound.</summary>
    private const long MaxBytes = 1_048_576;

    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Nuvia",
        "logs",
        "transfers.log");

    public static void Write(
        TransferSnapshot snapshot,
        TransferErrorClass errorClass = TransferErrorClass.None,
        int? rpcCode = null)
    {
        try
        {
            var path = LogPath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                File.Delete(path);

            var total = snapshot.TotalBytes?.ToString(CultureInfo.InvariantCulture) ?? "-";
            var flood = snapshot.FloodWaitSecondsRemaining?.ToString(CultureInfo.InvariantCulture) ?? "-";
            var code = rpcCode?.ToString(CultureInfo.InvariantCulture) ?? "-";
            var stamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

            var line = string.Create(CultureInfo.InvariantCulture,
                $"{stamp} state={snapshot.State} op={snapshot.Operation} bytes={snapshot.BytesTransferred} total={total} flood={flood} error={errorClass} rpc={code}");

            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // Diagnostics must never take a transfer down.
        }
    }
}
