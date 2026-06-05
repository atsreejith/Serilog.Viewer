namespace Serilog.Viewer.Models;

public sealed class LogEntry
{
    public string Id { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; }
    public LogLevel Level { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? RenderedMessage { get; init; }
    public string? Exception { get; init; }
    public string? SourceContext { get; init; }
    public string? CorrelationId { get; init; }
    public string? RequestId { get; init; }
    public string? TraceId { get; init; }
    public string? SpanId { get; init; }
    public IReadOnlyDictionary<string, object?> Properties { get; init; } =
        new Dictionary<string, object?>();
    public string RawJson { get; init; } = string.Empty;
    public long LineOffset { get; init; }
    public string FileName { get; init; } = string.Empty;
}
