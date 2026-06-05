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
}
