using System.Text.Json;
using System.Text.Json.Serialization;

namespace VsChromiumMcp.Shared;

public static class PipeMethods {
    public const string Ping = "ping";
    public const string EnsureIndexed = "ensure_indexed";
    public const string SearchText = "search_text";
    public const string SearchFiles = "search_files";
    public const string ListIndexedDirs = "list_indexed_dirs";
    public const string GetIndexStatus = "get_index_status";
    public const string Shutdown = "shutdown";
}

public sealed class PipeRequest {
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("method")] public string Method { get; set; } = "";
    [JsonPropertyName("params")] public JsonElement? Params { get; set; }
}

public sealed class PipeResponse {
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("result")] public JsonElement? Result { get; set; }
    [JsonPropertyName("error")] public PipeError? Error { get; set; }
}

public sealed class PipeError {
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("details")] public string? Details { get; set; }
}

public sealed class EnsureIndexedParams {
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("wait")] public bool Wait { get; set; } = true;
    [JsonPropertyName("wait_timeout_seconds")] public int WaitTimeoutSeconds { get; set; } = 180;
}

public sealed class EnsureIndexedResult {
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("project_file_created")] public bool ProjectFileCreated { get; set; }
    [JsonPropertyName("already_known")] public bool AlreadyKnown { get; set; }
    [JsonPropertyName("indexed")] public bool Indexed { get; set; }
    [JsonPropertyName("file_count")] public long FileCount { get; set; }
    [JsonPropertyName("waited_seconds")] public double WaitedSeconds { get; set; }
}

public sealed class SearchTextParams {
    [JsonPropertyName("pattern")] public string Pattern { get; set; } = "";
    [JsonPropertyName("match_case")] public bool MatchCase { get; set; }
    [JsonPropertyName("whole_word")] public bool WholeWord { get; set; }
    [JsonPropertyName("regex")] public bool Regex { get; set; }
    [JsonPropertyName("file_filter")] public string? FileFilter { get; set; }
    [JsonPropertyName("max_results")] public int MaxResults { get; set; } = 500;
    [JsonPropertyName("extract_length")] public int ExtractLength { get; set; } = 160;
}

public sealed class SearchTextMatch {
    [JsonPropertyName("file_path")] public string FilePath { get; set; } = "";
    [JsonPropertyName("line")] public int Line { get; set; }
    [JsonPropertyName("column")] public int Column { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; } = "";
}

public sealed class SearchTextResult {
    [JsonPropertyName("matches")] public List<SearchTextMatch> Matches { get; set; } = new();
    [JsonPropertyName("hit_count")] public long HitCount { get; set; }
    [JsonPropertyName("searched_file_count")] public long SearchedFileCount { get; set; }
    [JsonPropertyName("total_file_count")] public long TotalFileCount { get; set; }
    [JsonPropertyName("truncated")] public bool Truncated { get; set; }
}

public sealed class SearchFilesParams {
    [JsonPropertyName("pattern")] public string Pattern { get; set; } = "";
    [JsonPropertyName("match_case")] public bool MatchCase { get; set; }
    [JsonPropertyName("regex")] public bool Regex { get; set; }
    [JsonPropertyName("max_results")] public int MaxResults { get; set; } = 500;
}

public sealed class SearchFilesResult {
    [JsonPropertyName("files")] public List<string> Files { get; set; } = new();
    [JsonPropertyName("hit_count")] public long HitCount { get; set; }
    [JsonPropertyName("total_file_count")] public long TotalFileCount { get; set; }
    [JsonPropertyName("truncated")] public bool Truncated { get; set; }
}

public sealed class IndexedDir {
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("file_count")] public long FileCount { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "unknown";
}

public sealed class ListIndexedDirsResult {
    [JsonPropertyName("dirs")] public List<IndexedDir> Dirs { get; set; } = new();
}

public sealed class IndexStatusResult {
    [JsonPropertyName("daemon_version")] public string DaemonVersion { get; set; } = "";
    [JsonPropertyName("server_state")] public string ServerState { get; set; } = "";
    [JsonPropertyName("uptime_seconds")] public double UptimeSeconds { get; set; }
    [JsonPropertyName("indexed_dir_count")] public int IndexedDirCount { get; set; }
}

public static class PipeJson {
    public static readonly JsonSerializerOptions Options = new() {
        PropertyNamingPolicy = null,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
