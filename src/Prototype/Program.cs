using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ProtoBuf;
using VsChromium.Core.Ipc;
using VsChromium.Core.Ipc.TypedMessages;

namespace VsChromiumMcp.Prototype;

internal static class Program {
    private const string ServerExe = @"X:\dev\vs-chromium-mcp\vendor\vs-chromium\Binaries\Release\VsChromium.Server.exe";

    private static async Task<int> Main(string[] args) {
        if (!File.Exists(ServerExe)) {
            Console.Error.WriteLine($"Server.exe not found at {ServerExe}");
            return 1;
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Console.WriteLine($"[host] listening on 127.0.0.1:{port}");

        var psi = new ProcessStartInfo(ServerExe, port.ToString()) {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var server = Process.Start(psi)!;
        server.OutputDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine($"[server stdout] {e.Data}"); };
        server.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine($"[server stderr] {e.Data}"); };
        server.BeginOutputReadLine();
        server.BeginErrorReadLine();
        Console.WriteLine($"[host] launched server pid={server.Id}");

        using var acceptCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = await listener.AcceptTcpClientAsync(acceptCts.Token);
        listener.Stop();
        client.NoDelay = true;
        Console.WriteLine($"[host] server connected from {client.Client.RemoteEndPoint}");

        var stream = client.GetStream();

        // First message from server should be HelloWorldProtocol.Response.
        var hello = Serializer.DeserializeWithLengthPrefix<IpcMessage>(stream, PrefixStyle.Base128);
        Console.WriteLine($"[host] received: type={hello.GetType().Name} requestId={hello.RequestId} protocol={hello.Protocol} data={hello.Data}");

        if (hello is IpcResponse helloResponse && helloResponse.Protocol == IpcProtocols.Hello) {
            Console.WriteLine("[host] handshake OK");
        } else {
            Console.WriteLine("[host] unexpected first message");
        }

        // Try a typed request: ask for the file system tree.
        var req = new IpcRequest {
            RequestId = 2,
            Protocol = IpcProtocols.TypedMessage,
            Data = new GetFileSystemRequest(),
        };
        Console.WriteLine("[host] sending GetFileSystemRequest");
        Serializer.SerializeWithLengthPrefix(stream, req, PrefixStyle.Base128);

        // Drain incoming messages for a few seconds; print everything that arrives.
        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try {
            while (!readCts.Token.IsCancellationRequested) {
                var msg = await Task.Run(() => Serializer.DeserializeWithLengthPrefix<IpcMessage>(stream, PrefixStyle.Base128), readCts.Token);
                if (msg == null) {
                    Console.WriteLine("[host] stream closed");
                    break;
                }
                Console.WriteLine($"[host] recv: type={msg.GetType().Name} requestId={msg.RequestId} protocol={msg.Protocol} dataType={msg.Data?.GetType().Name} data={msg.Data}");
            }
        } catch (OperationCanceledException) {
            Console.WriteLine("[host] read timeout reached, exiting cleanly");
        } catch (Exception ex) {
            Console.WriteLine($"[host] read error: {ex.GetType().Name}: {ex.Message}");
        }

        try { client.Close(); } catch { }
        if (!server.HasExited) {
            try { server.Kill(true); } catch { }
        }
        Console.WriteLine($"[host] server exited code={server.ExitCode}");
        return 0;
    }
}
