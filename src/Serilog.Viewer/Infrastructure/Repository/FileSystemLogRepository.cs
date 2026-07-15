using Microsoft.Extensions.Logging;
using Serilog.Viewer.Infrastructure.Indexing;
using Serilog.Viewer.Infrastructure.Reading;
using Serilog.Viewer.Interfaces;
using Serilog.Viewer.Models;

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
    private readonly LogFileIndexStore _indexStore;
    private readonly ILogger<FileSystemLogRepository> _logger;

    public FileSystemLogRepository(
        string logFolder,
        StreamingLogFileReader reader,
        ILogger<FileSystemLogRepository> logger
    )
        : this(logFolder, reader, new LogFileIndexStore(logFolder, reader), logger) { }

    /// <summary>
    /// Overload that also routes index diagnostics (timings, rebuild reasons) through
    /// <paramref name="loggerFactory"/>. Enable Debug for
    /// Serilog.Viewer.Infrastructure.Indexing.LogFileIndexStore to see them.
    /// </summary>
    public FileSystemLogRepository(
        string logFolder,
        StreamingLogFileReader reader,
        ILoggerFactory loggerFactory
    )
        : this(
            logFolder,
            reader,
            new LogFileIndexStore(logFolder, reader, loggerFactory.CreateLogger<LogFileIndexStore>()),
            loggerFactory.CreateLogger<FileSystemLogRepository>()
        ) { }

    internal FileSystemLogRepository(
        string logFolder,
        StreamingLogFileReader reader,
        LogFileIndexStore indexStore,
        ILogger<FileSystemLogRepository> logger
    )
    {
        _logFolder = logFolder;
        _reader = reader;
        _indexStore = indexStore;
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

    public async Task<LogFile?> GetFileAsync(
        string fileName,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(fileName) || !IsSafeRelativePath(fileName))
            return null;

        var files = await GetFilesAsync(cancellationToken);
        return files.FirstOrDefault(f =>
            string.Equals(f.Name, fileName, StringComparison.Ordinal)
        );
    }

    public async Task<bool> DeleteFileAsync(
        string fileName,
        CancellationToken cancellationToken = default
    )
    {
        var file = await GetFileAsync(fileName, cancellationToken);
        if (file is null)
            return false;

        File.Delete(file.FullPath);
        return true;
    }

    public async Task<PagedResult<LogEntry>> QueryAsync(
        LogQuery query,
        CancellationToken cancellationToken = default
    )
    {
        var targetFiles = await GetTargetFilesAsync(query, cancellationToken);
        if (targetFiles.Count == 0)
        {
            return new PagedResult<LogEntry>
            {
                Items = [],
                TotalCount = 0,
                Page = query.Page,
                PageSize = query.PageSize,
            };
        }

        try
        {
            var indexed = await _indexStore.QueryAsync(targetFiles, query, cancellationToken);
            var items = new List<LogEntry>(indexed.Items.Count);

            foreach (var pointer in indexed.Items)
            {
                var entry = await GetEntryAsync(pointer.FileName, pointer.Offset, cancellationToken);
                if (entry is not null)
                    items.Add(entry);
            }

            return new PagedResult<LogEntry>
            {
                Items = items,
                TotalCount = indexed.TotalCount,
                Page = query.Page,
                PageSize = query.PageSize,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falling back to streaming log query because indexing failed");
            return await QueryByStreamingAsync(targetFiles, query, cancellationToken);
        }
    }

    private async Task<PagedResult<LogEntry>> QueryByStreamingAsync(
        IReadOnlyList<LogFile> targetFiles,
        LogQuery query,
        CancellationToken cancellationToken = default
    )
    {
        var skip = Math.Max(0, query.Page - 1) * query.PageSize;
        var retainCount = skip + query.PageSize;
        var retained = new List<LogEntry>(Math.Min(retainCount, 4096));
        var comparer = CreateQueryComparer(query);
        var total = 0;

        await foreach (var entry in StreamFilesAsync(targetFiles, query, cancellationToken))
        {
            if (total < int.MaxValue)
                total++;

            retained.Add(entry);
            if (retained.Count >= retainCount * 2)
                TrimRetainedEntries(retained, comparer, retainCount);
        }

        TrimRetainedEntries(retained, comparer, retainCount);

        var paged = retained.Skip(skip).Take(query.PageSize).ToList();

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

        return await _indexStore.GetStatsAsync(targetFiles, cancellationToken);
    }

    public async IAsyncEnumerable<LogEntry> StreamAsync(
        LogQuery query,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default
    )
    {
        var targetFiles = await GetTargetFilesAsync(query, cancellationToken);

        await foreach (var entry in StreamFilesAsync(targetFiles, query, cancellationToken))
            yield return entry;
    }

    private async Task<IReadOnlyList<LogFile>> GetTargetFilesAsync(
        LogQuery query,
        CancellationToken cancellationToken
    )
    {
        var files = await GetFilesAsync(cancellationToken);

        if (query.FileNames?.Count > 0)
            return files.Where(f => query.FileNames.Contains(f.Name)).ToList();

        return !string.IsNullOrEmpty(query.FileName)
            ? files.Where(f => f.Name == query.FileName).ToList()
            : files.ToList();
    }

    private async IAsyncEnumerable<LogEntry> StreamFilesAsync(
        IEnumerable<LogFile> targetFiles,
        LogQuery query,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default
    )
    {
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

    private static IComparer<LogEntry> CreateQueryComparer(LogQuery query) =>
        Comparer<LogEntry>.Create(
            (left, right) =>
            {
                var result = query.SortBy.ToUpperInvariant() switch
                {
                    "LEVEL" => left.Level.CompareTo(right.Level),
                    "MESSAGE" => string.Compare(
                        left.Message,
                        right.Message,
                        StringComparison.OrdinalIgnoreCase
                    ),
                    _ => left.Timestamp.CompareTo(right.Timestamp),
                };

                if (query.SortDescending)
                    result = -result;

                if (result != 0)
                    return result;

                result = string.Compare(left.FileName, right.FileName, StringComparison.Ordinal);
                if (result != 0)
                    return result;

                return left.LineOffset.CompareTo(right.LineOffset);
            }
        );

    private static void TrimRetainedEntries(
        List<LogEntry> entries,
        IComparer<LogEntry> comparer,
        int retainCount
    )
    {
        if (entries.Count <= retainCount)
        {
            entries.Sort(comparer);
            return;
        }

        entries.Sort(comparer);
        entries.RemoveRange(retainCount, entries.Count - retainCount);
    }

    private string? ResolveFilePath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !IsSafeRelativePath(fileName))
            return null;

        var root = Path.GetFullPath(_logFolder);
        var path = Path.GetFullPath(
            Path.Combine(root, fileName.Replace('/', Path.DirectorySeparatorChar))
        );

        if (!IsWithinRoot(root, path))
            return null;

        return File.Exists(path) ? path : null;
    }

    private static bool IsSafeRelativePath(string fileName)
    {
        if (Path.IsPathFullyQualified(fileName))
            return false;

        var parts = fileName.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && !parts.Any(p => p is "." or "..");
    }

    private static bool IsWithinRoot(string root, string path)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        return string.Equals(path, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase
            );
    }
}
