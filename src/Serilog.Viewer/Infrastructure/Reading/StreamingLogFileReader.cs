using Serilog.Viewer.Infrastructure.Parsing;
using Serilog.Viewer.Interfaces;
using Serilog.Viewer.Models;

namespace Serilog.Viewer.Infrastructure.Reading;

/// <summary>
/// Reads log files efficiently using streaming — never loads the entire file into memory.
/// </summary>
public sealed class StreamingLogFileReader
{
    private readonly LogParserFactory _parserFactory;

    public StreamingLogFileReader(LogParserFactory parserFactory)
    {
        _parserFactory = parserFactory;
    }

    public async IAsyncEnumerable<LogEntry> ReadAsync(
        string filePath,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default
    )
    {
        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 65536,
            useAsync: true
        );

        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);

        var fileName = Path.GetFileName(filePath);
        ILogParser? parser = null;
        long lineOffset = 0;

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            var currentOffset = lineOffset;
            lineOffset = stream.Position;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            parser ??= _parserFactory.DetectParser(line);

            var entry = parser.Parse(line, fileName, currentOffset);
            if (entry is not null)
                yield return entry;
        }
    }

    public async IAsyncEnumerable<LogEntry> ReadFromOffsetAsync(
        string filePath,
        long startOffset,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default
    )
    {
        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            useAsync: true
        );

        stream.Seek(startOffset, SeekOrigin.Begin);

        using var reader = new StreamReader(
            stream,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true
        );

        var fileName = Path.GetFileName(filePath);
        ILogParser? parser = null;

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var offset = stream.Position;

            parser ??= _parserFactory.DetectParser(line);

            var entry = parser.Parse(line, fileName, offset);
            if (entry is not null)
                yield return entry;
        }
    }

    public static long GetFileSize(string filePath)
    {
        var fi = new FileInfo(filePath);
        return fi.Exists ? fi.Length : 0;
    }
}
