using VsChromiumMcp.Shared;

namespace VsChromiumMcp.Daemon;

internal static class Log {
    private static readonly object Lock = new();
    private static StreamWriter? _writer;
    private static string _logPath = "";

    public static void Init(string componentTag) {
        Paths.EnsureCreated();
        _logPath = Path.Combine(Paths.LogsDir, $"daemon-{DateTime.Now:yyyyMMdd}.log");
        var stream = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream) { AutoFlush = true };
        Info($"=== daemon started ({componentTag}) pid={Environment.ProcessId} log={_logPath} ===");
    }

    public static void Info(string msg) => Write("INFO ", msg);
    public static void Warn(string msg) => Write("WARN ", msg);
    public static void Error(string msg) => Write("ERROR", msg);
    public static void Error(Exception ex, string msg) => Write("ERROR", $"{msg}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    private static void Write(string level, string msg) {
        lock (Lock) {
            _writer?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {level} {msg}");
        }
    }
}
