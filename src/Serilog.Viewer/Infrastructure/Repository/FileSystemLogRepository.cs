using Microsoft.Extensions.Logging;
using Serilog.Viewer.Infrastructure.Reading;
using Serilog.Viewer.Interfaces;
using Serilog.Viewer.Models;
using CoreLogLevel = Serilog.Viewer.Models.LogLevel;

namespace Serilog.Viewer.Infrastructure.Repository;

/// <summary>
/// ILogRepository implementation that reads from the local file system.
/// All queries stream through files without loading them fully into memory.
/// </summary>
public sealed class FileSystemLogRepository : ILogRepository
{
    private static readonly string[] _patterns = ["*.txt", "*.log", "*.json", "*.clef"];

    private readonly string _logFolder;
    private readonly StreamingLogFileReader _reader;
    private readonly ILogger<FileSystemLogRepository> _logger;

    public FileSystemLogRepository(
        string logFolder,
        StreamingLogFileReader reader,
        ILogger<FileSystemLogRepository> logger
    )
    {
        _logFolder = logFolder;
        _reader = reader;
        _logger = logger;
    }

    public Task<IReadOnlyList<LogFile>> GetFilesAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_logFolder))
            return Task.FromResult<IReadOnlyList<LogFile>>([]);

        var files = new List<LogFile>();
        foreach (var pattern in _patterns)
        {
            foreach (
                var path in Directory.EnumerateFiles(
                    _logFolder,
                    pattern,
                    SearchOption.AllDirectories
                )
            )
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fi = new FileInfo(path);
                files.Add(
                    new LogFile
                    {
                        Name = Path.GetRelativePath(_logFolder, path).Replace('\\', '/'),
                        FullPath = path,
                        SizeBytes = fi.Length,
                        LastModified = fi.LastWriteTimeUtc,
                        Format =
                            path.EndsWith(".json") || path.EndsWith(".clef")
                                ? LogFileFormat.Clef
                                : LogFileFormat.PlainText,
                        IsActive = (DateTime.UtcNow - fi.LastWriteTimeUtc).TotalMinutes < 5,
                    }
                );
            }
        }

        var unique = files.DistinctBy(f => f.FullPath).ToList();
        return Task.FromResult<IReadOnlyList<LogFile>>(unique);
    }

    public async Task<PagedResult<LogEntry>> QueryAsync(
        LogQuery query,
        CancellationToken cancellationToken = default
    )
    {
        var allEntries = new List<LogEntry>();
        await foreach (var entry in StreamAsync(query, cancellationToken))
            allEntries.Add(entry);

        IEnumerable<LogEntry> sorted = query.SortBy.ToUpperInvariant() switch
        {
            "LEVEL" => query.SortDescending
                ? allEntries.OrderByDescending(e => e.Level)
                : allEntries.OrderBy(e => e.Level),
            "MESSAGE" => query.SortDescending
                ? allEntries.OrderByDescending(e => e.Message)
                : allEntries.OrderBy(e => e.Message),
            _ => query.SortDescending
                ? allEntries.OrderByDescending(e => e.Timestamp)
                : allEntries.OrderBy(e => e.Timestamp),
        };

        var sortedList = sorted.ToList();
        var total = sortedList.Count;
        var paged = sortedList
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToList();

        return new PagedResult<LogEntry>
        {
            Items = paged,
            TotalCount = total,
            Page = query.Page,
            PageSize = query.PageSize,
        };
    }

    public async Task<LogEntry?> GetEntryAsync(
        string fileName,
        long lineOffset,
        CancellationToken cancellationToken = default
    )
    {
        var filePath = ResolveFilePath(fileName);
        if (filePath is null)
            return null;

        await foreach (
            var entry in _reader.ReadFromOffsetAsync(filePath, lineOffset, cancellationToken)
        )
            return entry;

        return null;
    }

    public async Task<DashboardStats> GetStatsAsync(
        IReadOnlyList<string>? fileNames = null,
        CancellationToken cancellationToken = default
    )
    {
        var files = await GetFilesAsync(cancellationToken);
        var targetFiles =
            fileNames?.Count > 0
                ? files.Where(f => fileNames.Contains(f.Name)).ToList()
                : files.ToList();

        long total = 0,
            errors = 0,
            warnings = 0,
            fatals = 0,
            verboses = 0,
            debugs = 0,
            infos = 0;
        var errorsByHour = new Dictionary<DateTimeOffset, long>();
        var logsByDay = new Dictionary<DateTimeOffset, long>();
        var bySrc = new Dictionary<string, long>();

        foreach (var file in targetFiles)
        {
            await foreach (var entry in _reader.ReadAsync(file.FullPath, cancellationToken))
            {
                total++;
                switch (entry.Level)
                {
                    case CoreLogLevel.Error:
                        errors++;
                        break;
                    case CoreLogLevel.Warning:
                        warnings++;
                        break;
                    case CoreLogLevel.Fatal:
                        fatals++;
                        break;
                    case CoreLogLevel.Verbose:
                        verboses++;
                        break;
                    case CoreLogLevel.Debug:
                        debugs++;
                        break;
                    case CoreLogLevel.Information:
                        infos++;
                        break;
                }

                var hour = new DateTimeOffset(
                    entry.Timestamp.Year,
                    entry.Timestamp.Month,
                    entry.Timestamp.Day,
                    entry.Timestamp.Hour,
                    0,
                    0,
                    entry.Timestamp.Offset
                );
                if (entry.Level >= CoreLogLevel.Error)
                    errorsByHour[hour] = errorsByHour.GetValueOrDefault(hour) + 1;

                var day = new DateTimeOffset(
                    entry.Timestamp.Year,
                    entry.Timestamp.Month,
                    entry.Timestamp.Day,
                    0,
                    0,
                    0,
                    entry.Timestamp.Offset
                );
                logsByDay[day] = logsByDay.GetValueOrDefault(day) + 1;

                if (!string.IsNullOrEmpty(entry.SourceContext))
                    bySrc[entry.SourceContext] = bySrc.GetValueOrDefault(entry.SourceContext) + 1;
            }
        }

        return new DashboardStats
        {
            TotalLogs = total,
            Errors = errors,
            Warnings = warnings,
            Fatals = fatals,
            Verboses = verboses,
            Debugs = debugs,
            Informations = infos,
            ActiveFiles = targetFiles.Count(f => f.IsActive),
            TotalFileSizeBytes = targetFiles.Sum(f => f.SizeBytes),
            ErrorsByHour =
            [
                .. errorsByHour
                    .OrderBy(kv => kv.Key)
                    .Select(kv => new TimeSeriesPoint { Timestamp = kv.Key, Count = kv.Value }),
            ],
            LogsByDay =
            [
                .. logsByDay
                    .OrderBy(kv => kv.Key)
                    .Select(kv => new TimeSeriesPoint { Timestamp = kv.Key, Count = kv.Value }),
            ],
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
            TopSources =
            [
                .. bySrc
                    .OrderByDescending(kv => kv.Value)
                    .Take(10)
                    .Select(kv => new SourceDistributionPoint
                    {
                        Source = kv.Key,
                        Count = kv.Value,
                    }),
            ],
        };
    }

    public async IAsyncEnumerable<LogEntry> StreamAsync(
        LogQuery query,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default
    )
    {
        var files = await GetFilesAsync(cancellationToken);

        IEnumerable<LogFile> targetFiles;
        if (query.FileNames?.Count > 0)
            targetFiles = files.Where(f => query.FileNames.Contains(f.Name));
        else if (!string.IsNullOrEmpty(query.FileName))
            targetFiles = files.Where(f => f.Name == query.FileName);
        else
            targetFiles = files;

        foreach (var file in targetFiles)
        {
            await foreach (var entry in _reader.ReadAsync(file.FullPath, cancellationToken))
            {
                if (MatchesQuery(entry, query))
                    yield return entry;
            }
        }
    }

    private static bool MatchesQuery(LogEntry entry, LogQuery query)
    {
        if (query.From.HasValue && entry.Timestamp < query.From.Value)
            return false;
        if (query.To.HasValue && entry.Timestamp > query.To.Value)
            return false;
        if (query.Levels?.Count > 0 && !query.Levels.Contains(entry.Level))
            return false;

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var s = query.SearchText;
            if (
                !(
                    entry.Message.Contains(s, StringComparison.OrdinalIgnoreCase)
                    || (entry.Exception?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (
                        entry.SourceContext?.Contains(s, StringComparison.OrdinalIgnoreCase)
                        ?? false
                    )
                )
            )
                return false;
        }

        if (
            !string.IsNullOrWhiteSpace(query.SourceContext)
            && !string.Equals(
                entry.SourceContext,
                query.SourceContext,
                StringComparison.OrdinalIgnoreCase
            )
        )
            return false;

        if (
            !string.IsNullOrWhiteSpace(query.CorrelationId)
            && !string.Equals(
                entry.CorrelationId,
                query.CorrelationId,
                StringComparison.OrdinalIgnoreCase
            )
        )
            return false;

        if (
            !string.IsNullOrWhiteSpace(query.RequestId)
            && !string.Equals(entry.RequestId, query.RequestId, StringComparison.OrdinalIgnoreCase)
        )
            return false;

        return true;
    }

    private string? ResolveFilePath(string fileName)
    {
        var path = Path.Combine(_logFolder, fileName);
        return File.Exists(path) ? path : null;
    }
}
