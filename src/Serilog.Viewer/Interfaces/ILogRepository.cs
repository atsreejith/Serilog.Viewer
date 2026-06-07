using Serilog.Viewer.Models;

namespace Serilog.Viewer.Interfaces;

public interface ILogRepository
{
    Task<IReadOnlyList<LogFile>> GetFilesAsync(CancellationToken cancellationToken = default);
    Task<LogFile?> GetFileAsync(string fileName, CancellationToken cancellationToken = default);
    Task<bool> DeleteFileAsync(string fileName, CancellationToken cancellationToken = default);
    Task<PagedResult<LogEntry>> QueryAsync(
        LogQuery query,
        CancellationToken cancellationToken = default
    );
    Task<LogEntry?> GetEntryAsync(
        string fileName,
        long lineOffset,
        CancellationToken cancellationToken = default
    );
    Task<DashboardStats> GetStatsAsync(
        IReadOnlyList<string>? fileNames = null,
        CancellationToken cancellationToken = default
    );
    IAsyncEnumerable<LogEntry> StreamAsync(
        LogQuery query,
        CancellationToken cancellationToken = default
    );
}
