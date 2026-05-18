using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ProtoBuf;
using VsChromium.Core.Ipc;
using VsChromium.Core.Ipc.TypedMessages;

namespace VsChromiumMcp.Daemon;

internal sealed class VsChromiumServerHost : IDisposable {
    private readonly string _serverExePath;
    private readonly object _writeLock = new();
    private TcpListener? _listener;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private Process? _serverProcess;
    private Thread? _readerThread;
    private long _nextRequestId = 100;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<TypedResponse>> _pending = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private volatile bool _disposed;

    public event Action<TypedEvent>? TypedEventReceived;

    public VsChromiumServerHost(string serverExePath) {
        _serverExePath = serverExePath;
    }

    public async Task StartAsync(CancellationToken ct) {
        if (!File.Exists(_serverExePath))
            throw new FileNotFoundException($"vs-chromium server not found", _serverExePath);

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Log.Info($"server-host: listening on 127.0.0.1:{port}");

        var psi = new ProcessStartInfo(_serverExePath, port.ToString()) {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(_serverExePath)!,
        };
        _serverProcess = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null for vs-chromium Server.exe");
        _serverProcess.OutputDataReceived += (_, e) => { if (e.Data != null) Log.Info($"[vs-chromium-out] {e.Data}"); };
        _serverProcess.ErrorDataReceived  += (_, e) => { if (e.Data != null) Log.Warn($"[vs-chromium-err] {e.Data}"); };
        _serverProcess.BeginOutputReadLine();
        _serverProcess.BeginErrorReadLine();
        Log.Info($"server-host: spawned vs-chromium server pid={_serverProcess.Id}");

        using var acceptTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        acceptTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        _client = await _listener.AcceptTcpClientAsync(acceptTimeout.Token);
        _listener.Stop();
        _client.NoDelay = true;
        _stream = _client.GetStream();
        Log.Info("server-host: vs-chromium server connected back");

        // First message must be the hello response.
        var hello = Serializer.DeserializeWithLengthPrefix<IpcMessage>(_stream, PrefixStyle.Base128);
        if (hello is not IpcResponse helloResp || helloResp.Protocol != IpcProtocols.Hello)
            throw new InvalidOperationException($"vs-chromium handshake failed: {hello?.GetType().Name} protocol={hello?.Protocol}");
        Log.Info("server-host: handshake complete");

        _readerThread = new Thread(ReaderLoop) { IsBackground = true, Name = "vs-chromium-reader" };
        _readerThread.Start();
    }

    private void ReaderLoop() {
        try {
            while (!_shutdownCts.IsCancellationRequested) {
                IpcMessage? msg;
                try {
                    msg = Serializer.DeserializeWithLengthPrefix<IpcMessage>(_stream!, PrefixStyle.Base128);
                } catch (Exception ex) when (_shutdownCts.IsCancellationRequested) {
                    Log.Info($"server-host: reader stopping after shutdown: {ex.GetType().Name}");
                    return;
                }
                if (msg == null) {
                    Log.Warn("server-host: stream returned null - server disconnected");
                    FailAllPending(new IOException("vs-chromium server disconnected"));
                    return;
                }
                DispatchIncoming(msg);
            }
        } catch (Exception ex) {
            Log.Error(ex, "server-host: reader loop crashed");
            FailAllPending(ex);
        }
    }

    private void DispatchIncoming(IpcMessage msg) {
        if (msg is not IpcResponse resp) {
            Log.Warn($"server-host: unexpected non-response message type={msg.GetType().Name}");
            return;
        }

        // Errors are delivered as IpcResponse with Protocol="exception" OR with Data=ErrorResponse.
        if (resp.Protocol == IpcProtocols.Exception || resp.Data is ErrorResponse) {
            var err = resp.Data as ErrorResponse;
            var errMsg = err == null
                ? "vs-chromium server returned exception protocol with no ErrorResponse data"
                : FlattenError(err);
            if (err?.StackTrace != null) Log.Info($"server-host: error stack: {err.StackTrace}");
            if (_pending.TryRemove(resp.RequestId, out var tcs))
                tcs.TrySetException(new InvalidOperationException(errMsg));
            else
                Log.Warn($"server-host: unsolicited error response: {errMsg}");
            return;
        }

        // Typed message: either a TypedResponse to a pending request, or a TypedEvent.
        if (resp.Data is TypedEvent typedEvent) {
            try { TypedEventReceived?.Invoke(typedEvent); }
            catch (Exception ex) { Log.Error(ex, "server-host: event handler threw"); }
            return;
        }

        if (resp.Data is TypedResponse typedResp) {
            if (_pending.TryRemove(resp.RequestId, out var tcs))
                tcs.TrySetResult(typedResp);
            else
                Log.Warn($"server-host: response for unknown request id={resp.RequestId} type={typedResp.GetType().Name}");
            return;
        }

        Log.Warn($"server-host: unhandled response data type={resp.Data?.GetType().Name}");
    }

    private static string FlattenError(ErrorResponse err) {
        var parts = new List<string>();
        for (var cur = err; cur != null; cur = cur.InnerError) {
            parts.Add($"{cur.FullTypeName}: {cur.Message}");
        }
        return string.Join(" -> ", parts);
    }

    public async Task<TResponse> SendAsync<TResponse>(TypedRequest request, CancellationToken ct)
        where TResponse : TypedResponse {
        if (_disposed) throw new ObjectDisposedException(nameof(VsChromiumServerHost));
        if (_stream == null) throw new InvalidOperationException("server not started");

        var requestId = Interlocked.Increment(ref _nextRequestId);
        var envelope = new IpcRequest {
            RequestId = requestId,
            Protocol = IpcProtocols.TypedMessage,
            Data = request,
        };
        var tcs = new TaskCompletionSource<TypedResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;

        try {
            lock (_writeLock) {
                Serializer.SerializeWithLengthPrefix(_stream, envelope, PrefixStyle.Base128);
            }
        } catch (Exception ex) {
            _pending.TryRemove(requestId, out _);
            throw new IOException("vs-chromium server write failed", ex);
        }

        using var reg = ct.Register(() => {
            if (_pending.TryRemove(requestId, out var pending))
                pending.TrySetCanceled(ct);
        });
        var resp = await tcs.Task.ConfigureAwait(false);
        if (resp is not TResponse typed)
            throw new InvalidOperationException(
                $"vs-chromium response type mismatch: expected {typeof(TResponse).Name} got {resp.GetType().Name}");
        return typed;
    }

    private void FailAllPending(Exception ex) {
        foreach (var kvp in _pending.ToArray()) {
            if (_pending.TryRemove(kvp.Key, out var tcs))
                tcs.TrySetException(ex);
        }
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        try { _shutdownCts.Cancel(); } catch { /* ignore */ }
        try { _stream?.Close(); } catch { /* ignore */ }
        try { _client?.Close(); } catch { /* ignore */ }
        try { _listener?.Stop(); } catch { /* ignore */ }
        if (_serverProcess is { HasExited: false }) {
            try { _serverProcess.Kill(true); } catch { /* ignore */ }
        }
        FailAllPending(new ObjectDisposedException(nameof(VsChromiumServerHost)));
        _shutdownCts.Dispose();
    }
}
