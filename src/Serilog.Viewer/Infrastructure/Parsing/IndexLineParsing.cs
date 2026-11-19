using Serilog.Viewer.Models;

namespace Serilog.Viewer.Infrastructure.Parsing;

/// <summary>
/// What the search index keeps of a log entry. The index only has to locate an entry and render
/// a grid row; the full entry is re-read from the log file through its offset.
/// </summary>
internal struct IndexLineFields
{
    public long TimestampUtcTicks;
    public LogLevel Level;

    /// <summary>At most <see cref="IndexLimits.MaxMessageChars"/> characters.</summary>
    public string Message;

    /// <summary>At most <see cref="IndexLimits.MaxExceptionChars"/> characters.</summary>
    public string? Exception;
    public string? SourceContext;
    public string? CorrelationId;
    public string? RequestId;
}

internal static class IndexLimits
{
    /// <summary>
    /// Characters of a message retained in the index. Search matches against this prefix only.
    /// </summary>
    public const int MaxMessageChars = 512;

    /// <summary>
    /// Characters of an exception retained in the index. Enough for the exception type, its
    /// message and a typical stack trace, while keeping one runaway trace from bloating the index.
    /// </summary>
    public const int MaxExceptionChars = 8192;

    public static string Truncate(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars];
}

/// <summary>
/// A parser that can produce index fields straight from the UTF-8 bytes of a line, without first
/// building a string, a property dictionary or a <see cref="LogEntry"/>. Indexing a file parses
/// every line of it, and almost everything a full parse produces is thrown away there.
/// <para>
/// The result must agree with what <see cref="Interfaces.ILogParser.Parse"/> reports for the same
/// line, because the grid shows index fields and the details view shows the full parse.
/// </para>
/// </summary>
internal interface IIndexLineParser
{
    /// <summary>
    /// True when lines that do not start an entry belong to the entry before them (plain-text
    /// stack traces). False when every entry is exactly one line.
    /// </summary>
    bool AcceptsContinuationLines { get; }

    /// <summary>
    /// Parses <paramref name="utf8Line"/> as the first line of an entry. Returns false when the
    /// line does not start an entry.
    /// </summary>
    bool TryParseIndexLine(ReadOnlySpan<byte> utf8Line, ref IndexLineFields fields);
}

/// <summary>
/// A parser whose entries can span several lines: a first line that starts the entry, followed by
/// any number of continuation lines.
/// </summary>
internal interface IMultiLineLogParser
{
    bool IsEntryStart(string line);

    /// <summary>
    /// Parses an entry from its first line and the continuation lines that followed it, joined
    /// with '\n' (null when there were none).
    /// </summary>
    LogEntry? Parse(string line, string? continuation, string fileName, long lineOffset);
}
