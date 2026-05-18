using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using VsChromiumMcp.Shared;

namespace VsChromiumMcp;

internal static class DaemonBootstrap {
    public static async Task<DaemonClient> EnsureRunningAsync(CancellationToken ct) {
        // Fast path: try to connect.
        try {
            return await DaemonClient.ConnectAsync(TimeSpan.FromMilliseconds(500), ct);
        } catch {
            // Continue to spawn.
        }

        var daemonExe = LocateDaemonExe()
            ?? throw new FileNotFoundException("Could not find VsChromiumMcp.Daemon.exe. Run scripts/build.ps1 first.");

        // Detached spawn so the daemon outlives this MCP process.
        // Trick: `cmd /c start "" /b daemon.exe` returns immediately; the daemon is reparented.
        var psi = new ProcessStartInfo("cmd.exe", $"/c start \"VsChromiumMcp.Daemon\" /b \"{daemonExe}\"") {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(daemonExe)!,
        };
        try {
            using var p = Process.Start(psi);
            // cmd.exe exits immediately after handing off; we don't wait on the daemon process itself.
        } catch (Exception ex) {
            throw new InvalidOperationException($"Failed to spawn daemon at {daemonExe}", ex);
        }

        // Poll for the pipe to appear.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline) {
            ct.ThrowIfCancellationRequested();
            try {
                return await DaemonClient.ConnectAsync(TimeSpan.FromMilliseconds(500), ct);
            } catch (Exception ex) {
                lastError = ex;
                await Task.Delay(250, ct);
            }
        }
        throw new TimeoutException(
            "Daemon failed to become ready within 20s. See logs at " + Paths.LogsDir +
            (lastError != null ? $". Last error: {lastError.Message}" : ""));
    }

    private static string? LocateDaemonExe() {
        var binDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;

        // 1. Side-by-side with the MCP executable (preferred install layout).
        var sideBySide = Path.Combine(binDir, "VsChromiumMcp.Daemon.exe");
        if (File.Exists(sideBySide)) return sideBySide;

        // 2. Dev layout: walk up to repo root, then look for the daemon's bin output.
        var dir = new DirectoryInfo(binDir);
        while (dir != null) {
            if (File.Exists(Path.Combine(dir.FullName, "VsChromiumMcp.sln")) ||
                File.Exists(Path.Combine(dir.FullName, "VsChromiumMcp.slnx"))) {
                foreach (var cfg in new[] { "Release", "Debug" }) {
                    var devPath = Path.Combine(
                        dir.FullName, "src", "VsChromiumMcp.Daemon",
                        "bin", cfg, "net8.0-windows", "VsChromiumMcp.Daemon.exe");
                    if (File.Exists(devPath)) return devPath;
                }
                return null;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
