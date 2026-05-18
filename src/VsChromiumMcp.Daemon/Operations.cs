using System.Diagnostics;
using VsChromium.Core.Ipc;
using VsChromium.Core.Ipc.TypedMessages;
using VsChromiumMcp.Shared;
using SharedPaths = VsChromiumMcp.Shared.Paths;

namespace VsChromiumMcp.Daemon;

internal sealed class Operations {
    private readonly VsChromiumServerHost _server;
    private readonly ProjectRegistry _registry;
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private TaskCompletionSource<bool> _nextScanFinished = NewTcs();
    private readonly object _scanGate = new();
    private volatile string _serverState = "starting";

    public Operations(VsChromiumServerHost server, ProjectRegistry registry) {
        _server = server;
        _registry = registry;
        _server.TypedEventReceived += OnTypedEvent;
    }

    public void Dispose() {
        _server.TypedEventReceived -= OnTypedEvent;
    }

    public async Task ReindexKnownProjectsAsync(CancellationToken ct) {
        var paths = _registry.All();
        foreach (var p in paths) {
            try {
                EnsureProjectFile(p);
                await RegisterAnchorAsync(p, ct);
            } catch (Exception ex) {
                Log.Error(ex, $"reindex: failed for {p}");
            }
        }
        if (paths.Count > 0) {
            await WaitForNextScanAsync(TimeSpan.FromMinutes(5), ct);
        }
    }

    private void OnTypedEvent(TypedEvent ev) {
        switch (ev) {
            case FileSystemScanStarted:
                _serverState = "scanning";
                Log.Info($"event: FileSystemScanStarted");
                break;
            case FileSystemScanFinished:
                _serverState = "ready";
                Log.Info($"event: FileSystemScanFinished");
                CompleteCurrentScanWaiters();
                break;
            case SearchEngineFilesLoading:
                _serverState = "loading-search";
                break;
            case SearchEngineFilesLoaded:
                _serverState = "ready";
                CompleteCurrentScanWaiters();
                break;
            case IndexingServerStateChangedEvent ssc:
                _serverState = $"indexer={ssc.ServerStatus}";
                break;
            case ProgressReportEvent:
                /* progress; ignored for now */
                break;
            default:
                Log.Info($"event: {ev.GetType().Name}");
                break;
        }
    }

    private void CompleteCurrentScanWaiters() {
        TaskCompletionSource<bool> tcs;
        lock (_scanGate) {
            tcs = _nextScanFinished;
            _nextScanFinished = NewTcs();
        }
        tcs.TrySetResult(true);
    }

    private Task WaitForNextScanAsync(TimeSpan timeout, CancellationToken ct) {
        TaskCompletionSource<bool> tcs;
        lock (_scanGate) { tcs = _nextScanFinished; }
        return WaitOrTimeout(tcs.Task, timeout, ct);
    }

    private static async Task WaitOrTimeout(Task task, TimeSpan timeout, CancellationToken ct) {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(timeout, cts.Token);
        var done = await Task.WhenAny(task, delay);
        cts.Cancel();
        if (done == delay && !task.IsCompleted)
            throw new TimeoutException($"timed out after {timeout}");
        await task;
    }

    public async Task<EnsureIndexedResult> EnsureIndexedAsync(EnsureIndexedParams p, CancellationToken ct) {
        if (string.IsNullOrWhiteSpace(p.Path))
            throw new ArgumentException("path is required");
        if (!Directory.Exists(p.Path))
            throw new DirectoryNotFoundException($"directory not found: {p.Path}");

        var fullPath = Path.GetFullPath(p.Path).TrimEnd('\\', '/');
        var alreadyKnown = _registry.Contains(fullPath);
        var fileCreated = EnsureProjectFile(fullPath);
        _registry.Add(fullPath);

        var sw = Stopwatch.StartNew();
        // Trigger discovery by registering an anchor file inside the project.
        await RegisterAnchorAsync(fullPath, ct);

        long fileCount = 0;
        bool indexed = false;
        if (p.Wait) {
            var deadline = DateTime.UtcNow.AddSeconds(p.WaitTimeoutSeconds);
            // Phase 1: check if the project is already in the tree; only wait for a scan event
            // if it isn't. On re-registration of a known path, RegisterAnchorAsync does not trigger
            // a new scan, so waiting for FileSystemScanFinished would block until WaitTimeoutSeconds
            // elapsed.
            (indexed, fileCount) = await GetProjectStatusAsync(fullPath, ct);
            while (!indexed && DateTime.UtcNow < deadline) {
                try {
                    await WaitForNextScanAsync(deadline - DateTime.UtcNow, ct);
                } catch (TimeoutException) { break; }
                (indexed, fileCount) = await GetProjectStatusAsync(fullPath, ct);
            }
            // Phase 2: wait for the search engine to load file contents. The engine warms
            // asynchronously after the scan and does not emit a typed event we can subscribe to;
            // we probe with a cheap search and poll until searched_file_count stabilizes.
            if (indexed) {
                fileCount = await WaitForSearchEngineReadyAsync(deadline, ct);
            }
        }

        sw.Stop();
        return new EnsureIndexedResult {
            Path = fullPath,
            ProjectFileCreated = fileCreated,
            AlreadyKnown = alreadyKnown,
            Indexed = indexed,
            FileCount = fileCount,
            WaitedSeconds = sw.Elapsed.TotalSeconds,
        };
    }

    private static bool EnsureProjectFile(string projectPath) {
        var marker = Path.Combine(projectPath, SharedPaths.VsChromiumProjectFileName);
        if (File.Exists(marker)) return false;
        File.WriteAllText(marker, SharedPaths.DefaultProjectFileContent);
        Log.Info($"ensure_indexed: created {marker}");
        return true;
    }

    private Task RegisterAnchorAsync(string projectPath, CancellationToken ct) {
        // The vs-chromium server discovers projects by walking up from any registered file.
        // The project marker itself is a stable anchor that always exists once we've ensured it.
        var anchor = Path.Combine(projectPath, SharedPaths.VsChromiumProjectFileName);
        return _server.SendAsync<DoneResponse>(new RegisterFileRequest { FileName = anchor }, ct);
    }

    private async Task<(bool indexed, long fileCount)> GetProjectStatusAsync(string projectPath, CancellationToken ct) {
        var resp = await _server.SendAsync<GetFileSystemTreeResponse>(new GetFileSystemTreeRequest(), ct);
        var projects = resp.Tree?.Projects;
        if (projects == null) return (false, 0);
        var match = projects.FirstOrDefault(p =>
            string.Equals(p.RootPath?.TrimEnd('\\', '/'), projectPath, StringComparison.OrdinalIgnoreCase));
        if (match == null) return (false, 0);
        // ProjectEntry doesn't carry file count; presence in the snapshot is the "indexed" signal.
        return (true, 0);
    }

    private static long CountFiles(DirectoryEntry dir) {
        long count = 0;
        if (dir.Entries == null) return 0;
        foreach (var e in dir.Entries) {
            if (e is FileEntry) count++;
            else if (e is DirectoryEntry sub) count += CountFiles(sub);
        }
        return count;
    }

    public async Task<SearchTextResult> SearchTextAsync(SearchTextParams p, CancellationToken ct) {
        if (string.IsNullOrEmpty(p.Pattern))
            throw new ArgumentException("pattern is required");

        var req = new SearchCodeRequest {
            SearchParams = new SearchParams {
                SearchString = p.Pattern,
                FilePathPattern = p.FileFilter ?? "",
                MaxResults = p.MaxResults > 0 ? p.MaxResults : 500,
                MatchCase = p.MatchCase,
                MatchWholeWord = p.WholeWord,
                Regex = p.Regex,
                IncludeSymLinks = false,
                UseRe2Engine = p.Regex,
            },
        };
        var resp = await _server.SendAsync<SearchCodeResponse>(req, ct);

        var perFile = new List<(string file, List<FilePositionSpan> spans)>();
        if (resp.SearchResults != null) CollectFileSpans(resp.SearchResults, "", perFile);

        var matches = new List<SearchTextMatch>(capacity: Math.Min(p.MaxResults, 4096));
        foreach (var (file, spans) in perFile) {
            if (matches.Count >= p.MaxResults) break;
            var extractReq = new GetFileExtractsRequest {
                FileName = file,
                Positions = spans,
                MaxExtractLength = Math.Max(60, p.ExtractLength),
            };
            GetFileExtractsResponse extractResp;
            try {
                extractResp = await _server.SendAsync<GetFileExtractsResponse>(extractReq, ct);
            } catch (Exception ex) {
                Log.Warn($"search_text: extract failed for {file}: {ex.Message}");
                continue;
            }
            if (extractResp.FileExtracts == null) continue;
            foreach (var ex in extractResp.FileExtracts) {
                if (ex == null) continue;
                if (matches.Count >= p.MaxResults) break;
                matches.Add(new SearchTextMatch {
                    FilePath = file,
                    Line = ex.LineNumber + 1, // vs-chromium uses 0-based
                    Column = ex.ColumnNumber + 1,
                    Text = (ex.Text ?? string.Empty).Trim(),
                });
            }
        }

        return new SearchTextResult {
            Matches = matches,
            HitCount = resp.HitCount,
            SearchedFileCount = resp.SearchedFileCount,
            TotalFileCount = resp.TotalFileCount,
            Truncated = matches.Count < resp.HitCount,
        };
    }

    private static void CollectFileSpans(DirectoryEntry dir, string prefix, List<(string file, List<FilePositionSpan>)> acc) {
        if (dir.Entries == null) return;
        // The root DirectoryEntry has Name="" (anonymous wrapper). Its direct children are project roots
        // whose Name is the absolute project path. Deeper entries' Names are basenames.
        foreach (var entry in dir.Entries) {
            var entryName = entry.Name ?? "";
            string fullPath;
            if (string.IsNullOrEmpty(prefix)) {
                // entry is a project root - Name is already absolute.
                fullPath = entryName;
            } else {
                fullPath = string.IsNullOrEmpty(entryName) ? prefix : Path.Combine(prefix, entryName);
            }

            if (entry is FileEntry file && file.Data is FilePositionsData pos) {
                if (pos.Positions != null && pos.Positions.Count > 0)
                    acc.Add((fullPath, pos.Positions));
            } else if (entry is DirectoryEntry sub) {
                CollectFileSpans(sub, fullPath, acc);
            }
        }
    }

    public async Task<SearchFilesResult> SearchFilesAsync(SearchFilesParams p, CancellationToken ct) {
        if (string.IsNullOrEmpty(p.Pattern))
            throw new ArgumentException("pattern is required");

        var req = new SearchFilePathsRequest {
            SearchParams = new SearchParams {
                SearchString = p.Pattern,
                FilePathPattern = "",
                MaxResults = p.MaxResults > 0 ? p.MaxResults : 500,
                MatchCase = p.MatchCase,
                MatchWholeWord = false,
                Regex = p.Regex,
                IncludeSymLinks = false,
            },
        };
        var resp = await _server.SendAsync<SearchFilePathsResponse>(req, ct);

        var files = new List<string>(capacity: 256);
        if (resp.SearchResult != null) CollectFilePaths(resp.SearchResult, "", files, p.MaxResults);

        return new SearchFilesResult {
            Files = files,
            HitCount = resp.HitCount,
            TotalFileCount = resp.TotalCount,
            Truncated = files.Count < resp.HitCount,
        };
    }

    private static void CollectFilePaths(DirectoryEntry dir, string prefix, List<string> acc, int max) {
        if (dir.Entries == null) return;
        foreach (var entry in dir.Entries) {
            if (acc.Count >= max) return;
            var entryName = entry.Name ?? "";
            string fullPath;
            if (string.IsNullOrEmpty(prefix)) {
                fullPath = entryName;
            } else {
                fullPath = string.IsNullOrEmpty(entryName) ? prefix : Path.Combine(prefix, entryName);
            }
            if (entry is FileEntry) {
                if (!string.IsNullOrEmpty(fullPath)) acc.Add(fullPath);
            } else if (entry is DirectoryEntry sub) {
                CollectFilePaths(sub, fullPath, acc, max);
            }
        }
    }

    public async Task<ListIndexedDirsResult> ListIndexedDirsAsync(CancellationToken ct) {
        var resp = await _server.SendAsync<GetFileSystemTreeResponse>(new GetFileSystemTreeRequest(), ct);
        var dirs = new List<IndexedDir>();
        if (resp.Tree?.Projects != null) {
            foreach (var pe in resp.Tree.Projects) {
                dirs.Add(new IndexedDir {
                    Path = pe.RootPath ?? "",
                    FileCount = 0, // not exposed by ProjectEntry; per-project details require GetProjectDetailsRequest
                    Status = "ready",
                });
            }
        }
        var known = dirs.Select(d => d.Path.TrimEnd('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _registry.All()) {
            if (!known.Contains(p))
                dirs.Add(new IndexedDir { Path = p, FileCount = 0, Status = "pending" });
        }
        return new ListIndexedDirsResult { Dirs = dirs };
    }

    public IndexStatusResult GetIndexStatus() => new() {
        DaemonVersion = typeof(Operations).Assembly.GetName().Version?.ToString() ?? "0.0.0",
        ServerState = _serverState,
        UptimeSeconds = (DateTime.UtcNow - _startedAt).TotalSeconds,
        IndexedDirCount = _registry.All().Count,
    };

    private async Task<long> WaitForSearchEngineReadyAsync(DateTime deadline, CancellationToken ct) {
        var probeReq = new SearchCodeRequest {
            SearchParams = new SearchParams {
                SearchString = "__vs_chromium_probe_no_match_pattern__",
                FilePathPattern = "",
                MaxResults = 1,
                MatchCase = true,
            },
        };
        long lastTotal = 0;
        int stableTicks = 0;
        // After the search engine reports a non-zero searched_file_count, it can still be loading
        // additional files. Wait until total_file_count is observed unchanged across this many polls
        // before declaring the engine warm; this prevents reporting a misleading partial count.
        const int requiredStableTicks = 3;
        var pollInterval = TimeSpan.FromMilliseconds(500);
        while (DateTime.UtcNow < deadline) {
            try {
                var probe = await _server.SendAsync<SearchCodeResponse>(probeReq, ct);
                var total = probe.TotalFileCount;
                if (probe.SearchedFileCount > 0) {
                    if (total == lastTotal && total > 0) {
                        stableTicks++;
                        if (stableTicks >= requiredStableTicks) return total;
                    } else {
                        stableTicks = 0;
                        lastTotal = total;
                    }
                }
            } catch (Exception ex) {
                Log.Warn($"engine-probe failed: {ex.Message}");
            }
            try { await Task.Delay(pollInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
        return lastTotal;
    }

    private static TaskCompletionSource<bool> NewTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
