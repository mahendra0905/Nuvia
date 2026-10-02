using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Nuvia.App.Services;

/// <summary>
/// Wraps a stream so a <see cref="TransferGate"/> can pause the byte flow through it. Before every
/// read (upload) or write (download) it waits while the gate is closed. This back-pressures
/// WTelegramClient's own transfer loop — a legitimate "slow stream" condition — instead of blocking
/// its progress callback, which would wedge the client and hang the app.
/// <para>
/// Only the transfer-direction operations gate; metadata (length, position, seek) passes straight
/// through so the library can still size and position the file.
/// </para>
/// </summary>
public sealed class PausableStream : Stream
{
    private readonly Stream _inner;
    private readonly TransferGate _gate;
    private readonly CancellationToken _ct;
    private readonly bool _leaveOpen;

    public PausableStream(Stream inner, TransferGate gate, CancellationToken ct, bool leaveOpen)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _ct = ct;
        _leaveOpen = leaveOpen;
    }

    // ---- gated transfer operations -------------------------------------------------

    public override int Read(byte[] buffer, int offset, int count)
    {
        _gate.WaitWhilePaused(_ct);
        return _inner.Read(buffer, offset, count);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _gate.WaitWhilePaused(_ct);
        return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        _gate.WaitWhilePaused(_ct);
        return await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _gate.WaitWhilePaused(_ct);
        _inner.Write(buffer, offset, count);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _gate.WaitWhilePaused(_ct);
        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        _gate.WaitWhilePaused(_ct);
        await _inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }

    // ---- pass-through --------------------------------------------------------------

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => _inner.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
            _inner.Dispose();

        base.Dispose(disposing);
    }
}
