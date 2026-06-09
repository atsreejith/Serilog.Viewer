using System.Text;
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
        var fileName = Path.GetFileName(filePath);
        ILogParser? parser = null;

        await foreach (var (line, offset) in ReadLinesAsync(filePath, 0, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            parser ??= _parserFactory.DetectParser(line);

            var entry = parser.Parse(line, fileName, offset);
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
        var fileName = Path.GetFileName(filePath);
        ILogParser? parser = null;

        await foreach (var (line, offset) in ReadLinesAsync(filePath, startOffset, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            parser ??= _parserFactory.DetectParser(line);

            var entry = parser.Parse(line, fileName, offset);
            if (entry is not null)
                yield return entry;
        }
    }

    private static async IAsyncEnumerable<(string Line, long Offset)> ReadLinesAsync(
        string filePath,
        long startOffset,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default
    )
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 65536,
            useAsync: true
        );

        stream.Seek(startOffset, SeekOrigin.Begin);

        var buffer = new byte[65536];
        var lineBytes = new MemoryStream();
        var lineStart = startOffset;
        var position = startOffset;

        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            for (var i = 0; i < bytesRead; i++)
            {
                var value = buffer[i];
                if (value == (byte)'\n')
                {
                    yield return (DecodeLine(lineBytes, trimCarriageReturn: true), lineStart);
                    lineBytes.SetLength(0);
                    lineStart = position + 1;
                }
                else
                {
                    lineBytes.WriteByte(value);
                }

                position++;
            }
        }

        if (lineBytes.Length > 0)
            yield return (DecodeLine(lineBytes, trimCarriageReturn: false), lineStart);
    }

    private static string DecodeLine(MemoryStream lineBytes, bool trimCarriageReturn)
    {
        var bytes = lineBytes.ToArray();
        var length = bytes.Length;

        if (trimCarriageReturn && length > 0 && bytes[length - 1] == (byte)'\r')
            length--;

        var offset = 0;
        if (length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            offset = 3;
            length -= 3;
        }

        return Encoding.UTF8.GetString(bytes, offset, length);
    }

    public static long GetFileSize(string filePath)
    {
        var fi = new FileInfo(filePath);
        return fi.Exists ? fi.Length : 0;
    }
}
