using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using VsChromiumMcp.Shared;

namespace VsChromiumMcp.Daemon;

internal sealed class PipeServer {
    private readonly Operations _ops;
    private readonly CancellationToken _shutdownToken;

    public PipeServer(Operations ops, CancellationToken shutdownToken) {
        _ops = ops;
        _shutdownToken = shutdownToken;
    }

    public async Task RunAsync() {
        Log.Info($"pipe-server: listening on \\\\.\\pipe\\{Paths.PipeName}");
        while (!_shutdownToken.IsCancellationRequested) {
            NamedPipeServerStream? pipe = null;
            try {
                pipe = new NamedPipeServerStream(
                    Paths.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(_shutdownToken);
            } catch (OperationCanceledException) {
                pipe?.Dispose();
                return;
            } catch (Exception ex) {
                Log.Error(ex, "pipe-server: accept failed");
                pipe?.Dispose();
                await Task.Delay(500, _shutdownToken);
                continue;
            }

            _ = Task.Run(() => HandleClient(pipe), _shutdownToken);
        }
    }

    private async Task HandleClient(NamedPipeServerStream pipe) {
        var clientId = Guid.NewGuid().ToString("N").Substring(0, 8);
        Log.Info($"pipe-server[{clientId}]: client connected");
        try {
            using (pipe) {
                var reader = new StreamReader(pipe, Encoding.UTF8, false, 64 * 1024, leaveOpen: true);
                var writer = new StreamWriter(pipe, new UTF8Encoding(false), 64 * 1024, leaveOpen: true) { AutoFlush = false };

                while (!_shutdownToken.IsCancellationRequested && pipe.IsConnected) {
                    string? line;
                    try {
                        line = await reader.ReadLineAsync(_shutdownToken);
                    } catch (OperationCanceledException) { break; }
                    catch (IOException) { break; }

                    if (line == null) break;
                    if (line.Length == 0) continue;

                    var response = await HandleRequestAsync(line, clientId);
                    var json = JsonSerializer.Serialize(response, PipeJson.Options);
                    await writer.WriteLineAsync(json);
                    await writer.FlushAsync();
                }
            }
        } catch (Exception ex) {
            Log.Error(ex, $"pipe-server[{clientId}]: handler crashed");
        } finally {
            Log.Info($"pipe-server[{clientId}]: client disconnected");
        }
    }

    private async Task<PipeResponse> HandleRequestAsync(string line, string clientId) {
        PipeRequest? req;
        try {
            req = JsonSerializer.Deserialize<PipeRequest>(line, PipeJson.Options);
        } catch (Exception ex) {
            return new PipeResponse { Id = 0, Error = new PipeError { Message = "invalid request JSON", Details = ex.Message } };
        }
        if (req == null)
            return new PipeResponse { Id = 0, Error = new PipeError { Message = "empty request" } };

        Log.Info($"pipe-server[{clientId}]: req id={req.Id} method={req.Method}");
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
        requestCts.CancelAfter(TimeSpan.FromMinutes(10));

        try {
            object? result = req.Method switch {
                PipeMethods.Ping => new { status = "ok", version = typeof(PipeServer).Assembly.GetName().Version?.ToString() },
                PipeMethods.GetIndexStatus => _ops.GetIndexStatus(),
                PipeMethods.ListIndexedDirs => await _ops.ListIndexedDirsAsync(requestCts.Token),
                PipeMethods.EnsureIndexed => await _ops.EnsureIndexedAsync(ParseParams<EnsureIndexedParams>(req.Params) ?? throw new ArgumentException("missing params"), requestCts.Token),
                PipeMethods.SearchText => await _ops.SearchTextAsync(ParseParams<SearchTextParams>(req.Params) ?? throw new ArgumentException("missing params"), requestCts.Token),
                PipeMethods.SearchFiles => await _ops.SearchFilesAsync(ParseParams<SearchFilesParams>(req.Params) ?? throw new ArgumentException("missing params"), requestCts.Token),
                _ => throw new ArgumentException($"unknown method: {req.Method}"),
            };
            return new PipeResponse {
                Id = req.Id,
                Result = JsonSerializer.SerializeToElement(result, PipeJson.Options),
            };
        } catch (Exception ex) {
            Log.Error(ex, $"pipe-server[{clientId}]: method {req.Method} failed");
            return new PipeResponse {
                Id = req.Id,
                Error = new PipeError { Message = ex.Message, Details = ex.GetType().FullName },
            };
        }
    }

    private static T? ParseParams<T>(JsonElement? element) where T : class {
        if (element == null) return null;
        return element.Value.Deserialize<T>(PipeJson.Options);
    }
}
