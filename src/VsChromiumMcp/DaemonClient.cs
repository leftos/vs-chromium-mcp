using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using VsChromiumMcp.Shared;

namespace VsChromiumMcp;

public sealed class DaemonClient : IAsyncDisposable {
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _nextId;

    public bool IsConnected => _pipe.IsConnected;

    private DaemonClient(NamedPipeClientStream pipe) {
        _pipe = pipe;
        _reader = new StreamReader(pipe, Encoding.UTF8, false, 64 * 1024, leaveOpen: true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), 64 * 1024, leaveOpen: true) { AutoFlush = false };
    }

    public static async Task<DaemonClient> ConnectAsync(TimeSpan timeout, CancellationToken ct) {
        var pipe = new NamedPipeClientStream(".", Paths.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try {
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds, ct);
        } catch {
            pipe.Dispose();
            throw;
        }
        return new DaemonClient(pipe);
    }

    public async Task<TResult> CallAsync<TResult>(string method, object? @params, CancellationToken ct) {
        var id = Interlocked.Increment(ref _nextId);
        var paramsElement = @params == null ? (JsonElement?)null
            : JsonSerializer.SerializeToElement(@params, PipeJson.Options);
        var req = new PipeRequest { Id = id, Method = method, Params = paramsElement };
        var requestJson = JsonSerializer.Serialize(req, PipeJson.Options);

        await _gate.WaitAsync(ct);
        try {
            await _writer.WriteLineAsync(requestJson);
            await _writer.FlushAsync();
            var line = await _reader.ReadLineAsync(ct);
            if (line == null) throw new IOException("daemon closed pipe");
            var resp = JsonSerializer.Deserialize<PipeResponse>(line, PipeJson.Options)
                ?? throw new IOException("daemon returned empty response");
            if (resp.Error != null)
                throw new InvalidOperationException(resp.Error.Message + (resp.Error.Details != null ? $" ({resp.Error.Details})" : ""));
            if (resp.Result == null) throw new IOException("daemon returned null result");
            return resp.Result.Value.Deserialize<TResult>(PipeJson.Options)
                ?? throw new IOException("daemon returned non-deserializable result");
        } finally {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync() {
        try { await _writer.FlushAsync(); } catch { /* ignore */ }
        _reader.Dispose();
        _writer.Dispose();
        _pipe.Dispose();
        _gate.Dispose();
    }
}
