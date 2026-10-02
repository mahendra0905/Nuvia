using System;

namespace Nuvia.App.Services;

/// <summary>
/// The states one transfer moves through. <see cref="Idle"/> is the resting state; every other value
/// is something the status bar can show while a transfer is running or has just finished.
/// </summary>
public enum TransferState
{
    /// <summary>No transfer is running.</summary>
    Idle,

    /// <summary>Checking the local file / destination before any bytes move.</summary>
    Preparing,

    /// <summary>Bytes are going up to Telegram.</summary>
    Uploading,

    /// <summary>Bytes are coming down from Telegram.</summary>
    Downloading,

    /// <summary>The user paused the transfer; no bytes are moving, but it can be resumed.</summary>
    Paused,

    /// <summary>Telegram asked for a wait before the retry (FLOOD_WAIT / FLOOD_PREMIUM_WAIT).</summary>
    WaitingForFloodLimit,

    /// <summary>A stop was requested and cleanup is in progress.</summary>
    Cancelling,

    /// <summary>Finished successfully.</summary>
    Completed,

    /// <summary>Finished with an error — classified by <see cref="TransferErrors"/>.</summary>
    Failed,

    /// <summary>Stopped because a stop was requested (user cancel or app close).</summary>
    Cancelled,
}

/// <summary>
/// One immutable progress reading, carried by a transfer's single <see cref="IProgress{T}"/> channel.
/// <para>
/// A snapshot is deliberately free of file names, local paths and remote identifiers, so it is safe to
/// show, to log, and to hand to any thread.
/// </para>
/// </summary>
public sealed record TransferSnapshot(
    TransferState State,
    string Operation,
    long BytesTransferred = 0,
    long? TotalBytes = null,
    double? Fraction = null,
    int? FloodWaitSecondsRemaining = null)
{
    /// <summary>Fixed operation token for an upload. Never a file name.</summary>
    public const string UploadOperation = "Upload";

    /// <summary>Fixed operation token for a download. Never a file name.</summary>
    public const string DownloadOperation = "Download";

    /// <summary>Fraction 0..1 when the total is known; null when a percentage would be meaningless.</summary>
    public static double? FractionOf(long bytes, long? total)
        => total is > 0 ? Math.Clamp((double)bytes / total.Value, 0d, 1d) : null;

    /// <summary>A snapshot for a state change with no byte reading of its own.</summary>
    public static TransferSnapshot ForState(TransferState state, string operation, long bytes = 0, long? total = null)
        => new(state, operation, bytes, total, FractionOf(bytes, total));

    /// <summary>A snapshot for the flood-wait countdown.</summary>
    public static TransferSnapshot Flood(string operation, int secondsRemaining, long? total = null)
        => new(TransferState.WaitingForFloodLimit, operation, 0, total, null, secondsRemaining);
}
