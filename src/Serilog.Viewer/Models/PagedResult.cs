namespace Serilog.Viewer.Models;

public sealed class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public QueryPerformanceMetrics? Performance { get; init; }
    public bool HasNextPage => Page * PageSize < TotalCount;
}

public sealed class QueryPerformanceMetrics
{
    public long DurationMs { get; init; }
    public long ServerPeakMemoryBytes { get; init; }
    public string ServerPeakMemoryFormatted { get; init; } = string.Empty;
}
