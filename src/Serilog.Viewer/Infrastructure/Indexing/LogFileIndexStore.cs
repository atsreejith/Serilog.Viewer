using Microsoft.Data.Sqlite;
using Serilog.Viewer.Infrastructure.Reading;
using Serilog.Viewer.Models;

namespace Serilog.Viewer.Infrastructure.Indexing;

internal sealed class LogFileIndexStore
{
    private const int SchemaVersion = 1;

    private readonly string _databasePath;
    private readonly StreamingLogFileReader _reader;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public LogFileIndexStore(string logFolder, StreamingLogFileReader reader)
    {
        var indexFolder = Path.Combine(logFolder, ".serilog-viewer");
        _databasePath = Path.Combine(indexFolder, "index.sqlite");
        _reader = reader;
    }

    public async Task<IndexedQueryResult> QueryAsync(
        IReadOnlyList<LogFile> files,
        LogQuery query,
        CancellationToken cancellationToken = default
    )
    {
        if (files.Count == 0)
            return new IndexedQueryResult([], 0);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await InitializeAsync(cancellationToken);

            await using var connection = OpenConnection();
            await EnsureFilesIndexedAsync(connection, files, cancellationToken);

            var total = await CountAsync(connection, files, query, cancellationToken);
            var pointers = await GetPageAsync(connection, files, query, cancellationToken);

            return new IndexedQueryResult(pointers, total);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DashboardStats> GetStatsAsync(
        IReadOnlyList<LogFile> files,
        CancellationToken cancellationToken = default
    )
    {
        if (files.Count == 0)
            return new DashboardStats();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await InitializeAsync(cancellationToken);

            await using var connection = OpenConnection();
            await EnsureFilesIndexedAsync(connection, files, cancellationToken);

            var countsByLevel = await GetCountsByLevelAsync(connection, files, cancellationToken);
            var errorsByHour = await GetErrorsByHourAsync(connection, files, cancellationToken);
            var logsByDay = await GetLogsByDayAsync(connection, files, cancellationToken);
            var topSources = await GetTopSourcesAsync(connection, files, cancellationToken);

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
        finally
        {
            _gate.Release();
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);

        await using var connection = OpenConnection();
        await ExecuteNonQueryAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken);
        await ExecuteNonQueryAsync(connection, "PRAGMA synchronous=NORMAL;", cancellationToken);

        var currentVersion = await GetUserVersionAsync(connection, cancellationToken);
        if (currentVersion != SchemaVersion)
        {
            await ExecuteNonQueryAsync(connection, "DROP TABLE IF EXISTS entries;", cancellationToken);
            await ExecuteNonQueryAsync(connection, "DROP TABLE IF EXISTS files;", cancellationToken);
        }

        await ExecuteNonQueryAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS files (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE,
                full_path TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                last_write_ticks INTEGER NOT NULL,
                last_scanned_offset INTEGER NOT NULL,
                format TEXT NOT NULL
            );
            """,
            cancellationToken
        );
        await ExecuteNonQueryAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS entries (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id INTEGER NOT NULL,
                seq INTEGER NOT NULL,
                offset INTEGER NOT NULL,
                timestamp_ticks INTEGER NOT NULL,
                level INTEGER NOT NULL,
                message TEXT NOT NULL,
                exception TEXT NULL,
                source_context TEXT NULL COLLATE NOCASE,
                correlation_id TEXT NULL COLLATE NOCASE,
                request_id TEXT NULL COLLATE NOCASE,
                FOREIGN KEY(file_id) REFERENCES files(id) ON DELETE CASCADE
            );
            """,
            cancellationToken
        );
        await ExecuteNonQueryAsync(
            connection,
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_entries_file_seq ON entries(file_id, seq);",
            cancellationToken
        );
        await ExecuteNonQueryAsync(
            connection,
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_entries_file_offset ON entries(file_id, offset);",
            cancellationToken
        );
        await ExecuteNonQueryAsync(
            connection,
            "CREATE INDEX IF NOT EXISTS ix_entries_file_time ON entries(file_id, timestamp_ticks);",
            cancellationToken
        );
        await ExecuteNonQueryAsync(
            connection,
            "CREATE INDEX IF NOT EXISTS ix_entries_level_time ON entries(level, timestamp_ticks);",
            cancellationToken
        );
        await ExecuteNonQueryAsync(
            connection,
            "CREATE INDEX IF NOT EXISTS ix_entries_source ON entries(source_context);",
            cancellationToken
        );
        await ExecuteNonQueryAsync(
            connection,
            "CREATE INDEX IF NOT EXISTS ix_entries_correlation ON entries(correlation_id);",
            cancellationToken
        );
        await ExecuteNonQueryAsync(
            connection,
            "CREATE INDEX IF NOT EXISTS ix_entries_request ON entries(request_id);",
            cancellationToken
        );
        await ExecuteNonQueryAsync(
            connection,
            $"PRAGMA user_version={SchemaVersion};",
            cancellationToken
        );

        _initialized = true;
    }

    private async Task EnsureFilesIndexedAsync(
        SqliteConnection connection,
        IReadOnlyList<LogFile> files,
        CancellationToken cancellationToken
    )
    {
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var state = await GetFileStateAsync(connection, file, cancellationToken);
            var currentLastWriteTicks = file.LastModified.UtcTicks;
            var startOffset = state?.LastScannedOffset ?? 0;
            var nextSeq = state?.NextSeq ?? 0;
            var rebuild =
                state is null
                || !string.Equals(state.FullPath, file.FullPath, StringComparison.OrdinalIgnoreCase)
                || file.SizeBytes < startOffset
                || (file.SizeBytes == state.SizeBytes
                    && currentLastWriteTicks != state.LastWriteTicks);

            if (rebuild)
            {
                await DeleteFileIndexAsync(connection, file.Name, cancellationToken);
                state = await UpsertFileAsync(
                    connection,
                    file,
                    lastScannedOffset: 0,
                    cancellationToken
                );
                startOffset = 0;
                nextSeq = 0;
            }
            else if (file.SizeBytes == state!.SizeBytes && startOffset == file.SizeBytes)
            {
                continue;
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText =
                """
                INSERT OR IGNORE INTO entries (
                    file_id,
                    seq,
                    offset,
                    timestamp_ticks,
                    level,
                    message,
                    exception,
                    source_context,
                    correlation_id,
                    request_id
                )
                VALUES (
                    $file_id,
                    $seq,
                    $offset,
                    $timestamp_ticks,
                    $level,
                    $message,
                    $exception,
                    $source_context,
                    $correlation_id,
                    $request_id
                );
                """;

            var fileIdParam = insert.Parameters.Add("$file_id", SqliteType.Integer);
            var seqParam = insert.Parameters.Add("$seq", SqliteType.Integer);
            var offsetParam = insert.Parameters.Add("$offset", SqliteType.Integer);
            var timestampParam = insert.Parameters.Add("$timestamp_ticks", SqliteType.Integer);
            var levelParam = insert.Parameters.Add("$level", SqliteType.Integer);
            var messageParam = insert.Parameters.Add("$message", SqliteType.Text);
            var exceptionParam = insert.Parameters.Add("$exception", SqliteType.Text);
            var sourceParam = insert.Parameters.Add("$source_context", SqliteType.Text);
            var correlationParam = insert.Parameters.Add("$correlation_id", SqliteType.Text);
            var requestParam = insert.Parameters.Add("$request_id", SqliteType.Text);

            await foreach (
                var entry in _reader.ReadFromOffsetAsync(file.FullPath, startOffset, cancellationToken)
            )
            {
                fileIdParam.Value = state.Id;
                seqParam.Value = nextSeq++;
                offsetParam.Value = entry.LineOffset;
                timestampParam.Value = entry.Timestamp.UtcTicks;
                levelParam.Value = (int)entry.Level;
                messageParam.Value = entry.Message;
                exceptionParam.Value = (object?)entry.Exception ?? DBNull.Value;
                sourceParam.Value = (object?)entry.SourceContext ?? DBNull.Value;
                correlationParam.Value = (object?)entry.CorrelationId ?? DBNull.Value;
                requestParam.Value = (object?)entry.RequestId ?? DBNull.Value;

                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await UpdateFileMetadataAsync(
                connection,
                (SqliteTransaction)transaction,
                state.Id,
                file,
                file.SizeBytes,
                cancellationToken
            );
            await transaction.CommitAsync(cancellationToken);
        }
    }

    private async Task<int> CountAsync(
        SqliteConnection connection,
        IReadOnlyList<LogFile> files,
        LogQuery query,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM entries e {BuildWhere(command, files, query)};";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        var count = Convert.ToInt64(result);
        return count > int.MaxValue ? int.MaxValue : (int)count;
    }

    private async Task<IReadOnlyList<IndexedLogPointer>> GetPageAsync(
        SqliteConnection connection,
        IReadOnlyList<LogFile> files,
        LogQuery query,
        CancellationToken cancellationToken
    )
    {
        var offset = Math.Max(0, query.Page - 1) * query.PageSize;
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT f.name, e.offset
            FROM entries e
            JOIN files f ON f.id = e.file_id
            {BuildWhere(command, files, query)}
            {BuildOrderBy(query)}
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$limit", query.PageSize);
        command.Parameters.AddWithValue("$offset", offset);

        var results = new List<IndexedLogPointer>(query.PageSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(new IndexedLogPointer(reader.GetString(0), reader.GetInt64(1)));

        return results;
    }

    private async Task<Dictionary<LogLevel, long>> GetCountsByLevelAsync(
        SqliteConnection connection,
        IReadOnlyList<LogFile> files,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT e.level, COUNT(*) FROM entries e {BuildFileWhere(command, files)} GROUP BY e.level;";

        var result = new Dictionary<LogLevel, long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result[(LogLevel)reader.GetInt32(0)] = reader.GetInt64(1);

        return result;
    }

    private async Task<IReadOnlyList<TimeSeriesPoint>> GetErrorsByHourAsync(
        SqliteConnection connection,
        IReadOnlyList<LogFile> files,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT
                ((e.timestamp_ticks / 36000000000) * 36000000000) AS hour_ticks,
                COUNT(*)
            FROM entries e
            {BuildFileWhere(command, files)}
                AND e.level >= $error_level
            GROUP BY hour_ticks
            ORDER BY hour_ticks ASC;
            """;
        command.Parameters.AddWithValue("$error_level", (int)LogLevel.Error);

        var result = new List<TimeSeriesPoint>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(
                new TimeSeriesPoint
                {
                    Timestamp = new DateTimeOffset(reader.GetInt64(0), TimeSpan.Zero),
                    Count = reader.GetInt64(1),
                }
            );
        }

        return result;
    }

    private async Task<IReadOnlyList<TimeSeriesPoint>> GetLogsByDayAsync(
        SqliteConnection connection,
        IReadOnlyList<LogFile> files,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT
                ((e.timestamp_ticks / 864000000000) * 864000000000) AS day_ticks,
                COUNT(*)
            FROM entries e
            {BuildFileWhere(command, files)}
            GROUP BY day_ticks
            ORDER BY day_ticks ASC;
            """;

        var result = new List<TimeSeriesPoint>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(
                new TimeSeriesPoint
                {
                    Timestamp = new DateTimeOffset(reader.GetInt64(0), TimeSpan.Zero),
                    Count = reader.GetInt64(1),
                }
            );
        }

        return result;
    }

    private async Task<IReadOnlyList<SourceDistributionPoint>> GetTopSourcesAsync(
        SqliteConnection connection,
        IReadOnlyList<LogFile> files,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT e.source_context, COUNT(*) AS count
            FROM entries e
            {BuildFileWhere(command, files)}
                AND e.source_context IS NOT NULL
                AND e.source_context <> ''
            GROUP BY e.source_context
            ORDER BY count DESC, e.source_context ASC
            LIMIT 10;
            """;

        var result = new List<SourceDistributionPoint>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(
                new SourceDistributionPoint
                {
                    Source = reader.GetString(0),
                    Count = reader.GetInt64(1),
                }
            );
        }

        return result;
    }

    private static string BuildWhere(
        SqliteCommand command,
        IReadOnlyList<LogFile> files,
        LogQuery query
    )
    {
        var predicates = new List<string> { BuildFilePredicate(command, files) };

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

        if (query.Levels?.Count > 0)
        {
            var levelParameters = new List<string>(query.Levels.Count);
            for (var i = 0; i < query.Levels.Count; i++)
            {
                var name = "$level" + i;
                levelParameters.Add(name);
                command.Parameters.AddWithValue(name, (int)query.Levels[i]);
            }

            predicates.Add($"e.level IN ({string.Join(",", levelParameters)})");
        }

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

        return "WHERE " + string.Join(" AND ", predicates);
    }

    private static string BuildFileWhere(SqliteCommand command, IReadOnlyList<LogFile> files) =>
        "WHERE " + BuildFilePredicate(command, files);

    private static string BuildFilePredicate(SqliteCommand command, IReadOnlyList<LogFile> files)
    {
        var fileParameters = new List<string>(files.Count);
        for (var i = 0; i < files.Count; i++)
        {
            var name = "$file" + i;
            fileParameters.Add(name);
            command.Parameters.AddWithValue(name, files[i].Name);
        }

        return $"e.file_id IN (SELECT id FROM files WHERE name IN ({string.Join(",", fileParameters)}))";
    }

    private static string BuildOrderBy(LogQuery query)
    {
        var direction = query.SortDescending ? "DESC" : "ASC";
        var primary = query.SortBy.ToUpperInvariant() switch
        {
            "LEVEL" => $"e.level {direction}",
            "MESSAGE" => $"e.message COLLATE NOCASE {direction}",
            _ => $"e.timestamp_ticks {direction}",
        };

        return $"ORDER BY {primary}, f.name ASC, e.offset ASC";
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private async Task<FileIndexState?> GetFileStateAsync(
        SqliteConnection connection,
        LogFile file,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                f.id,
                f.full_path,
                f.size_bytes,
                f.last_write_ticks,
                f.last_scanned_offset,
                COALESCE(MAX(e.seq) + 1, 0)
            FROM files f
            LEFT JOIN entries e ON e.file_id = f.id
            WHERE f.name = $name
            GROUP BY f.id;
            """;
        command.Parameters.AddWithValue("$name", file.Name);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new FileIndexState(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt32(5)
        );
    }

    private async Task<FileIndexState> UpsertFileAsync(
        SqliteConnection connection,
        LogFile file,
        long lastScannedOffset,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO files (
                name,
                full_path,
                size_bytes,
                last_write_ticks,
                last_scanned_offset,
                format
            )
            VALUES (
                $name,
                $full_path,
                $size_bytes,
                $last_write_ticks,
                $last_scanned_offset,
                $format
            )
            ON CONFLICT(name) DO UPDATE SET
                full_path = excluded.full_path,
                size_bytes = excluded.size_bytes,
                last_write_ticks = excluded.last_write_ticks,
                last_scanned_offset = excluded.last_scanned_offset,
                format = excluded.format
            RETURNING id, full_path, size_bytes, last_write_ticks, last_scanned_offset;
            """;
        command.Parameters.AddWithValue("$name", file.Name);
        command.Parameters.AddWithValue("$full_path", file.FullPath);
        command.Parameters.AddWithValue("$size_bytes", file.SizeBytes);
        command.Parameters.AddWithValue("$last_write_ticks", file.LastModified.UtcTicks);
        command.Parameters.AddWithValue("$last_scanned_offset", lastScannedOffset);
        command.Parameters.AddWithValue("$format", file.Format.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        return new FileIndexState(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            0
        );
    }

    private static async Task UpdateFileMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long fileId,
        LogFile file,
        long lastScannedOffset,
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
                format = $format
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", fileId);
        command.Parameters.AddWithValue("$full_path", file.FullPath);
        command.Parameters.AddWithValue("$size_bytes", file.SizeBytes);
        command.Parameters.AddWithValue("$last_write_ticks", file.LastModified.UtcTicks);
        command.Parameters.AddWithValue("$last_scanned_offset", lastScannedOffset);
        command.Parameters.AddWithValue("$format", file.Format.ToString());

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteFileIndexAsync(
        SqliteConnection connection,
        string fileName,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM entries
            WHERE file_id IN (SELECT id FROM files WHERE name = $name);
            DELETE FROM files
            WHERE name = $name;
            """;
        command.Parameters.AddWithValue("$name", fileName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

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
        string sql,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
            }.ToString()
        );
        connection.Open();
        return connection;
    }

    private sealed record FileIndexState(
        long Id,
        string FullPath,
        long SizeBytes,
        long LastWriteTicks,
        long LastScannedOffset,
        int NextSeq
    );
}

internal sealed record IndexedQueryResult(IReadOnlyList<IndexedLogPointer> Items, int TotalCount);

internal sealed record IndexedLogPointer(string FileName, long Offset);
