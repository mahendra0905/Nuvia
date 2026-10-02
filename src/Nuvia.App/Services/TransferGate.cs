using System;
using System.Threading;

namespace Nuvia.App.Services;

/// <summary>
/// A thread-safe pause switch shared between the coordinator (which flips it) and a
/// <see cref="PausableStream"/> (which honours it). Pausing genuinely stops the byte flow by making
/// the transfer's own <c>Read</c>/<c>Write</c> block until resumed — a slow stream is a condition
/// WTelegramClient tolerates, unlike blocking its progress callback (which wedges the client).
/// <para>Starts open (not paused). Safe to call from any thread.</para>
/// </summary>
public sealed class TransferGate
{
    // Set = open = not paused. Reset = closed = paused. Starts open.
    private readonly ManualResetEventSlim _gate = new(true);
    private volatile bool _paused;

    /// <summary>True while the gate is closed.</summary>
    public bool IsPaused => _paused;

    /// <summary>Closes the gate: subsequent <see cref="WaitWhilePaused"/> calls block until <see cref="Resume"/>.</summary>
    public void Pause()
    {
        _paused = true;
        _gate.Reset();
    }

    /// <summary>Opens the gate so a paused transfer continues from where it stopped.</summary>
    public void Resume()
    {
        _paused = false;
        _gate.Set();
    }

    /// <summary>
    /// Blocks the calling (transfer) thread while paused, waking in short slices so a cancel stays
    /// responsive. Throws <see cref="OperationCanceledException"/> if cancelled — so cancel-while-paused
    /// aborts the transfer cleanly instead of hanging.
    /// </summary>
    public void WaitWhilePaused(CancellationToken ct)
    {
        while (_paused)
        {
            ct.ThrowIfCancellationRequested();
            // Short, cancellable slices: never blocks indefinitely on a gate a rapid toggle reopened.
            _gate.Wait(150, ct);
        }
    }
}
