using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using VsChromiumMcp.Shared;

namespace VsChromiumMcp;

[McpServerToolType]
public sealed class VsChromiumTools {
    private readonly DaemonHandle _handle;

    public VsChromiumTools(DaemonHandle handle) {
        _handle = handle;
    }

    [McpServerTool(Name = "vs_chromium_search_text")]
    [Description(
        "Fast indexed full-text search across all indexed directories. Returns matches with file path, " +
        "line, column, and a short snippet. Prefer this over filesystem grep when the target tree is " +
        "already in the vs-chromium index, or call vs_chromium_ensure_indexed first to add it.")]
    public async Task<string> SearchTextAsync(
        [Description("The text to search for. Treated as a literal substring unless 'regex' is true.")] string pattern,
        [Description("Optional substring or glob (e.g. '*.cs', 'src/Foo*') filtering matching files.")] string? file_filter = null,
        [Description("Case-sensitive match.")] bool match_case = false,
        [Description("Match the pattern only when it is a complete word (alphanumerics/underscore on both sides count as the same word).")] bool whole_word = false,
        [Description("Interpret 'pattern' as a regular expression (RE2 syntax).")] bool regex = false,
        [Description("Maximum number of matches to return. Capped at 5000.")] int max_results = 500,
        CancellationToken cancellationToken = default) {

        var result = await _handle.CallAsync<SearchTextResult>(PipeMethods.SearchText, new SearchTextParams {
            Pattern = pattern,
            FileFilter = file_filter,
            MatchCase = match_case,
            WholeWord = whole_word,
            Regex = regex,
            MaxResults = Math.Clamp(max_results, 1, 5000),
        }, cancellationToken);

        var sb = new StringBuilder();
        sb.Append($"vs-chromium search: {result.HitCount} hits in {result.SearchedFileCount} of {result.TotalFileCount} files");
        if (result.Truncated) sb.Append(" (truncated)");
        sb.AppendLine();
        if (result.Matches.Count == 0) {
            sb.AppendLine("(no matches returned)");
            return sb.ToString();
        }
        sb.AppendLine();
        foreach (var m in result.Matches) {
            sb.Append(m.FilePath).Append(':').Append(m.Line).Append(':').Append(m.Column).Append(": ");
            sb.AppendLine(m.Text);
        }
        return sb.ToString();
    }

    [McpServerTool(Name = "vs_chromium_search_files")]
    [Description(
        "Fast filename search across all indexed directories. Returns matching file paths. Use this " +
        "to locate files by name pattern without scanning the filesystem.")]
    public async Task<string> SearchFilesAsync(
        [Description("Filename or path fragment to search for (matched against absolute paths).")] string pattern,
        [Description("Case-sensitive match.")] bool match_case = false,
        [Description("Interpret 'pattern' as a regular expression (RE2 syntax).")] bool regex = false,
        [Description("Maximum number of paths to return. Capped at 5000.")] int max_results = 500,
        CancellationToken cancellationToken = default) {

        var result = await _handle.CallAsync<SearchFilesResult>(PipeMethods.SearchFiles, new SearchFilesParams {
            Pattern = pattern,
            MatchCase = match_case,
            Regex = regex,
            MaxResults = Math.Clamp(max_results, 1, 5000),
        }, cancellationToken);

        var sb = new StringBuilder();
        sb.Append($"vs-chromium file search: {result.HitCount} of {result.TotalFileCount} files");
        if (result.Truncated) sb.Append(" (truncated)");
        sb.AppendLine();
        sb.AppendLine();
        foreach (var f in result.Files) sb.AppendLine(f);
        return sb.ToString();
    }

    [McpServerTool(Name = "vs_chromium_ensure_indexed")]
    [Description(
        "Register a directory with the vs-chromium indexer. Drops a vs-chromium-project.txt (with sensible " +
        "defaults) if the directory does not already have one, then waits for the initial scan to complete. " +
        "Subsequent searches will include files under this path. Call this once per repo before searching.")]
    public async Task<string> EnsureIndexedAsync(
        [Description("Absolute path to the directory to index (typically a repo root).")] string path,
        [Description("If true, block until the directory is fully indexed. Defaults to true.")] bool wait = true,
        [Description("Seconds to wait for indexing to complete (default 180).")] int wait_timeout_seconds = 180,
        CancellationToken cancellationToken = default) {

        var result = await _handle.CallAsync<EnsureIndexedResult>(PipeMethods.EnsureIndexed, new EnsureIndexedParams {
            Path = path,
            Wait = wait,
            WaitTimeoutSeconds = wait_timeout_seconds,
        }, cancellationToken);

        var sb = new StringBuilder();
        sb.AppendLine($"Path: {result.Path}");
        sb.AppendLine($"Project file created: {result.ProjectFileCreated}");
        sb.AppendLine($"Already in registry: {result.AlreadyKnown}");
        sb.AppendLine($"Indexed: {result.Indexed}");
        if (result.FileCount > 0) sb.AppendLine($"File count: {result.FileCount}");
        sb.AppendLine($"Wait time: {result.WaitedSeconds:F2}s");
        if (!result.Indexed) sb.AppendLine("Note: index not yet ready; retry vs_chromium_status in a few seconds.");
        return sb.ToString();
    }

    [McpServerTool(Name = "vs_chromium_list_indexed")]
    [Description("List all directories currently registered with the vs-chromium indexer.")]
    public async Task<string> ListIndexedAsync(CancellationToken cancellationToken = default) {
        var result = await _handle.CallAsync<ListIndexedDirsResult>(PipeMethods.ListIndexedDirs, null, cancellationToken);
        if (result.Dirs.Count == 0) return "No directories indexed. Call vs_chromium_ensure_indexed first.";
        var sb = new StringBuilder();
        foreach (var d in result.Dirs) {
            sb.Append(d.Path).Append("  (").Append(d.Status).Append(')');
            if (d.FileCount > 0) sb.Append("  ").Append(d.FileCount).Append(" files");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    [McpServerTool(Name = "vs_chromium_status")]
    [Description("Show vs-chromium indexer status (daemon uptime, indexer state, project count).")]
    public async Task<string> StatusAsync(CancellationToken cancellationToken = default) {
        var result = await _handle.CallAsync<IndexStatusResult>(PipeMethods.GetIndexStatus, null, cancellationToken);
        var sb = new StringBuilder();
        sb.AppendLine($"Daemon version: {result.DaemonVersion}");
        sb.AppendLine($"Indexer state: {result.ServerState}");
        sb.AppendLine($"Uptime: {result.UptimeSeconds:F0}s");
        sb.AppendLine($"Indexed dirs: {result.IndexedDirCount}");
        return sb.ToString();
    }
}

/// <summary>Shared, lazily-initialized handle to the daemon for all tool invocations in this MCP session.</summary>
public sealed class DaemonHandle {
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DaemonClient? _client;

    public async Task<TResult> CallAsync<TResult>(string method, object? @params, CancellationToken ct) {
        var client = await GetAsync(ct);
        try {
            return await client.CallAsync<TResult>(method, @params, ct);
        } catch (Exception ex) when (IsTransportFailure(ex)) {
            await InvalidateAsync();
            client = await GetAsync(ct);
            return await client.CallAsync<TResult>(method, @params, ct);
        }
    }

    private static bool IsTransportFailure(Exception ex) {
        // The daemon died, the pipe broke mid-call, or the connection was reset. These all signal
        // the cached client is unusable and we should reconnect once before surfacing the error.
        if (ex is IOException or ObjectDisposedException) return true;
        if (ex is InvalidOperationException && ex.Message is { } m
            && (m.Contains("daemon", StringComparison.OrdinalIgnoreCase)
                || m.Contains("pipe", StringComparison.OrdinalIgnoreCase))) return true;
        return false;
    }

    private async Task<DaemonClient> GetAsync(CancellationToken ct) {
        if (_client != null && _client.IsConnected) return _client;
        await _gate.WaitAsync(ct);
        try {
            if (_client != null && !_client.IsConnected) {
                await _client.DisposeAsync();
                _client = null;
            }
            _client ??= await DaemonBootstrap.EnsureRunningAsync(ct);
            return _client;
        } finally {
            _gate.Release();
        }
    }

    private async Task InvalidateAsync() {
        await _gate.WaitAsync();
        try {
            if (_client != null) {
                await _client.DisposeAsync();
                _client = null;
            }
        } finally {
            _gate.Release();
        }
    }
}
