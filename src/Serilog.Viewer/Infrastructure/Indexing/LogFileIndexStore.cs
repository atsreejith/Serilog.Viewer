using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog.Viewer.Infrastructure.Parsing;
using Serilog.Viewer.Infrastructure.Reading;
using Serilog.Viewer.Interfaces;
using Serilog.Viewer.Models;
using LogLevel = Serilog.Viewer.Models.LogLevel;

namespace Serilog.Viewer.Infrastructure.Indexing;

/// <summary>
/// SQLite index over the log files: one row per entry holding its file offset and the columns
/// the grid, filters and dashboard need.
/// <para>
/// Concurrency: the database runs in WAL mode, where readers see a consistent snapshot and are
/// never blocked by a writer. Queries therefore take no lock at all. Everything that writes —
/// schema setup, indexing a file, a background backfill slice — is serialised by
/// <see cref="_writeGate"/>. A query only waits on that gate when one of the files it asks about
/// actually needs indexing work.
/// </para>
/// </summary>
internal sealed class LogFileIndexStore
{
    private const int SchemaVersion = 6;

    /// <summary>
    /// A cold load of at least this many bytes may drop the secondary indexes for the duration of
    /// the insert and recreate them afterwards. Below this the drop/recreate overhead outweighs
    /// the saved per-row index maintenance.
    /// </summary>
    private const long BulkLoadThresholdBytes = 8L * 1024 * 1024;

    /// <summary>
    /// How many bytes of not-yet-indexed files one request indexes before it answers. Files are
    /// taken newest first; a file that fits in what is left of the budget is indexed whole, a
    /// larger one gets a window at its end, and once the budget is spent the remaining files are
    /// left entirely to the background backfill.
    /// <para>
    /// Without a budget the first request after start-up indexed every file it was asked about
    /// before returning: ten 16 MB files took 63 seconds.
    /// </para>
    /// </summary>
    private const long ColdSyncBudgetBytes = 32L * 1024 * 1024;

    /// <summary>
    /// Size of the window indexed at the end of a file too large to index whole. The default view
    /// is newest-first, so the entries a user waits for live at the tail. Sized so the newest
    /// entries cover far more than a first page while staying quick to index.
    /// </summary>
    private const long TailWindowBytes = 8L * 1024 * 1024;

    /// <summary>
    /// Bytes indexed per background backfill slice. The write gate is released between slices, so
    /// this bounds how long a query that needs to index new lines can be stuck behind the backfill.
    /// </summary>
    private const long HeadBackfillSliceBytes = 1L * 1024 * 1024;

    /// <summary>
    /// How long the backfill keeps taking slices before it releases the write gate and pauses.
    /// Bounds the wait of a query that has new lines to index, while letting many cheap slices
    /// share one pause.
    /// </summary>
    private static readonly TimeSpan HeadBackfillBurst = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Pause between backfill bursts, so the backfill never monopolises a core of the host
    /// process and a waiting writer gets a clear window to take the gate.
    /// </summary>
    private static readonly TimeSpan HeadBackfillSliceDelay = TimeSpan.FromMilliseconds(15);

    /// <summary>
    /// A file not written to for this long is treated as finished for now: a last line without a
    /// line break is then taken as a whole line rather than as one still being written.
    /// </summary>
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(2);

    private readonly string _databasePath;
    private readonly StreamingLogFileReader _reader;
    private readonly ILogger _logger;
    private readonly bool _backgroundIndexing;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private volatile bool _initialized;

    /// <summary>Guards the backfill queue and the stats cache.</summary>
    private readonly object _stateLock = new();

    /// <summary>
    /// File ids whose head backfill is queued or running. Every query re-observes a pending
    /// low-water mark, so without this each one would queue a redundant duplicate.
    /// </summary>
    private readonly HashSet<long> _queuedBackfills = [];
    private readonly List<PendingBackfill> _backfillQueue = [];
    private bool _backfillWorkerRunning;

    /// <summary>
    /// Dashboard aggregates per file, keyed by file id and valid for one exact index state of
    /// that file, so a dashboard refresh only re-aggregates files whose index actually changed —
    /// typically just the active one.
    /// </summary>
    private readonly Dictionary<long, CachedFileStats> _statsCache = [];

    public LogFileIndexStore(
        string logFolder,
        StreamingLogFileReader reader,
        ILogger<LogFileIndexStore>? logger = null,
        string? indexFolder = null,
        bool backgroundIndexing = true
    )
    {
        indexFolder ??= Path.Combine(logFolder, ".serilog-viewer");
        _databasePath = Path.Combine(indexFolder, "index.sqlite");
        _reader = reader;
        _logger = logger ?? NullLogger<LogFileIndexStore>.Instance;
        _backgroundIndexing = backgroundIndexing;
    }

    // ------------------------------------------------------------------------------------------
    // Queries
    // ------------------------------------------------------------------------------------------

    public async Task<IndexedQueryResult> QueryAsync(
        IReadOnlyList<LogFile> files,
        LogQuery query,
        CancellationToken cancellationToken = default,
        bool locateOnly = false
    )
    {
        if (files.Count == 0)
            return new IndexedQueryResult([], 0);

        await InitializeAsync(cancellationToken);

        await using var connection = OpenConnection();
        await EnsureFreshAsync(connection, files, cancellationToken);

        // One read snapshot for the count and the page, so a backfill slice committing between
        // the two cannot make them disagree.
        await using var snapshot = connection.BeginTransaction(deferred: true);

        // A text search is a full LIKE scan of every candidate row, and the count needs the same
        // scan. Doing both in one statement (COUNT(*) OVER ()) halves the search cost. Without
        // search text the count is cheap and the page is a bounded index walk, so they stay
        // separate: a window function would force the page query to visit every matching row.
        var hasSearch = !string.IsNullOrWhiteSpace(query.SearchText);

        var countStarted = Stopwatch.GetTimestamp();
        var total = hasSearch
            ? 0
            : await CountAsync(connection, snapshot, files, query, cancellationToken);
        var countElapsed = Stopwatch.GetElapsedTime(countStarted);

        var pageStarted = Stopwatch.GetTimestamp();
        var (pointers, pageTotal) = await GetPageAsync(
            connection,
            snapshot,
            files,
            query,
            includeTotal: hasSearch,
            locateOnly,
            cancellationToken
        );
        var pageElapsed = Stopwatch.GetElapsedTime(pageStarted);

        if (hasSearch)
        {
            if (pageTotal is { } windowTotal)
                total = (int)Math.Min(windowTotal, int.MaxValue);
            else if (query.Page > 1)
                // An empty page past the end carries no window count; count separately.
                total = await CountAsync(connection, snapshot, files, query, cancellationToken);
            // An empty first page means nothing matched: the scan that produced it already
            // proved the total is zero, so there is no reason to run it a second time.
        }

        _logger.LogDebug(
            "Index query over {FileCount} file(s): count={CountMs} ms ({TotalCount} matches), page={PageMs} ms ({PointerCount} pointers, page {Page} size {PageSize}), search={HasSearchText}",
            files.Count,
            (long)countElapsed.TotalMilliseconds,
            total,
            (long)pageElapsed.TotalMilliseconds,
            pointers.Count,
            query.Page,
            query.PageSize,
            hasSearch
        );

        return new IndexedQueryResult(pointers, total);
    }

    public async Task<DashboardStats> GetStatsAsync(
        IReadOnlyList<LogFile> files,
        CancellationToken cancellationToken = default
    )
    {
        if (files.Count == 0)
            return new DashboardStats();

        await InitializeAsync(cancellationToken);

        await using var connection = OpenConnection();
        await EnsureFreshAsync(connection, files, cancellationToken);

        var statsStarted = Stopwatch.GetTimestamp();
        var (merged, recomputed) = await GetMergedStatsAsync(connection, files, cancellationToken);
        var statsElapsed = Stopwatch.GetElapsedTime(statsStarted);

        _logger.LogDebug(
            "Index stats over {FileCount} file(s) in {ElapsedMs} ms: {RecomputedFiles} file(s) re-aggregated, {CachedFiles} served from cache",
            files.Count,
            (long)statsElapsed.TotalMilliseconds,
            recomputed,
            files.Count - recomputed
        );

        var countsByLevel = merged.ByLevel;
        var errorsByHour = merged
            .ErrorsByHour.OrderBy(p => p.Key)
            .Select(p => new TimeSeriesPoint
            {
                Timestamp = new DateTimeOffset(p.Key, TimeSpan.Zero),
                Count = p.Value,
            })
            .ToList();
        var logsByDay = merged
            .LogsByDay.OrderBy(p => p.Key)
            .Select(p => new TimeSeriesPoint
            {
                Timestamp = new DateTimeOffset(p.Key, TimeSpan.Zero),
                Count = p.Value,
            })
            .ToList();
        var topSources = merged
            .BySource.OrderByDescending(p => p.Value)
            .ThenBy(p => p.Key, StringComparer.Ordinal)
            .Take(10)
            .Select(p => new SourceDistributionPoint { Source = p.Key, Count = p.Value })
            .ToList();

        var verboses = countsByLevel.GetValueOrDefault(LogLevel.Verbose);
        var debugs = countsByLevel.GetValueOrDefault(LogLevel.Debug);
        var infos = countsByLevel.GetValueOrDefault(LogLevel.Information);
        var warnings = countsByLevel.GetValueOrDefault(LogLevel.Warning);
        var errors = countsByLevel.GetValueOrDefault(LogLevel.Error);
        var fatals = countsByLevel.GetValueOrDefault(LogLevel.Fatal);

        return new DashboardStats
        {
            TotalLogs = countsByLevel.Values.Sum(),
            Errors = errors,
            Warnings = warnings,
            Fatals = fatals,
            Verboses = verboses,
            Debugs = debugs,
            Informations = infos,
            ActiveFiles = files.Count(f => f.IsActive),
            TotalFileSizeBytes = files.Sum(f => f.SizeBytes),
            ErrorsByHour = errorsByHour,
            LogsByDay = logsByDay,
            LogsByLevel =
            [
                new()
                {
                    Level = "Verbose",
                    Count = verboses,
                    Color = "#8b949e",
                },
                new()
                {
                    Level = "Debug",
                    Count = debugs,
                    Color = "#58a6ff",
                },
                new()
                {
                    Level = "Information",
                    Count = infos,
                    Color = "#3fb950",
                },
                new()
                {
                    Level = "Warning",
                    Count = warnings,
                    Color = "#d29922",
                },
                new()
                {
                    Level = "Error",
                    Count = errors,
                    Color = "#f85149",
                },
                new()
                {
                    Level = "Fatal",
                    Count = fatals,
                    Color = "#bc8cff",
                },
            ],
            TopSources = topSources,
        };
    }

    // ------------------------------------------------------------------------------------------
    // Schema
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Secondary indexes on <c>entries</c>. Declared once so schema creation and post-bulk-load
    /// rebuild cannot drift apart.
    /// </summary>
    private static readonly (string Name, string Ddl)[] _secondaryIndexes =
    [
        // Identity of an entry, and what makes re-reading a range of a file idempotent.
        ("ux_entries_file_offset",
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_entries_file_offset ON entries(file_id, offset);"),
        // Newest-first paging: one file, and all files.
        ("ix_entries_file_time",
            "CREATE INDEX IF NOT EXISTS ix_entries_file_time ON entries(file_id, timestamp_ticks);"),
        ("ix_entries_time",
            "CREATE INDEX IF NOT EXISTS ix_entries_time ON entries(timestamp_ticks);"),
        // Level filters, each level already in time order so a page needs no sort: all files,
        // and one file. The second also covers the dashboard's errors-per-hour aggregate.
        ("ix_entries_level_time",
            "CREATE INDEX IF NOT EXISTS ix_entries_level_time ON entries(level, timestamp_ticks);"),
        ("ix_entries_file_level_time",
            "CREATE INDEX IF NOT EXISTS ix_entries_file_level_time ON entries(file_id, level, timestamp_ticks);"),
        // Dashboard top sources are computed per file, so file_id leads: the per-file GROUP BY
        // then walks one contiguous, already-ordered range of the index.
        ("ix_entries_file_source",
            "CREATE INDEX IF NOT EXISTS ix_entries_file_source ON entries(file_id, source_context);"),
        // Partial: most entries carry neither id, and a full index would hold a NULL row for
        // every one of them. An equality filter still uses these, since it implies NOT NULL.
        ("ix_entries_correlation",
            "CREATE INDEX IF NOT EXISTS ix_entries_correlation ON entries(correlation_id) WHERE correlation_id IS NOT NULL;"),
        ("ix_entries_request",
            "CREATE INDEX IF NOT EXISTS ix_entries_request ON entries(request_id) WHERE request_id IS NOT NULL;"),
    ];

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
            return;

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
                return;

            var started = Stopwatch.GetTimestamp();
            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);

            await using var connection = OpenConnection();
            await ExecuteNonQueryAsync(connection, null, "PRAGMA journal_mode=WAL;", cancellationToken);

            var currentVersion = await GetUserVersionAsync(connection, cancellationToken);
            if (currentVersion != SchemaVersion)
            {
                if (currentVersion != 0)
                {
                    _logger.LogDebug(
                        "Index schema version {CurrentVersion} does not match expected {ExpectedVersion}; dropping and rebuilding the whole index at {DatabasePath}",
                        currentVersion,
                        SchemaVersion,
                        _databasePath
                    );
                }

                await ExecuteNonQueryAsync(connection, null, "DROP TABLE IF EXISTS entries;", cancellationToken);
                await ExecuteNonQueryAsync(connection, null, "DROP TABLE IF EXISTS file_levels;", cancellationToken);
                await ExecuteNonQueryAsync(connection, null, "DROP TABLE IF EXISTS files;", cancellationToken);
            }

            await ExecuteNonQueryAsync(
                connection,
                null,
                """
                CREATE TABLE IF NOT EXISTS files (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL UNIQUE,
                    full_path TEXT NOT NULL,
                    size_bytes INTEGER NOT NULL,
                    last_write_ticks INTEGER NOT NULL,
                    -- Bytes below this offset that are indexed, counting from head_scanned_offset.
                    -- Always the end of a complete line, so the next scan starts on a line start.
                    last_scanned_offset INTEGER NOT NULL,
                    format TEXT NOT NULL,
                    -- Low-water mark of the background backfill: bytes below it are not yet
                    -- indexed. 0 once the file is fully indexed.
                    head_scanned_offset INTEGER NOT NULL DEFAULT 0
                );
                """,
                cancellationToken
            );
            await ExecuteNonQueryAsync(
                connection,
                null,
                """
                CREATE TABLE IF NOT EXISTS entries (
                    id INTEGER PRIMARY KEY,
                    file_id INTEGER NOT NULL,
                    offset INTEGER NOT NULL,
                    timestamp_ticks INTEGER NOT NULL,
                    level INTEGER NOT NULL,
                    message TEXT NOT NULL,
                    exception TEXT NULL,
                    source_context TEXT NULL COLLATE NOCASE,
                    correlation_id TEXT NULL COLLATE NOCASE,
                    request_id TEXT NULL COLLATE NOCASE
                );
                """,
                cancellationToken
            );
            await ExecuteNonQueryAsync(
                connection,
                null,
                """
                -- Entries per file and level, maintained in the same transaction as the rows.
                -- Totals and level-filtered totals are read from here instead of counting rows,
                -- which made every page request cost time proportional to the size of the index.
                CREATE TABLE IF NOT EXISTS file_levels (
                    file_id INTEGER NOT NULL,
                    level INTEGER NOT NULL,
                    entry_count INTEGER NOT NULL,
                    PRIMARY KEY (file_id, level)
                ) WITHOUT ROWID;
                """,
                cancellationToken
            );
            await CreateSecondaryIndexesAsync(connection, null, cancellationToken);
            await ExecuteNonQueryAsync(
                connection,
                null,
                $"PRAGMA user_version={SchemaVersion};",
                cancellationToken
            );

            _logger.LogDebug(
                "Index schema ready in {ElapsedMs} ms at {DatabasePath}",
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                _databasePath
            );

            _initialized = true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async Task CreateSecondaryIndexesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken
    )
    {
        foreach (var (_, ddl) in _secondaryIndexes)
            await ExecuteNonQueryAsync(connection, transaction, ddl, cancellationToken);
    }

    private static async Task DropSecondaryIndexesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken
    )
    {
        foreach (var (name, _) in _secondaryIndexes)
        {
            await ExecuteNonQueryAsync(
                connection,
                transaction,
                $"DROP INDEX IF EXISTS {name};",
                cancellationToken
            );
        }
    }

    // ------------------------------------------------------------------------------------------
    // Keeping the index up to date
    // ------------------------------------------------------------------------------------------

    private enum FileAction
    {
        /// <summary>Nothing new to index.</summary>
        Skip,

        /// <summary>The file grew: index the new lines.</summary>
        Tail,

        /// <summary>Not indexed yet, or no longer the file that was indexed: start over.</summary>
        Rebuild,
    }

    private readonly record struct FilePlan(
        LogFile File,
        FileIndexState? State,
        FileAction Action,
        string? RebuildReason,
        bool Quiet
    );

    private static FilePlan PlanFile(LogFile file, FileIndexState? state, DateTimeOffset now)
    {
        var quiet = now - file.LastModified > QuietPeriod;

        if (state is null)
            return new FilePlan(file, state, FileAction.Rebuild, "not-indexed", quiet);

        if (!string.Equals(state.FullPath, file.FullPath, StringComparison.OrdinalIgnoreCase))
            return new FilePlan(file, state, FileAction.Rebuild, "path-changed", quiet);

        if (file.SizeBytes < state.LastScannedOffset)
            return new FilePlan(file, state, FileAction.Rebuild, "file-shrank", quiet);

        if (file.SizeBytes == state.SizeBytes)
        {
            if (file.LastModified.UtcTicks != state.LastWriteTicks)
                return new FilePlan(file, state, FileAction.Rebuild, "size-same-mtime-changed", quiet);

            // Unchanged since the last scan. The only thing that can still be outstanding is a
            // last line without a line break that the scan left alone because the file was being
            // written; once the file has gone quiet that line is as complete as it will get.
            var pendingTail = state.LastScannedOffset < file.SizeBytes;
            return new FilePlan(
                file,
                state,
                pendingTail && quiet ? FileAction.Tail : FileAction.Skip,
                null,
                quiet
            );
        }

        return new FilePlan(file, state, FileAction.Tail, null, quiet);
    }

    /// <summary>
    /// Brings the index up to date for <paramref name="files"/> if any of them needs it. The check
    /// itself is one small read; the write gate is only taken when there is work to do, so a
    /// query over files that are already indexed never waits behind a backfill.
    /// </summary>
    private async Task EnsureFreshAsync(
        SqliteConnection connection,
        IReadOnlyList<LogFile> files,
        CancellationToken cancellationToken
    )
    {
        var states = await LoadFileStatesAsync(connection, cancellationToken);
        if (!NeedsWork(files, states))
            return;

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureFilesIndexedAsync(connection, files, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private bool NeedsWork(IReadOnlyList<LogFile> files, Dictionary<string, FileIndexState> states)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var file in files)
        {
            states.TryGetValue(file.Name, out var state);
            var plan = PlanFile(file, state, now);
            if (plan.Action != FileAction.Skip)
                return true;

            // Fully tailed, but the head is still unindexed and nothing is working on it: an
            // earlier backfill was interrupted (process exit, crash) and has to be resumed.
            if (state!.HeadScannedOffset > 0)
            {
                lock (_stateLock)
                {
                    if (!_queuedBackfills.Contains(state.Id))
                        return true;
                }
            }
        }

        return false;
    }

    private sealed record ColdLoad(FilePlan Plan, ILogParser Parser, long IngestFrom);

    /// <summary>Must be called while holding <see cref="_writeGate"/>.</summary>
    private async Task EnsureFilesIndexedAsync(
        SqliteConnection connection,
        IReadOnlyList<LogFile> files,
        CancellationToken cancellationToken
    )
    {
        var ingestStarted = Stopwatch.GetTimestamp();
        var states = await LoadFileStatesAsync(connection, cancellationToken);
        var now = DateTimeOffset.UtcNow;

        var tails = new List<FilePlan>();
        var rebuilds = new List<FilePlan>();
        var backfills = new List<PendingBackfill>();
        var filesSkipped = 0;
        var totalEntries = 0L;
        var totalBytes = 0L;

        foreach (var file in files)
        {
            states.TryGetValue(file.Name, out var state);
            var plan = PlanFile(file, state, now);
            switch (plan.Action)
            {
                case FileAction.Tail:
                    tails.Add(plan);
                    break;
                case FileAction.Rebuild:
                    rebuilds.Add(plan);
                    break;
                default:
                    filesSkipped++;
                    if (state!.HeadScannedOffset > 0)
                        backfills.Add(new PendingBackfill(file, state.Id, state.HeadScannedOffset));
                    break;
            }
        }

        // Files that grew: index the new lines. These are appends of a few rows, so the indexes
        // stay live.
        foreach (var plan in tails)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileStarted = Stopwatch.GetTimestamp();
            var state = plan.State!;
            var parser = await _reader.DetectParserAsync(plan.File.FullPath, cancellationToken);

            await using var transaction = connection.BeginTransaction();
            var result = await IngestForwardAsync(
                connection,
                transaction,
                state.Id,
                plan.File,
                parser,
                state.LastScannedOffset,
                plan.Quiet,
                attachLeadingContinuation: true,
                cancellationToken
            );
            await UpdateFileProgressAsync(
                connection,
                transaction,
                state.Id,
                plan.File,
                result.Cursor,
                headScannedOffset: null,
                cancellationToken
            );
            await transaction.CommitAsync(cancellationToken);

            totalEntries += result.Entries;
            totalBytes += result.Cursor - state.LastScannedOffset;
            LogIndexed(plan.File, "tail", state.LastScannedOffset, result, fileStarted);

            if (state.HeadScannedOffset > 0)
                backfills.Add(new PendingBackfill(plan.File, state.Id, state.HeadScannedOffset));
        }

        // Files that are not indexed yet, newest first. Decide for each how much of it this
        // request indexes before answering; the rest is backfilled in the background.
        if (rebuilds.Count > 0)
        {
            rebuilds.Sort((a, b) => b.File.LastModified.CompareTo(a.File.LastModified));

            var coldLoads = new List<ColdLoad>(rebuilds.Count);
            var remaining = ColdSyncBudgetBytes;
            var syncBytes = 0L;

            foreach (var plan in rebuilds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var file = plan.File;
                var parser = await _reader.DetectParserAsync(file.FullPath, cancellationToken);

                long ingestFrom = 0;
                if (_backgroundIndexing && file.SizeBytes >= remaining)
                {
                    var window = Math.Min(TailWindowBytes, remaining);
                    ingestFrom =
                        window > 0
                            ? await FindEntryStartAsync(
                                file.FullPath,
                                file.SizeBytes - window,
                                file.SizeBytes,
                                parser,
                                cancellationToken
                            )
                            : -1;

                    // Budget spent, or no entry starts inside the window: leave the whole file
                    // to the backfill, which starts from the end of its last complete line.
                    if (ingestFrom < 0)
                    {
                        ingestFrom = await FindLastLineEndAsync(
                            file.FullPath,
                            file.SizeBytes,
                            cancellationToken
                        );
                    }
                }

                var bytes = Math.Max(0, file.SizeBytes - ingestFrom);
                remaining = Math.Max(0, remaining - bytes);
                syncBytes += bytes;
                coldLoads.Add(new ColdLoad(plan, parser, ingestFrom));
            }

            // Maintaining eight secondary indexes row by row costs several times the insert
            // itself, so a large load into a mostly-empty index drops them and builds them once
            // at the end. They are indexes over the whole table, though: with other files already
            // indexed, recreating them re-sorts all of those rows too, and doing that per file
            // made a cold start quadratic in the number of files. So it happens at most once per
            // request, around every file together, and only when the new rows are at least as
            // many bytes of log as the index already holds.
            var rebuiltNames = rebuilds.Select(p => p.File.Name).ToHashSet(StringComparer.Ordinal);
            var existingBytes = states
                .Values.Where(s => !rebuiltNames.Contains(s.Name))
                .Sum(s => Math.Max(0, s.LastScannedOffset - s.HeadScannedOffset));
            var bulkLoad = syncBytes >= BulkLoadThresholdBytes && syncBytes >= existingBytes;

            if (bulkLoad)
            {
                // One transaction around the whole load. Readers keep seeing the previous,
                // fully indexed snapshot until it commits, and a failure or a cancelled request
                // rolls the dropped indexes back instead of leaving the table without them.
                await using var transaction = connection.BeginTransaction();
                await DropSecondaryIndexesAsync(connection, transaction, cancellationToken);

                foreach (var load in coldLoads)
                {
                    var loaded = await ColdLoadFileAsync(
                        connection,
                        transaction,
                        load,
                        backfills,
                        cancellationToken
                    );
                    totalEntries += loaded.Entries;
                    totalBytes += load.Plan.File.SizeBytes - load.IngestFrom;
                }

                var reindexStarted = Stopwatch.GetTimestamp();
                await CreateSecondaryIndexesAsync(connection, transaction, cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                _logger.LogDebug(
                    "Rebuilt secondary indexes after bulk load of {FileCount} file(s) in {ElapsedMs} ms",
                    coldLoads.Count,
                    (long)Stopwatch.GetElapsedTime(reindexStarted).TotalMilliseconds
                );
            }
            else
            {
                foreach (var load in coldLoads)
                {
                    await using var transaction = connection.BeginTransaction();
                    var loaded = await ColdLoadFileAsync(
                        connection,
                        transaction,
                        load,
                        backfills,
                        cancellationToken
                    );
                    await transaction.CommitAsync(cancellationToken);

                    totalEntries += loaded.Entries;
                    totalBytes += load.Plan.File.SizeBytes - load.IngestFrom;
                }
            }
        }

        if (rebuilds.Count > 0 || tails.Count > 0)
        {
            var ingestElapsed = Stopwatch.GetElapsedTime(ingestStarted);
            _logger.LogDebug(
                "Index ingest completed in {ElapsedMs} ms: {FilesRebuilt} rebuilt, {FilesTailed} tailed, {FilesSkipped} up-to-date, {TotalEntries} entries, {TotalBytes} bytes ({EntriesPerSecond:F0} entries/sec)",
                (long)ingestElapsed.TotalMilliseconds,
                rebuilds.Count,
                tails.Count,
                filesSkipped,
                totalEntries,
                totalBytes,
                totalEntries / Math.Max(ingestElapsed.TotalSeconds, 0.001)
            );
        }

        foreach (var pending in backfills)
            EnqueueBackfill(pending);
    }

    /// <summary>
    /// Replaces whatever the index holds for one file with a fresh load from
    /// <see cref="ColdLoad.IngestFrom"/> to the end of the file. Bytes before that offset are
    /// recorded as pending and queued for the backfill.
    /// </summary>
    private async Task<IngestResult> ColdLoadFileAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ColdLoad load,
        List<PendingBackfill> backfills,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var fileStarted = Stopwatch.GetTimestamp();
        var file = load.Plan.File;

        _logger.LogDebug(
            "Index rebuild for {LogFileName}: reason={RebuildReason}, size={FileSizeBytes} bytes, indexing from offset {IngestOffset}, {HeadBytes} bytes deferred to backfill",
            file.Name,
            load.Plan.RebuildReason,
            file.SizeBytes,
            load.IngestFrom,
            load.IngestFrom
        );

        await DeleteFileIndexAsync(connection, transaction, file.Name, cancellationToken);
        var fileId = await InsertFileAsync(connection, transaction, file, load.IngestFrom, cancellationToken);

        var result = await IngestForwardAsync(
            connection,
            transaction,
            fileId,
            file,
            load.Parser,
            load.IngestFrom,
            load.Plan.Quiet,
            attachLeadingContinuation: false,
            cancellationToken
        );
        await UpdateFileProgressAsync(
            connection,
            transaction,
            fileId,
            file,
            result.Cursor,
            headScannedOffset: load.IngestFrom,
            cancellationToken
        );

        LogIndexed(file, load.IngestFrom > 0 ? "tail-first" : "rebuild", load.IngestFrom, result, fileStarted);

        if (load.IngestFrom > 0)
            backfills.Add(new PendingBackfill(file, fileId, load.IngestFrom));

        return result;
    }

    private void LogIndexed(
        LogFile file,
        string mode,
        long startOffset,
        IngestResult result,
        long startedTimestamp
    )
    {
        var elapsed = Stopwatch.GetElapsedTime(startedTimestamp);
        _logger.LogDebug(
            "Indexed {LogFileName} in {ElapsedMs} ms: mode={IndexMode}, {EntriesInserted} entries from {LinesRead} lines ({ContinuationLines} continuation, {DroppedLines} unparseable), bytes {StartOffset}-{EndOffset} ({EntriesPerSecond:F0} entries/sec)",
            file.Name,
            (long)elapsed.TotalMilliseconds,
            mode,
            result.Entries,
            result.Lines,
            result.ContinuationLines,
            result.DroppedLines,
            startOffset,
            result.Cursor,
            result.Entries / Math.Max(elapsed.TotalSeconds, 0.001)
        );
    }

    private readonly record struct IngestResult(
        long Entries,
        long Cursor,
        long Lines,
        long ContinuationLines,
        long DroppedLines
    );

    /// <summary>
    /// Indexes the complete lines of <paramref name="file"/> from <paramref name="fromOffset"/>
    /// up to the size the file had when it was listed. The returned cursor is where the next scan
    /// must start: the end of the last line consumed.
    /// </summary>
    private async Task<IngestResult> IngestForwardAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long fileId,
        LogFile file,
        ILogParser parser,
        long fromOffset,
        bool quiet,
        bool attachLeadingContinuation,
        CancellationToken cancellationToken
    )
    {
        using var ingester = new LineIngester(
            connection,
            transaction,
            fileId,
            parser,
            Path.GetFileName(file.FullPath),
            attachLeadingContinuation
        );
        await using var lines = LogLineReader.Open(file.FullPath, fromOffset, file.SizeBytes);

        while (true)
        {
            while (lines.TryReadLine(out var start, out var length, out var offset))
                ingester.Add(lines.Buffer, start, length, offset);

            cancellationToken.ThrowIfCancellationRequested();
            if (!await lines.FillAsync(cancellationToken))
                break;
        }

        // What is left has no line break. On a file that is being written it may be half a line,
        // and indexing it now would store a truncated entry (or, for JSON, lose it: the partial
        // line fails to parse and the next scan would start in the middle of it). So it is only
        // taken when it is known to be whole: the file has gone quiet, or it is a single-line
        // format and the text parses as a complete entry. Otherwise the cursor stays in front of
        // it and the next scan reads it again.
        var cursor = lines.Position;
        if (lines.TryReadRemainder(out var tailStart, out var tailLength, out var tailOffset))
        {
            if (quiet)
            {
                ingester.Add(lines.Buffer, tailStart, tailLength, tailOffset);
                cursor = lines.Position;
            }
            else if (
                !ingester.AcceptsContinuationLines
                && ingester.Add(lines.Buffer, tailStart, tailLength, tailOffset, countDropped: false)
            )
            {
                cursor = lines.Position;
            }
        }

        ingester.Complete();
        return new IngestResult(
            ingester.Entries,
            cursor,
            ingester.Lines,
            ingester.ContinuationLines,
            ingester.DroppedLines
        );
    }

    /// <summary>
    /// Turns lines into index rows. Synchronous on purpose: it runs once per line, and the only
    /// awaiting the ingest needs is refilling the read buffer, which its caller does.
    /// </summary>
    private sealed class LineIngester : IDisposable
    {
        private static readonly int MaxContinuationBytes = IndexLimits.MaxExceptionChars * 3;

        private readonly SqliteConnection _connection;
        private readonly SqliteTransaction _transaction;
        private readonly long _fileId;
        private readonly ILogParser _parser;
        private readonly IIndexLineParser? _fast;
        private readonly string _fileName;
        private readonly bool _attachLeadingContinuation;

        private readonly SqliteCommand _insert;
        private readonly SqliteParameter _offsetParam;
        private readonly SqliteParameter _timestampParam;
        private readonly SqliteParameter _levelParam;
        private readonly SqliteParameter _messageParam;
        private readonly SqliteParameter _exceptionParam;
        private readonly SqliteParameter _sourceParam;
        private readonly SqliteParameter _correlationParam;
        private readonly SqliteParameter _requestParam;

        private bool _hasPending;
        private IndexLineFields _pending;
        private long _pendingOffset;
        private bool _seenEntry;
        private long _firstLineOffset = -1;
        private byte[] _continuation = [];
        private int _continuationLength;
        private readonly long[] _levelCounts = new long[8];

        public long Entries { get; private set; }
        public long Lines { get; private set; }
        public long ContinuationLines { get; private set; }
        public long DroppedLines { get; private set; }
        public bool AcceptsContinuationLines { get; }

        public LineIngester(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long fileId,
            ILogParser parser,
            string fileName,
            bool attachLeadingContinuation
        )
        {
            _connection = connection;
            _transaction = transaction;
            _fileId = fileId;
            _parser = parser;
            _fast = parser as IIndexLineParser;
            _fileName = fileName;
            _attachLeadingContinuation = attachLeadingContinuation;
            AcceptsContinuationLines = _fast?.AcceptsContinuationLines ?? false;

            _insert = connection.CreateCommand();
            _insert.Transaction = transaction;
            _insert.CommandText =
                """
                INSERT OR IGNORE INTO entries (
                    file_id, offset, timestamp_ticks, level,
                    message, exception, source_context, correlation_id, request_id
                )
                VALUES (
                    $file_id, $offset, $timestamp_ticks, $level,
                    $message, $exception, $source_context, $correlation_id, $request_id
                );
                """;
            _insert.Parameters.Add("$file_id", SqliteType.Integer).Value = fileId;
            _offsetParam = _insert.Parameters.Add("$offset", SqliteType.Integer);
            _timestampParam = _insert.Parameters.Add("$timestamp_ticks", SqliteType.Integer);
            _levelParam = _insert.Parameters.Add("$level", SqliteType.Integer);
            _messageParam = _insert.Parameters.Add("$message", SqliteType.Text);
            _exceptionParam = _insert.Parameters.Add("$exception", SqliteType.Text);
            _sourceParam = _insert.Parameters.Add("$source_context", SqliteType.Text);
            _correlationParam = _insert.Parameters.Add("$correlation_id", SqliteType.Text);
            _requestParam = _insert.Parameters.Add("$request_id", SqliteType.Text);
        }

        /// <summary>Returns true when the line started an entry.</summary>
        public bool Add(byte[] buffer, int start, int length, long offset, bool countDropped = true)
        {
            var line = buffer.AsSpan(start, length);
            if (LogLineReader.IsBlank(line))
                return false;

            Lines++;
            if (_firstLineOffset < 0)
                _firstLineOffset = offset;

            var fields = default(IndexLineFields);
            var startsEntry = _fast is not null
                ? _fast.TryParseIndexLine(line, ref fields)
                : TryParseThroughFullEntry(line, offset, ref fields);

            if (startsEntry)
            {
                Flush();
                _pending = fields;
                _pendingOffset = offset;
                _hasPending = true;
                _seenEntry = true;

                // A single-line format has nothing more to wait for.
                if (!AcceptsContinuationLines)
                    Flush();
                return true;
            }

            // A line that starts no entry belongs to the entry before it. When the scan itself
            // began on such lines, that entry was indexed by an earlier scan.
            if (AcceptsContinuationLines && (_hasPending || (_attachLeadingContinuation && !_seenEntry)))
            {
                AppendContinuation(line);
                ContinuationLines++;
                return false;
            }

            if (countDropped)
                DroppedLines++;
            else
                Lines--;
            return false;
        }

        /// <summary>For a parser with no index fast path: parse the full entry and keep its fields.</summary>
        private bool TryParseThroughFullEntry(ReadOnlySpan<byte> line, long offset, ref IndexLineFields fields)
        {
            var entry = _parser.Parse(Encoding.UTF8.GetString(line), _fileName, offset);
            if (entry is null)
                return false;

            fields.TimestampUtcTicks = entry.Timestamp.UtcTicks;
            fields.Level = entry.Level;
            fields.Message = IndexLimits.Truncate(entry.Message, IndexLimits.MaxMessageChars);
            fields.Exception = entry.Exception is null
                ? null
                : IndexLimits.Truncate(entry.Exception, IndexLimits.MaxExceptionChars);
            fields.SourceContext = entry.SourceContext;
            fields.CorrelationId = entry.CorrelationId;
            fields.RequestId = entry.RequestId;
            return true;
        }

        private void AppendContinuation(ReadOnlySpan<byte> line)
        {
            if (_continuationLength >= MaxContinuationBytes)
                return;

            var needed = _continuationLength + line.Length + 1;
            if (needed > _continuation.Length)
                Array.Resize(ref _continuation, Math.Max(needed, Math.Max(4096, _continuation.Length * 2)));

            if (_continuationLength > 0)
                _continuation[_continuationLength++] = (byte)'\n';
            line.CopyTo(_continuation.AsSpan(_continuationLength));
            _continuationLength += line.Length;
        }

        private string? TakeContinuation()
        {
            if (_continuationLength == 0)
                return null;

            var text = Encoding.UTF8.GetString(_continuation, 0, _continuationLength);
            _continuationLength = 0;
            return IndexLimits.Truncate(text, IndexLimits.MaxExceptionChars);
        }

        private void Flush()
        {
            var continuation = TakeContinuation();

            if (!_hasPending)
            {
                if (continuation is not null)
                    AttachToPreviousEntry(continuation);
                return;
            }

            _hasPending = false;
            var level = (int)_pending.Level;

            _offsetParam.Value = _pendingOffset;
            _timestampParam.Value = _pending.TimestampUtcTicks;
            _levelParam.Value = level;
            _messageParam.Value = _pending.Message;
            _exceptionParam.Value = (object?)(continuation ?? _pending.Exception) ?? DBNull.Value;
            _sourceParam.Value = (object?)_pending.SourceContext ?? DBNull.Value;
            _correlationParam.Value = (object?)_pending.CorrelationId ?? DBNull.Value;
            _requestParam.Value = (object?)_pending.RequestId ?? DBNull.Value;

            // INSERT OR IGNORE reports 0 rows for an entry that is already indexed, so the level
            // counts only ever include rows that really went in.
            if (_insert.ExecuteNonQuery() > 0)
            {
                Entries++;
                if ((uint)level < (uint)_levelCounts.Length)
                    _levelCounts[level]++;
            }
        }

        /// <summary>
        /// Appends lines read at the very start of a scan to the last entry indexed before it:
        /// the rest of a stack trace whose first lines an earlier scan had already indexed.
        /// </summary>
        private void AttachToPreviousEntry(string continuation)
        {
            using var command = _connection.CreateCommand();
            command.Transaction = _transaction;
            command.CommandText =
                """
                UPDATE entries
                SET exception = substr(
                    CASE
                        WHEN exception IS NULL OR exception = '' THEN $text
                        ELSE exception || char(10) || $text
                    END,
                    1,
                    $max
                )
                WHERE id = (
                    SELECT id FROM entries
                    WHERE file_id = $file_id AND offset < $offset
                    ORDER BY offset DESC
                    LIMIT 1
                );
                """;
            command.Parameters.AddWithValue("$text", continuation);
            command.Parameters.AddWithValue("$max", IndexLimits.MaxExceptionChars);
            command.Parameters.AddWithValue("$file_id", _fileId);
            command.Parameters.AddWithValue("$offset", _firstLineOffset);
            command.ExecuteNonQuery();
        }

        /// <summary>Writes the last pending entry and the per-level counts of this scan.</summary>
        public void Complete()
        {
            Flush();

            using var command = _connection.CreateCommand();
            command.Transaction = _transaction;
            command.CommandText =
                """
                INSERT INTO file_levels (file_id, level, entry_count)
                VALUES ($file_id, $level, $count)
                ON CONFLICT(file_id, level) DO UPDATE SET
                    entry_count = entry_count + excluded.entry_count;
                """;
            command.Parameters.AddWithValue("$file_id", _fileId);
            var levelParam = command.Parameters.Add("$level", SqliteType.Integer);
            var countParam = command.Parameters.Add("$count", SqliteType.Integer);

            for (var level = 0; level < _levelCounts.Length; level++)
            {
                if (_levelCounts[level] == 0)
                    continue;

                levelParam.Value = level;
                countParam.Value = _levelCounts[level];
                command.ExecuteNonQuery();
                _levelCounts[level] = 0;
            }
        }

        public void Dispose() => _insert.Dispose();
    }

    // ------------------------------------------------------------------------------------------
    // Finding entry boundaries
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Offset of the first line that starts an entry at or after <paramref name="target"/> and
    /// before <paramref name="limit"/>, or -1 when there is none.
    /// <para>
    /// A byte offset chosen by size usually lands mid-line, and in a plain-text log a line start
    /// may still be the middle of a stack trace. Ranges of a file are indexed independently, so
    /// each has to begin on an entry: otherwise the entry straddling the boundary would lose the
    /// lines that fall in the other range.
    /// </para>
    /// </summary>
    private static async Task<long> FindEntryStartAsync(
        string filePath,
        long target,
        long limit,
        ILogParser parser,
        CancellationToken cancellationToken
    )
    {
        if (target <= 0)
            return 0;
        if (target >= limit)
            return -1;

        // Start one byte early: the first "line" read is then the rest of whichever line holds
        // that byte (empty when the byte is itself a line break), and every line after it is a
        // real one.
        await using var lines = LogLineReader.Open(filePath, target - 1, limit);
        var skippedPartialLine = false;

        while (true)
        {
            while (lines.TryReadLine(out var start, out var length, out var offset))
            {
                if (!skippedPartialLine)
                {
                    skippedPartialLine = true;
                    continue;
                }

                if (StartsEntry(parser, lines.Buffer, start, length))
                    return offset;
            }

            if (!await lines.FillAsync(cancellationToken))
                return -1;
        }
    }

    private static bool StartsEntry(ILogParser parser, byte[] buffer, int start, int length)
    {
        var line = buffer.AsSpan(start, length);
        if (LogLineReader.IsBlank(line))
            return false;

        if (parser is IIndexLineParser fast)
        {
            var fields = default(IndexLineFields);
            return fast.TryParseIndexLine(line, ref fields);
        }

        return parser.Parse(Encoding.UTF8.GetString(line), string.Empty, 0) is not null;
    }

    /// <summary>
    /// Offset just past the last line break before <paramref name="size"/>: the end of the file's
    /// last complete line. 0 when the file has no line break at all.
    /// </summary>
    private static async Task<long> FindLastLineEndAsync(
        string filePath,
        long size,
        CancellationToken cancellationToken
    )
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1,
            FileOptions.Asynchronous
        );

        var buffer = new byte[64 * 1024];
        var end = Math.Min(size, stream.Length);

        while (end > 0)
        {
            var blockStart = Math.Max(0, end - buffer.Length);
            var wanted = (int)(end - blockStart);
            stream.Seek(blockStart, SeekOrigin.Begin);

            var read = 0;
            while (read < wanted)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read, wanted - read), cancellationToken);
                if (n == 0)
                    break;
                read += n;
            }

            var newline = read > 0 ? Array.LastIndexOf(buffer, (byte)'\n', read - 1, read) : -1;
            if (newline >= 0)
                return blockStart + newline + 1;

            end = blockStart;
        }

        return 0;
    }

    // ------------------------------------------------------------------------------------------
    // Background backfill
    // ------------------------------------------------------------------------------------------

    private sealed record PendingBackfill(LogFile File, long FileId, long HeadEndOffset);

    private readonly record struct HeadSliceResult(long EntriesInserted, long NewHeadOffset);

    private void EnqueueBackfill(PendingBackfill pending)
    {
        lock (_stateLock)
        {
            if (!_queuedBackfills.Add(pending.FileId))
                return;

            _backfillQueue.Add(pending);
            if (_backfillWorkerRunning)
                return;

            _backfillWorkerRunning = true;
        }

        _ = Task.Run(BackfillWorkerAsync);
    }

    /// <summary>
    /// Backfills queued files one at a time, newest first. One worker rather than one task per
    /// file: the tasks would all contend for the same write gate anyway, and a query that needs
    /// the gate would have to queue behind every one of them.
    /// </summary>
    private async Task BackfillWorkerAsync()
    {
        while (true)
        {
            PendingBackfill next;
            lock (_stateLock)
            {
                if (_backfillQueue.Count == 0)
                {
                    _backfillWorkerRunning = false;
                    return;
                }

                var newest = 0;
                for (var i = 1; i < _backfillQueue.Count; i++)
                {
                    if (_backfillQueue[i].File.LastModified > _backfillQueue[newest].File.LastModified)
                        newest = i;
                }

                next = _backfillQueue[newest];
                _backfillQueue.RemoveAt(newest);
            }

            try
            {
                await RunBackfillAsync(next);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Head backfill failed for {LogFileName}; the file remains partially indexed and will resume on the next query",
                    next.File.Name
                );
            }
            finally
            {
                // Release the marker so an interrupted or failed backfill can be queued again by
                // a later query.
                lock (_stateLock)
                    _queuedBackfills.Remove(next.FileId);
            }
        }
    }

    /// <summary>
    /// Indexes the not-yet-indexed head of a file, working backwards from the low-water mark so
    /// the newest still-missing entries land first.
    /// </summary>
    private async Task RunBackfillAsync(PendingBackfill pending)
    {
        var started = Stopwatch.GetTimestamp();
        var totalEntries = 0L;

        _logger.LogDebug(
            "Starting head backfill for {LogFileName}: {HeadBytes} bytes still unindexed",
            pending.File.Name,
            pending.HeadEndOffset
        );

        var parser = await _reader.DetectParserAsync(pending.File.FullPath);

        // The backfill must never hold the write gate for its whole run. It works in slices,
        // taking the gate per slice and releasing it in between. Each slice commits and advances
        // head_scanned_offset, so progress survives a crash.
        var cursor = pending.HeadEndOffset;
        while (cursor > 0)
        {
            await _writeGate.WaitAsync();
            try
            {
                await using var connection = OpenConnection();

                // A megabyte of short lines is thousands of rows and takes over a hundred
                // milliseconds; a megabyte of large JSON events is a few dozen rows and takes a
                // few. Pausing after every slice regardless made the pause, not the work, the
                // bulk of a backfill of large-line files, so slices are run back to back until a
                // burst's worth of time has been spent, and only then is the gate released.
                var burstStarted = Stopwatch.GetTimestamp();
                do
                {
                    var state = await GetFileStateByIdAsync(
                        connection,
                        pending.FileId,
                        CancellationToken.None
                    );

                    // File was rebuilt or removed under us; the backfill is void.
                    if (state is null || state.HeadScannedOffset != cursor)
                        return;

                    var slice = await IngestHeadSliceAsync(connection, state, pending.File, parser, cursor);
                    totalEntries += slice.EntriesInserted;
                    cursor = slice.NewHeadOffset;
                } while (cursor > 0 && Stopwatch.GetElapsedTime(burstStarted) < HeadBackfillBurst);
            }
            finally
            {
                _writeGate.Release();
            }

            await Task.Delay(HeadBackfillSliceDelay);
        }

        _logger.LogDebug(
            "Head backfill for {LogFileName} completed in {ElapsedMs} ms: {EntriesInserted} entries over {BytesScanned} bytes",
            pending.File.Name,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            totalEntries,
            pending.HeadEndOffset
        );
    }

    /// <summary>
    /// Indexes one slice of the unindexed head, ending at <paramref name="endOffset"/>. Returns
    /// the rows written and the new low-water mark, which is 0 once the head is fully indexed.
    /// </summary>
    private async Task<HeadSliceResult> IngestHeadSliceAsync(
        SqliteConnection connection,
        FileIndexState state,
        LogFile file,
        ILogParser parser,
        long endOffset
    )
    {
        // The slice has to begin on an entry. If none starts in the last slice's worth of bytes
        // (one enormous stack trace, or text that is not a log at all) widen it until one does.
        long sliceStart = 0;
        var step = HeadBackfillSliceBytes;
        var target = Math.Max(0, endOffset - step);
        while (target > 0)
        {
            var found = await FindEntryStartAsync(
                file.FullPath,
                target,
                endOffset,
                parser,
                CancellationToken.None
            );
            if (found >= 0)
            {
                sliceStart = found;
                break;
            }

            step *= 2;
            target = Math.Max(0, endOffset - step);
        }

        await using var transaction = connection.BeginTransaction();
        long entries;
        using (
            var ingester = new LineIngester(
                connection,
                transaction,
                state.Id,
                parser,
                Path.GetFileName(file.FullPath),
                attachLeadingContinuation: false
            )
        )
        {
            await using var lines = LogLineReader.Open(file.FullPath, sliceStart, endOffset);
            while (true)
            {
                while (lines.TryReadLine(out var start, out var length, out var offset))
                    ingester.Add(lines.Buffer, start, length, offset);

                if (!await lines.FillAsync(CancellationToken.None))
                    break;
            }

            ingester.Complete();
            entries = ingester.Entries;
        }

        // Advance the low-water mark in the same transaction as the rows themselves.
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE files SET head_scanned_offset = $head WHERE id = $id;";
            command.Parameters.AddWithValue("$head", sliceStart);
            command.Parameters.AddWithValue("$id", state.Id);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return new HeadSliceResult(entries, sliceStart);
    }

    // ------------------------------------------------------------------------------------------
    // Counting and paging
    // ------------------------------------------------------------------------------------------

    private static async Task<int> CountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<LogFile> files,
        LogQuery query,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;

        if (FiltersOnlyByFileAndLevel(query))
        {
            // No row needs to be looked at: the per-file, per-level counts are kept up to date
            // with the rows themselves.
            var predicates = new List<string> { BuildFilePredicate(command, files, "c.file_id") };
            AddLevelPredicate(command, query, "c.level", predicates);
            command.CommandText =
                $"SELECT COALESCE(SUM(c.entry_count), 0) FROM file_levels c WHERE {string.Join(" AND ", predicates)};";
        }
        else
        {
            command.CommandText =
                $"SELECT COUNT(*) FROM entries e WHERE {string.Join(" AND ", BuildPredicates(command, files, query, includeLevels: true))};";
        }

        var count = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        return count > int.MaxValue ? int.MaxValue : (int)count;
    }

    private static bool FiltersOnlyByFileAndLevel(LogQuery query) =>
        !query.From.HasValue
        && !query.To.HasValue
        && string.IsNullOrWhiteSpace(query.SearchText)
        && string.IsNullOrWhiteSpace(query.SourceContext)
        && string.IsNullOrWhiteSpace(query.CorrelationId)
        && string.IsNullOrWhiteSpace(query.RequestId);

    private const string PageColumns =
        """
        f.name AS name,
        e.offset AS offset,
        e.timestamp_ticks AS timestamp_ticks,
        e.level AS level,
        e.message AS message,
        e.exception AS exception,
        e.source_context AS source_context,
        e.correlation_id AS correlation_id,
        e.request_id AS request_id
        """;

    /// <summary>
    /// The same shape without the text columns, for callers that only need to know where each
    /// entry is because they re-read it from the log file anyway. An export of 50,000 entries
    /// would otherwise hold 50,000 messages and stack traces in memory just to discard them.
    /// </summary>
    private const string LocateColumns =
        """
        f.name AS name,
        e.offset AS offset,
        e.timestamp_ticks AS timestamp_ticks,
        e.level AS level,
        '' AS message,
        NULL AS exception,
        NULL AS source_context,
        NULL AS correlation_id,
        NULL AS request_id
        """;

    /// <summary>
    /// Returns one page of matching entries. Every column the grid renders is read from the index
    /// row itself, so callers can show a page without touching the log files; the offset is kept
    /// so the full entry can still be re-read on demand. With <paramref name="includeTotal"/> the
    /// statement also returns the total match count via a window function, which costs a full
    /// scan of the matches and is therefore only used when the WHERE clause already forces one.
    /// </summary>
    private static async Task<(IReadOnlyList<IndexedLogPointer> Pointers, long? Total)> GetPageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<LogFile> files,
        LogQuery query,
        bool includeTotal,
        bool locateOnly,
        CancellationToken cancellationToken
    )
    {
        var offset = (long)Math.Max(0, query.Page - 1) * query.PageSize;
        var columns = locateOnly ? LocateColumns : PageColumns;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;

        var levels = query.Levels?.Distinct().ToList();
        var sortsByTime = IsTimestampSort(query);

        if (!includeTotal && sortsByTime && levels is { Count: > 1 })
        {
            // Several levels at once. A single "level IN (...)" makes SQLite gather every match
            // and sort it before it can return the first page (290 ms at a million rows, against
            // 2 ms for one level). Each level on its own is already in time order in the index,
            // so take the top of each and merge those few rows instead.
            var direction = query.SortDescending ? "DESC" : "ASC";
            var shared = string.Join(" AND ", BuildPredicates(command, files, query, includeLevels: false));
            var branches = new List<string>(levels.Count);
            for (var i = 0; i < levels.Count; i++)
            {
                var name = "$merge_level" + i;
                command.Parameters.AddWithValue(name, (int)levels[i]);
                branches.Add(
                    $"""
                    SELECT * FROM (
                        SELECT {columns}, e.id AS id
                        FROM entries e
                        JOIN files f ON f.id = e.file_id
                        WHERE {shared} AND e.level = {name}
                        ORDER BY e.timestamp_ticks {direction}, e.id {direction}
                        LIMIT $merge_limit
                    )
                    """
                );
            }

            command.CommandText =
                $"""
                SELECT name, offset, timestamp_ticks, level, message, exception,
                       source_context, correlation_id, request_id
                FROM (
                {string.Join("\nUNION ALL\n", branches)}
                )
                ORDER BY timestamp_ticks {direction}, id {direction}
                LIMIT $limit OFFSET $offset;
                """;
            command.Parameters.AddWithValue("$merge_limit", offset + query.PageSize);
        }
        else
        {
            command.CommandText =
                $"""
                SELECT {columns}{(includeTotal ? ", COUNT(*) OVER () AS total" : string.Empty)}
                FROM entries e
                JOIN files f ON f.id = e.file_id
                WHERE {string.Join(" AND ", BuildPredicates(command, files, query, includeLevels: true))}
                {BuildOrderBy(query)}
                LIMIT $limit OFFSET $offset;
                """;
        }

        command.Parameters.AddWithValue("$limit", query.PageSize);
        command.Parameters.AddWithValue("$offset", offset);

        var results = new List<IndexedLogPointer>(Math.Min(query.PageSize, 4096));
        long? total = null;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(
                new IndexedLogPointer(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    (LogLevel)reader.GetInt32(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8)
                )
            );
            if (includeTotal && total is null)
                total = reader.GetInt64(9);
        }

        return (results, total);
    }

    private static List<string> BuildPredicates(
        SqliteCommand command,
        IReadOnlyList<LogFile> files,
        LogQuery query,
        bool includeLevels
    )
    {
        var predicates = new List<string> { BuildFilePredicate(command, files, "e.file_id") };

        if (query.From.HasValue)
        {
            predicates.Add("e.timestamp_ticks >= $from");
            command.Parameters.AddWithValue("$from", query.From.Value.UtcTicks);
        }

        if (query.To.HasValue)
        {
            predicates.Add("e.timestamp_ticks <= $to");
            command.Parameters.AddWithValue("$to", query.To.Value.UtcTicks);
        }

        if (includeLevels)
            AddLevelPredicate(command, query, "e.level", predicates);

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            predicates.Add(
                "(e.message LIKE $search ESCAPE '\\' OR e.exception LIKE $search ESCAPE '\\' OR e.source_context LIKE $search ESCAPE '\\')"
            );
            command.Parameters.AddWithValue("$search", "%" + EscapeLike(query.SearchText) + "%");
        }

        if (!string.IsNullOrWhiteSpace(query.SourceContext))
        {
            predicates.Add("e.source_context = $source_context COLLATE NOCASE");
            command.Parameters.AddWithValue("$source_context", query.SourceContext);
        }

        if (!string.IsNullOrWhiteSpace(query.CorrelationId))
        {
            predicates.Add("e.correlation_id = $correlation_id COLLATE NOCASE");
            command.Parameters.AddWithValue("$correlation_id", query.CorrelationId);
        }

        if (!string.IsNullOrWhiteSpace(query.RequestId))
        {
            predicates.Add("e.request_id = $request_id COLLATE NOCASE");
            command.Parameters.AddWithValue("$request_id", query.RequestId);
        }

        return predicates;
    }

    private static void AddLevelPredicate(
        SqliteCommand command,
        LogQuery query,
        string column,
        List<string> predicates
    )
    {
        if (query.Levels is not { Count: > 0 })
            return;

        var levelParameters = new List<string>(query.Levels.Count);
        for (var i = 0; i < query.Levels.Count; i++)
        {
            var name = "$level" + i;
            levelParameters.Add(name);
            command.Parameters.AddWithValue(name, (int)query.Levels[i]);
        }

        predicates.Add($"{column} IN ({string.Join(",", levelParameters)})");
    }

    private static string BuildFilePredicate(
        SqliteCommand command,
        IReadOnlyList<LogFile> files,
        string column
    )
    {
        var indexed = LoadIndexedFileIds(command);

        // When every indexed file is selected — the all-files view — the predicate would only ask
        // whether each row belongs to a file it already belongs to. Dropping it lets SQLite walk
        // ix_entries_time straight into the page instead of filtering or sorting anything.
        if (SelectsAllIndexedFiles(indexed, files))
            return "1=1";

        var ids = new List<long>(files.Count);
        foreach (var file in files)
        {
            if (indexed.TryGetValue(file.Name, out var id))
                ids.Add(id);
        }

        if (ids.Count == 0)
            return "0=1";

        // Bind the ids themselves. A single file becomes an equality, which lets SQLite serve a
        // timestamp-ordered page straight from ix_entries_file_time — an
        // IN (SELECT id FROM files WHERE name IN (...)) form makes it pick a different index and
        // sort every row of the file first.
        if (ids.Count == 1)
        {
            command.Parameters.AddWithValue("$file_id", ids[0]);
            return $"{column} = $file_id";
        }

        var parameters = new List<string>(ids.Count);
        for (var i = 0; i < ids.Count; i++)
        {
            var name = "$file_id" + i;
            parameters.Add(name);
            command.Parameters.AddWithValue(name, ids[i]);
        }

        return $"{column} IN ({string.Join(",", parameters)})";
    }

    private static Dictionary<string, long> LoadIndexedFileIds(SqliteCommand command)
    {
        using var indexed = command.Connection!.CreateCommand();
        indexed.Transaction = command.Transaction;
        indexed.CommandText = "SELECT name, id FROM files;";

        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        using var reader = indexed.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetInt64(1);

        return result;
    }

    /// <summary>
    /// True when <paramref name="files"/> covers every file currently in the index. Compares the
    /// actual name sets, not just counts: a stale row (e.g. a log file deleted from disk) would
    /// otherwise let a partial selection match on count alone and silently widen the query.
    /// </summary>
    private static bool SelectsAllIndexedFiles(
        Dictionary<string, long> indexed,
        IReadOnlyList<LogFile> files
    )
    {
        if (indexed.Count != files.Count)
            return false;

        foreach (var file in files)
        {
            if (!indexed.ContainsKey(file.Name))
                return false;
        }

        return true;
    }

    private static bool IsTimestampSort(LogQuery query) =>
        query.SortBy.ToUpperInvariant() is not ("LEVEL" or "MESSAGE");

    private static string BuildOrderBy(LogQuery query)
    {
        var direction = query.SortDescending ? "DESC" : "ASC";
        var primary = query.SortBy.ToUpperInvariant() switch
        {
            "LEVEL" => $"e.level {direction}",
            "MESSAGE" => $"e.message COLLATE NOCASE {direction}",
            _ => $"e.timestamp_ticks {direction}",
        };

        // The tie-breaker must be the rowid, not a column from the joined files table. SQLite can
        // only stream a page straight out of an index when the ORDER BY is that index's key plus
        // rowid; any other trailing column forces it to materialise and sort every matching row.
        return $"ORDER BY {primary}, e.id {direction}";
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    // ------------------------------------------------------------------------------------------
    // Dashboard aggregates
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// The exact index state of one file that a cached aggregate is valid for. Ingest updates
    /// size/last-write/last-scanned in the same transaction as the rows, and the backfill advances
    /// head_scanned_offset the same way, so any change to the rows changes this key.
    /// </summary>
    private sealed record FileStatsKey(
        long SizeBytes,
        long LastWriteTicks,
        long LastScannedOffset,
        long HeadScannedOffset
    );

    private sealed class FileStatsSnapshot
    {
        public Dictionary<LogLevel, long> ByLevel { get; } = [];
        public Dictionary<long, long> ErrorsByHour { get; } = [];
        public Dictionary<long, long> LogsByDay { get; } = [];
        public Dictionary<string, long> BySource { get; } = new(StringComparer.Ordinal);
    }

    private sealed record CachedFileStats(FileStatsKey Key, FileStatsSnapshot Snapshot);

    /// <summary>
    /// Merges the per-file aggregates of <paramref name="files"/>, recomputing only those whose
    /// index state changed since they were last cached. Also evicts cache entries for files that
    /// have left the index.
    /// </summary>
    private async Task<(FileStatsSnapshot Merged, int Recomputed)> GetMergedStatsAsync(
        SqliteConnection connection,
        IReadOnlyList<LogFile> files,
        CancellationToken cancellationToken
    )
    {
        var wanted = new HashSet<string>(files.Select(f => f.Name), StringComparer.Ordinal);
        var merged = new FileStatsSnapshot();
        var recomputed = 0;
        var states = await LoadFileStatesAsync(connection, cancellationToken);

        foreach (var state in states.Values)
        {
            if (!wanted.Contains(state.Name))
                continue;

            var key = new FileStatsKey(
                state.SizeBytes,
                state.LastWriteTicks,
                state.LastScannedOffset,
                state.HeadScannedOffset
            );

            CachedFileStats? cached;
            lock (_stateLock)
                _statsCache.TryGetValue(state.Id, out cached);

            if (cached is null || cached.Key != key)
            {
                // Computed outside the lock. If the file changes between reading its key and
                // aggregating it, the snapshot is stored under a key that is already stale and
                // simply gets recomputed on the next refresh.
                cached = new CachedFileStats(
                    key,
                    await ComputeFileStatsAsync(connection, state.Id, cancellationToken)
                );
                lock (_stateLock)
                    _statsCache[state.Id] = cached;
                recomputed++;
            }

            Merge(merged, cached.Snapshot);
        }

        var indexedIds = states.Values.Select(s => s.Id).ToHashSet();
        lock (_stateLock)
        {
            foreach (var stale in _statsCache.Keys.Where(id => !indexedIds.Contains(id)).ToList())
                _statsCache.Remove(stale);
        }

        return (merged, recomputed);
    }

    private static void Merge(FileStatsSnapshot into, FileStatsSnapshot from)
    {
        foreach (var (level, count) in from.ByLevel)
            into.ByLevel[level] = into.ByLevel.GetValueOrDefault(level) + count;
        foreach (var (hour, count) in from.ErrorsByHour)
            into.ErrorsByHour[hour] = into.ErrorsByHour.GetValueOrDefault(hour) + count;
        foreach (var (day, count) in from.LogsByDay)
            into.LogsByDay[day] = into.LogsByDay.GetValueOrDefault(day) + count;
        foreach (var (source, count) in from.BySource)
            into.BySource[source] = into.BySource.GetValueOrDefault(source) + count;
    }

    /// <summary>
    /// Aggregates one file. Every statement filters on <c>file_id</c>, so each walks one
    /// contiguous range of a file-leading index instead of the whole table.
    /// </summary>
    private static async Task<FileStatsSnapshot> ComputeFileStatsAsync(
        SqliteConnection connection,
        long fileId,
        CancellationToken cancellationToken
    )
    {
        var snapshot = new FileStatsSnapshot();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT level, entry_count FROM file_levels WHERE file_id = $id;";
            command.Parameters.AddWithValue("$id", fileId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                snapshot.ByLevel[(LogLevel)reader.GetInt32(0)] = reader.GetInt64(1);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT
                    ((e.timestamp_ticks / 36000000000) * 36000000000) AS hour_ticks,
                    COUNT(*)
                FROM entries e
                WHERE e.file_id = $id AND e.level >= $error_level
                GROUP BY hour_ticks;
                """;
            command.Parameters.AddWithValue("$id", fileId);
            command.Parameters.AddWithValue("$error_level", (int)LogLevel.Error);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                snapshot.ErrorsByHour[reader.GetInt64(0)] = reader.GetInt64(1);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT
                    ((e.timestamp_ticks / 864000000000) * 864000000000) AS day_ticks,
                    COUNT(*)
                FROM entries e
                WHERE e.file_id = $id
                GROUP BY day_ticks;
                """;
            command.Parameters.AddWithValue("$id", fileId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                snapshot.LogsByDay[reader.GetInt64(0)] = reader.GetInt64(1);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT e.source_context, COUNT(*)
                FROM entries e
                WHERE e.file_id = $id
                    AND e.source_context IS NOT NULL
                    AND e.source_context <> ''
                GROUP BY e.source_context;
                """;
            command.Parameters.AddWithValue("$id", fileId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                snapshot.BySource[reader.GetString(0)] = reader.GetInt64(1);
        }

        return snapshot;
    }

    // ------------------------------------------------------------------------------------------
    // The files table
    // ------------------------------------------------------------------------------------------

    private sealed record FileIndexState(
        long Id,
        string Name,
        string FullPath,
        long SizeBytes,
        long LastWriteTicks,
        long LastScannedOffset,
        long HeadScannedOffset
    );

    private const string FileStateColumns =
        "id, name, full_path, size_bytes, last_write_ticks, last_scanned_offset, head_scanned_offset";

    private static FileIndexState ReadFileState(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6)
        );

    /// <summary>Every indexed file, by name. One statement however many files there are.</summary>
    private static async Task<Dictionary<string, FileIndexState>> LoadFileStatesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken
    )
    {
        var states = new Dictionary<string, FileIndexState>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {FileStateColumns} FROM files;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var state = ReadFileState(reader);
            states[state.Name] = state;
        }

        return states;
    }

    private static async Task<FileIndexState?> GetFileStateByIdAsync(
        SqliteConnection connection,
        long fileId,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {FileStateColumns} FROM files WHERE id = $id;";
        command.Parameters.AddWithValue("$id", fileId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadFileState(reader) : null;
    }

    private static async Task<long> InsertFileAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LogFile file,
        long scannedOffset,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO files (
                name, full_path, size_bytes, last_write_ticks,
                last_scanned_offset, format, head_scanned_offset
            )
            VALUES (
                $name, $full_path, $size_bytes, $last_write_ticks,
                $scanned, $format, $scanned
            )
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$name", file.Name);
        command.Parameters.AddWithValue("$full_path", file.FullPath);
        command.Parameters.AddWithValue("$size_bytes", file.SizeBytes);
        command.Parameters.AddWithValue("$last_write_ticks", file.LastModified.UtcTicks);
        command.Parameters.AddWithValue("$scanned", scannedOffset);
        command.Parameters.AddWithValue("$format", file.Format.ToString());

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task UpdateFileProgressAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long fileId,
        LogFile file,
        long lastScannedOffset,
        long? headScannedOffset,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE files
            SET
                full_path = $full_path,
                size_bytes = $size_bytes,
                last_write_ticks = $last_write_ticks,
                last_scanned_offset = $last_scanned_offset,
                format = $format,
                head_scanned_offset = COALESCE($head_scanned_offset, head_scanned_offset)
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", fileId);
        command.Parameters.AddWithValue("$full_path", file.FullPath);
        command.Parameters.AddWithValue("$size_bytes", file.SizeBytes);
        command.Parameters.AddWithValue("$last_write_ticks", file.LastModified.UtcTicks);
        command.Parameters.AddWithValue("$last_scanned_offset", lastScannedOffset);
        command.Parameters.AddWithValue("$format", file.Format.ToString());
        command.Parameters.AddWithValue(
            "$head_scanned_offset",
            headScannedOffset is { } head ? head : DBNull.Value
        );

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteFileIndexAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string fileName,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            DELETE FROM entries
            WHERE file_id IN (SELECT id FROM files WHERE name = $name);
            DELETE FROM file_levels
            WHERE file_id IN (SELECT id FROM files WHERE name = $name);
            DELETE FROM files
            WHERE name = $name;
            """;
        command.Parameters.AddWithValue("$name", fileName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // ------------------------------------------------------------------------------------------
    // Plumbing
    // ------------------------------------------------------------------------------------------

    private static async Task<int> GetUserVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqliteConnection OpenConnection()
    {
        // Private page cache (the default), not shared-cache mode: shared cache takes table-level
        // locks between connections of the same process, which would put readers back behind the
        // writer that WAL mode is there to keep them clear of.
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
            }.ToString()
        );
        connection.Open();

        // These are per-connection settings; journal_mode=WAL is persisted in the database file
        // itself and is set once during schema setup. The page cache lives in the host
        // application's process and pooled connections keep it alive, so it is kept modest; the
        // hot paths are index walks that need little of it. journal_size_limit hands the disk
        // space of a large write-ahead log back once it has been checkpointed.
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText =
                "PRAGMA synchronous=NORMAL; PRAGMA cache_size=-16384; PRAGMA temp_store=MEMORY; PRAGMA journal_size_limit=67108864;";
            pragma.ExecuteNonQuery();
        }

        return connection;
    }
}

internal sealed record IndexedQueryResult(IReadOnlyList<IndexedLogPointer> Items, int TotalCount);

/// <summary>
/// One index row: where the entry lives (<paramref name="FileName"/> is the folder-relative name,
/// <paramref name="Offset"/> the byte offset of its line) plus the indexed columns, enough to render
/// a grid row without re-reading the file. <paramref name="Message"/> and
/// <paramref name="Exception"/> are truncated to their indexed prefixes.
/// </summary>
internal sealed record IndexedLogPointer(
    string FileName,
    long Offset,
    long TimestampTicks,
    LogLevel Level,
    string Message,
    string? Exception,
    string? SourceContext,
    string? CorrelationId,
    string? RequestId
);
