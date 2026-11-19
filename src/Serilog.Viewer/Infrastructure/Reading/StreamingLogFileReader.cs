using System.Runtime.CompilerServices;
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
    /// <summary>
    /// Upper bound on the continuation text (exception and stack trace) attached to one entry, so
    /// a file that stops looking like a log part-way through cannot grow one entry without limit.
    /// </summary>
    private const int MaxContinuationChars = 1024 * 1024;

    private readonly LogParserFactory _parserFactory;

    public StreamingLogFileReader(LogParserFactory parserFactory)
    {
        _parserFactory = parserFactory;
    }

    public IAsyncEnumerable<LogEntry> ReadAsync(
        string filePath,
        CancellationToken cancellationToken = default
    ) => ReadFromOffsetAsync(filePath, 0, cancellationToken);

    /// <summary>
    /// Reads every entry from <paramref name="startOffset"/> to the end of the file, including a
    /// final line that has no line break yet.
    /// </summary>
    public IAsyncEnumerable<LogEntry> ReadFromOffsetAsync(
        string filePath,
        long startOffset,
        CancellationToken cancellationToken = default
    ) =>
        ReadCoreAsync(
            filePath,
            startOffset,
            long.MaxValue,
            includeUnterminatedTail: true,
            cursor: null,
            cancellationToken
        );

    /// <summary>
    /// Reads the entries made of complete lines in [<paramref name="startOffset"/>,
    /// <paramref name="endOffset"/>). A final line without a line break is left alone — on a file
    /// that is still being written it may be half an entry — and <paramref name="cursor"/> ends
    /// up at the first byte not consumed, which is where the next read should start.
    /// </summary>
    internal IAsyncEnumerable<LogEntry> ReadRangeAsync(
        string filePath,
        long startOffset,
        long endOffset,
        ReadCursor cursor,
        CancellationToken cancellationToken = default
    )
    {
        cursor.Position = startOffset;
        return ReadCoreAsync(
            filePath,
            startOffset,
            endOffset,
            includeUnterminatedTail: false,
            cursor,
            cancellationToken
        );
    }

    private async IAsyncEnumerable<LogEntry> ReadCoreAsync(
        string filePath,
        long startOffset,
        long endOffset,
        bool includeUnterminatedTail,
        ReadCursor? cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var assembler = new EntryAssembler(_parserFactory, Path.GetFileName(filePath));
        await using var lines = LogLineReader.Open(filePath, startOffset, endOffset);

        while (true)
        {
            while (lines.TryReadLine(out var start, out var length, out var offset))
            {
                var entry = assembler.Feed(lines.Buffer, start, length, offset);
                if (cursor is not null)
                    cursor.Position = lines.Position;
                if (entry is not null)
                    yield return entry;
            }

            if (!await lines.FillAsync(cancellationToken))
                break;
        }

        if (
            includeUnterminatedTail
            && lines.TryReadRemainder(out var tailStart, out var tailLength, out var tailOffset)
        )
        {
            var entry = assembler.Feed(lines.Buffer, tailStart, tailLength, tailOffset);
            if (cursor is not null)
                cursor.Position = lines.Position;
            if (entry is not null)
                yield return entry;
        }

        var last = assembler.Flush();
        if (last is not null)
            yield return last;
    }

    /// <summary>
    /// Parses the entries that start at each of <paramref name="offsets"/>, through a single open
    /// stream, visiting offsets in ascending order. Returns one result per distinct offset; the
    /// value is null when no parseable entry starts there (file truncated or rewritten).
    /// <para>
    /// Offsets that are close together are served from the same buffered block, so reading a page
    /// or an export of neighbouring entries costs a handful of disk reads rather than one per
    /// entry.
    /// </para>
    /// </summary>
    public async Task<Dictionary<long, LogEntry?>> ReadEntriesAtOffsetsAsync(
        string filePath,
        IReadOnlyCollection<long> offsets,
        CancellationToken cancellationToken = default
    )
    {
        var result = new Dictionary<long, LogEntry?>(offsets.Count);
        if (offsets.Count == 0)
            return result;

        var sorted = offsets.Distinct().ToArray();
        Array.Sort(sorted);

        var fileName = Path.GetFileName(filePath);
        ILogParser? parser = null;
        IMultiLineLogParser? multiLine = null;
        StringBuilder? continuation = null;

        await using var lines = LogLineReader.Open(filePath, sequential: false);
        var fileLength = lines.FileLength;

        foreach (var offset in sorted)
        {
            result[offset] = null;
            if (offset < 0 || offset >= fileLength)
                continue;

            lines.Seek(offset);
            var line = await NextLineAsync(lines, cancellationToken);
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (parser is null)
            {
                parser = _parserFactory.DetectParser(line);
                multiLine = parser as IMultiLineLogParser;
            }

            if (multiLine is null)
            {
                result[offset] = parser.Parse(line, fileName, offset);
                continue;
            }

            if (!multiLine.IsEntryStart(line))
                continue;

            // The entry runs until the next line that starts one. That line is read here and then
            // simply left behind: the next offset is repositioned from the buffer at no cost.
            continuation?.Clear();
            while (true)
            {
                var next = await NextLineAsync(lines, cancellationToken);
                if (next is null || multiLine.IsEntryStart(next))
                    break;
                if (string.IsNullOrWhiteSpace(next))
                    continue;

                continuation ??= new StringBuilder();
                if (continuation.Length > 0)
                    continuation.Append('\n');
                continuation.Append(next);
                if (continuation.Length >= MaxContinuationChars)
                    break;
            }

            result[offset] = multiLine.Parse(
                line,
                continuation is { Length: > 0 } ? continuation.ToString() : null,
                fileName,
                offset
            );
        }

        return result;
    }

    /// <summary>The entry that starts exactly at <paramref name="offset"/>, or null.</summary>
    internal async Task<LogEntry?> ReadEntryAtAsync(
        string filePath,
        long offset,
        CancellationToken cancellationToken = default
    )
    {
        var entries = await ReadEntriesAtOffsetsAsync(filePath, [offset], cancellationToken);
        return entries.GetValueOrDefault(offset);
    }

    /// <summary>
    /// Chooses the parser for a file from its first non-blank line. Reads that start part-way
    /// through a file (index slices, live tail) can land on a stack-trace line, which says nothing
    /// about the file's format; the start of the file always does.
    /// </summary>
    internal async Task<ILogParser> DetectParserAsync(
        string filePath,
        CancellationToken cancellationToken = default
    )
    {
        await using var lines = LogLineReader.Open(filePath);
        while (true)
        {
            var line = await NextLineAsync(lines, cancellationToken);
            if (line is null)
                return _parserFactory.DetectParser(string.Empty);
            if (!string.IsNullOrWhiteSpace(line))
                return _parserFactory.DetectParser(line);
        }
    }

    private static async ValueTask<string?> NextLineAsync(
        LogLineReader lines,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            if (lines.TryReadLine(out var start, out var length, out _))
                return Encoding.UTF8.GetString(lines.Buffer, start, length);

            if (!await lines.FillAsync(cancellationToken))
            {
                return lines.TryReadRemainder(out start, out length, out _)
                    ? Encoding.UTF8.GetString(lines.Buffer, start, length)
                    : null;
            }
        }
    }

    /// <summary>
    /// Turns a stream of lines into entries. For a multi-line format an entry is only complete
    /// once the next one starts (or the input ends), so each call returns the entry that the
    /// given line finished, if any.
    /// </summary>
    private sealed class EntryAssembler
    {
        private readonly LogParserFactory _factory;
        private readonly string _fileName;
        private ILogParser? _parser;
        private IMultiLineLogParser? _multiLine;
        private string? _pendingLine;
        private long _pendingOffset;
        private StringBuilder? _continuation;

        public EntryAssembler(LogParserFactory factory, string fileName)
        {
            _factory = factory;
            _fileName = fileName;
        }

        public LogEntry? Feed(byte[] buffer, int start, int length, long offset)
        {
            if (LogLineReader.IsBlank(buffer.AsSpan(start, length)))
                return null;

            var line = Encoding.UTF8.GetString(buffer, start, length);
            if (_parser is null)
            {
                _parser = _factory.DetectParser(line);
                _multiLine = _parser as IMultiLineLogParser;
            }

            if (_multiLine is null)
                return _parser.Parse(line, _fileName, offset);

            if (_multiLine.IsEntryStart(line))
            {
                var completed = Flush();
                _pendingLine = line;
                _pendingOffset = offset;
                return completed;
            }

            // A line that starts no entry belongs to the one before it. With no entry before it
            // (the read began mid-entry) there is nothing to attach it to.
            if (_pendingLine is not null)
            {
                _continuation ??= new StringBuilder();
                if (_continuation.Length < MaxContinuationChars)
                {
                    if (_continuation.Length > 0)
                        _continuation.Append('\n');
                    _continuation.Append(line);
                }
            }

            return null;
        }

        public LogEntry? Flush()
        {
            if (_pendingLine is null || _multiLine is null)
                return null;

            var entry = _multiLine.Parse(
                _pendingLine,
                _continuation is { Length: > 0 } ? _continuation.ToString() : null,
                _fileName,
                _pendingOffset
            );

            _pendingLine = null;
            _continuation?.Clear();
            return entry;
        }
    }

    /// <summary>
    /// Returns the offset of the first line boundary at or after <paramref name="targetOffset"/>,
    /// or the file length if no line break occurs after the target.
    /// </summary>
    public static async Task<long> FindNextLineStartAsync(
        string filePath,
        long targetOffset,
        CancellationToken cancellationToken = default
    )
    {
        if (targetOffset <= 0)
            return 0;

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 65536,
            useAsync: true
        );

        if (targetOffset >= stream.Length)
            return stream.Length;

        stream.Seek(targetOffset, SeekOrigin.Begin);

        var buffer = new byte[65536];
        var position = targetOffset;

        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            var newline = Array.IndexOf(buffer, (byte)'\n', 0, bytesRead);
            if (newline >= 0)
                return position + newline + 1;
            position += bytesRead;
        }

        return stream.Length;
    }

    public static long GetFileSize(string filePath)
    {
        var fi = new FileInfo(filePath);
        return fi.Exists ? fi.Length : 0;
    }
}

/// <summary>Where a ranged read stopped: the offset of the first byte it did not consume.</summary>
internal sealed class ReadCursor
{
    public long Position { get; set; }
}
