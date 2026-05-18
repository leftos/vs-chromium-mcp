using System.Reflection;
using VsChromiumMcp.Shared;

namespace VsChromiumMcp.Daemon;

internal static class Program {
    private const string MutexName = @"Global\VsChromiumMcp.Daemon.SingleInstance";

    public static async Task<int> Main(string[] args) {
        Paths.EnsureCreated();
        Log.Init("daemon");

        string? serverExe = null;
        foreach (var a in args) {
            if (a.StartsWith("--server-exe=", StringComparison.OrdinalIgnoreCase))
                serverExe = a.Substring("--server-exe=".Length).Trim('"');
        }
        serverExe ??= ResolveDefaultServerExe();

        if (!File.Exists(serverExe)) {
            Log.Error($"daemon: VsChromium.Server.exe not found at {serverExe}");
            return 2;
        }

        using var mutex = new Mutex(initiallyOwned: false, MutexName, out _);
        bool gotMutex;
        try { gotMutex = mutex.WaitOne(TimeSpan.Zero, false); }
        catch (AbandonedMutexException) { gotMutex = true; }
        if (!gotMutex) {
            Log.Info("daemon: another instance is running; exiting cleanly");
            return 0;
        }

        try {
            await File.WriteAllTextAsync(Paths.DaemonPidFile, Environment.ProcessId.ToString());
        } catch (Exception ex) {
            Log.Warn($"daemon: pidfile write failed: {ex.Message}");
        }

        using var shutdownCts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdownCts.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdownCts.Cancel();

        using var serverHost = new VsChromiumServerHost(serverExe);
        try {
            await serverHost.StartAsync(shutdownCts.Token);
        } catch (Exception ex) {
            Log.Error(ex, "daemon: failed to start vs-chromium server");
            return 3;
        }

        var registry = new ProjectRegistry();
        registry.Load();

        var ops = new Operations(serverHost, registry);
        _ = Task.Run(async () => {
            try { await ops.ReindexKnownProjectsAsync(shutdownCts.Token); }
            catch (Exception ex) { Log.Error(ex, "daemon: reindex failed"); }
        }, shutdownCts.Token);

        try {
            await File.WriteAllTextAsync(Paths.DaemonReadyFile, DateTime.UtcNow.ToString("O"));
        } catch { /* ignore */ }

        var pipeServer = new PipeServer(ops, shutdownCts.Token);
        try {
            await pipeServer.RunAsync();
        } catch (Exception ex) {
            Log.Error(ex, "daemon: pipe server crashed");
        } finally {
            Log.Info("daemon: shutting down");
            ops.Dispose();
            try { File.Delete(Paths.DaemonPidFile); } catch { /* ignore */ }
            try { File.Delete(Paths.DaemonReadyFile); } catch { /* ignore */ }
            try { mutex.ReleaseMutex(); } catch { /* ignore */ }
        }
        return 0;
    }

    private static string ResolveDefaultServerExe() {
        // Prefer same install dir as the daemon binary.
        var binDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var candidate = Path.Combine(binDir, "VsChromium.Server.exe");
        if (File.Exists(candidate)) return candidate;

        // Fallback: the vendor build output.
        var repoRoot = FindRepoRoot(binDir);
        if (repoRoot != null) {
            var vendor = Path.Combine(repoRoot, "vendor", "vs-chromium", "Binaries", "Release", "VsChromium.Server.exe");
            if (File.Exists(vendor)) return vendor;
        }
        return candidate;
    }

    private static string? FindRepoRoot(string start) {
        var dir = new DirectoryInfo(start);
        while (dir != null) {
            if (File.Exists(Path.Combine(dir.FullName, "VsChromiumMcp.sln")) ||
                File.Exists(Path.Combine(dir.FullName, "VsChromiumMcp.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
