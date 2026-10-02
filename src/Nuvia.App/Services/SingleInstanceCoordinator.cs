using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nuvia.App.QuickUpload;

namespace Nuvia.App.Services;

/// <summary>
/// Guarantees one Nuvia process per user and carries a shell-launched quick-upload request to it.
/// <para>
/// WTelegramClient's <c>.session</c> file cannot be opened safely by two processes at once (see the
/// note in <c>App.xaml.cs</c>). So every launch first tries to become the single <em>primary</em>
/// instance via a per-user <see cref="Mutex"/>. The primary owns the session and runs a local
/// named-pipe server; a second launch (for example from the Explorer "Nuvia" menu) hands its
/// <see cref="QuickUploadRequest"/> to the primary over that pipe and exits immediately, so the session
/// is never touched by a second process.
/// </para>
/// <para>
/// The pipe is local-only and carries a <see cref="QuickUploadRequest"/> — a file path plus a
/// destination hint, nothing sensitive. The server stream is ACL'd to the current user so no other
/// account on the machine can inject a request.
/// </para>
/// </summary>
public sealed class SingleInstanceCoordinator : IDisposable
{
    // Session-local namespace => one primary per interactive user session.
    private const string MutexName = @"Local\Nuvia.SingleInstance";
    private const string PipeName = "Nuvia.QuickUpload";

    // A request payload is a few hundred bytes at most; cap the read so a hostile writer cannot flood us.
    private const int MaxPayloadBytes = 64 * 1024;

    private Mutex? _mutex;
    private bool _ownsMutex;
    private CancellationTokenSource? _serverCts;
    private Task? _serverLoop;
    private Action<QuickUploadRequest>? _onRequest;
    private bool _disposed;

    /// <summary>
    /// Attempts to become the primary instance. Returns true if this process now owns the per-user
    /// mutex (and should run the pipe server); false if another instance already owns it.
    /// </summary>
    public bool TryAcquirePrimary()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew)
        {
            _ownsMutex = true;
            return true;
        }

        // The mutex already existed. It may have been abandoned by a crashed previous owner, so try to
        // take it without blocking.
        try
        {
            if (_mutex.WaitOne(TimeSpan.Zero))
            {
                _ownsMutex = true;
                return true;
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous owner exited without releasing it; ownership has passed to us.
            _ownsMutex = true;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Starts the named-pipe server on a background task. Each accepted connection is parsed into a
    /// <see cref="QuickUploadRequest"/> and passed to <paramref name="onRequest"/>. The callback runs on a
    /// background thread — callers marshal to the UI thread themselves.
    /// </summary>
    public void StartServer(Action<QuickUploadRequest> onRequest)
    {
        if (!_ownsMutex)
            throw new InvalidOperationException("Only the primary instance may run the pipe server.");

        _onRequest = onRequest ?? throw new ArgumentNullException(nameof(onRequest));
        _serverCts = new CancellationTokenSource();
        _serverLoop = Task.Run(() => ServerLoopAsync(_serverCts.Token));
    }

    /// <summary>
    /// Connects to the running primary instance and hands it the request. Returns false (never throwing)
    /// when no primary is reachable within <paramref name="timeout"/>, so a failed handoff simply means
    /// this launcher exits without doing anything.
    /// </summary>
    public async Task<bool> TrySendAsync(QuickUploadRequest request, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var bytes = Encoding.UTF8.GetBytes(request.Serialize());
        var waitMs = (int)(timeout ?? TimeSpan.FromSeconds(5)).TotalMilliseconds;

        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            await client.ConnectAsync(waitMs).ConfigureAwait(false);
            await client.WriteAsync(bytes.AsMemory()).ConfigureAwait(false);
            await client.FlushAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            // No secrets: only the exception type, never the payload or any path content.
            DiagnosticLog.Write("quickupload.log", "pipe client send failed: " + ex.GetType().Name);
            return false;
        }
    }

    private async Task ServerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = CreateServerStream();
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                var payload = await ReadPayloadAsync(server, ct).ConfigureAwait(false);
                if (QuickUploadRequest.TryParse(payload, out var request))
                {
                    try
                    {
                        _onRequest?.Invoke(request);
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLog.Write("quickupload.log", "server dispatch failed: " + ex.GetType().Name);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("quickupload.log", "pipe server error: " + ex.GetType().Name);
                try
                {
                    // Brief backoff so a persistent failure cannot spin the CPU.
                    await Task.Delay(200, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private static NamedPipeServerStream CreateServerStream()
    {
        try
        {
            var security = new PipeSecurity();
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User is { } sid)
            {
                security.AddAccessRule(new PipeAccessRule(
                    sid,
                    PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                    AccessControlType.Allow));
            }

            return NamedPipeServerStreamAcl.Create(
                PipeName,
                PipeDirection.In,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                inBufferSize: 0,
                outBufferSize: 0,
                security);
        }
        catch
        {
            // If ACL creation is unavailable for any reason, fall back to the default (still local) DACL.
            return new NamedPipeServerStream(
                PipeName,
                PipeDirection.In,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
        }
    }

    private static async Task<string> ReadPayloadAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await server.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxPayloadBytes)
                break;
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            _serverCts?.Cancel();
        }
        catch
        {
            // Best effort.
        }

        try
        {
            // Give the accept loop a moment to unwind so the pipe is released cleanly.
            _serverLoop?.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch
        {
            // Ignore: shutdown must never throw.
        }

        _serverCts?.Dispose();

        try
        {
            if (_mutex is { } mutex)
            {
                if (_ownsMutex)
                    mutex.ReleaseMutex();
                mutex.Dispose();
            }
        }
        catch
        {
            // Ignore: an already-released or abandoned mutex must not break shutdown.
        }
    }
}
