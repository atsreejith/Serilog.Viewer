namespace Serilog.Viewer.Models;

public sealed class LogQuery
{
    public string? FileName { get; init; }
    public IReadOnlyList<string>? FileNames { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public IReadOnlyList<LogLevel>? Levels { get; init; }
    public string? SearchText { get; init; }
    public string? SourceContext { get; init; }
    public string? CorrelationId { get; init; }
    public string? RequestId { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 100;
    public string SortBy { get; init; } = "Timestamp";
    public bool SortDescending { get; init; } = true;

    /// <summary>
    /// When true (default), each returned entry is re-read from its log file so it carries the
    /// full message, exception, structured properties and raw line. When false, entries are built
    /// from the search index alone — timestamp, level, source, correlation/request ids and the
    /// first 512 characters of the message — which avoids one file open per row. The grid uses
    /// false and fetches full details on demand via <c>GetEntryAsync</c>.
    /// </summary>
    public bool IncludeFullEntries { get; init; } = true;
}
