using System.Text;
using Serilog.Viewer.Infrastructure.Parsing;
using Serilog.Viewer.Infrastructure.Reading;
using Serilog.Viewer.Interfaces;
using Serilog.Viewer.Models;

namespace Serilog.Viewer.Core.Tests;

/// <summary>
/// The line reader and the entry reader built on it: line splitting without copies, entries that
/// span several lines, and reads that stop cleanly in front of a line still being written.
/// </summary>
public class LogReadingTests : IDisposable
{
    private readonly string _tempDir;
    private readonly StreamingLogFileReader _reader;

    public LogReadingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "slv-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _reader = new StreamingLogFileReader(
            new LogParserFactory(new ILogParser[] { new ClefLogParser(), new PlainTextLogParser() })
        );
    }

    private string Write(string name, string content, bool bom = false)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content, new UTF8Encoding(bom));
        return path;
    }

    private static async Task<List<(string Text, long Offset)>> ReadLinesAsync(
        string path,
        long start = 0,
        long limit = long.MaxValue,
        bool includeRemainder = true
    )
    {
        var result = new List<(string, long)>();
        await using var lines = LogLineReader.Open(path, start, limit);
        while (true)
        {
            while (lines.TryReadLine(out var s, out var length, out var offset))
                result.Add((Encoding.UTF8.GetString(lines.Buffer, s, length), offset));
            if (!await lines.FillAsync(CancellationToken.None))
                break;
        }

        if (includeRemainder && lines.TryReadRemainder(out var rs, out var rl, out var ro))
            result.Add((Encoding.UTF8.GetString(lines.Buffer, rs, rl), ro));
        return result;
    }

    [Fact]
    public async Task LineReader_SplitsLfAndCrLf_AndReportsByteOffsets()
    {
        var path = Write("lines.txt", "first\r\nsecond\nthïrd\r\n\r\nlast-without-break");

        var lines = await ReadLinesAsync(path);

        Assert.Equal(
            new[] { "first", "second", "thïrd", "", "last-without-break" },
            lines.Select(l => l.Text)
        );
        // "thïrd" holds a two-byte character, so offsets are bytes, not characters.
        Assert.Equal(new long[] { 0, 7, 14, 22, 24 }, lines.Select(l => l.Offset));
    }

    [Fact]
    public async Task LineReader_StripsByteOrderMarkFromTheFirstLineOnly()
    {
        var path = Write("bom.txt", "first\nsecond\n", bom: true);

        var lines = await ReadLinesAsync(path);

        Assert.Equal(new[] { "first", "second" }, lines.Select(l => l.Text));
        Assert.Equal(new long[] { 0, 9 }, lines.Select(l => l.Offset));
    }

    [Fact]
    public async Task LineReader_HandlesLinesLongerThanItsBuffer()
    {
        var huge = new string('x', 700_000);
        var path = Write("huge.txt", $"short\n{huge}\nafter\n");

        var lines = await ReadLinesAsync(path);

        Assert.Equal(3, lines.Count);
        Assert.Equal(huge.Length, lines[1].Text.Length);
        Assert.Equal("after", lines[2].Text);
        Assert.Equal(6 + huge.Length + 1, lines[2].Offset);
    }

    [Fact]
    public async Task LineReader_NeverReadsPastItsLimit()
    {
        var path = Write("limit.txt", "one\ntwo\nthree\n");

        // The limit falls inside "two": the complete line before it is returned, and what is
        // left is a remainder, not a line.
        var complete = await ReadLinesAsync(path, limit: 6, includeRemainder: false);
        var withRemainder = await ReadLinesAsync(path, limit: 6);

        Assert.Equal(new[] { "one" }, complete.Select(l => l.Text));
        Assert.Equal(new[] { "one", "tw" }, withRemainder.Select(l => l.Text));
    }

    [Fact]
    public async Task LineReader_SeekReusesBufferedData()
    {
        var path = Write("seek.txt", "aaa\nbbb\nccc\nddd\n");
        await using var lines = LogLineReader.Open(path);
        await lines.FillAsync(CancellationToken.None);

        lines.Seek(8);
        Assert.True(lines.TryReadLine(out var s, out var length, out var offset));
        Assert.Equal("ccc", Encoding.UTF8.GetString(lines.Buffer, s, length));
        Assert.Equal(8, offset);

        // Backwards, still inside the buffer.
        lines.Seek(4);
        Assert.True(lines.TryReadLine(out s, out length, out offset));
        Assert.Equal("bbb", Encoding.UTF8.GetString(lines.Buffer, s, length));
        Assert.Equal(4, offset);
    }

    private const string PlainLog =
        "2026-01-01 00:00:00.000 +00:00 [INF] Starting up\n"
        + "2026-01-01 00:00:01.000 +00:00 [ERR] Order 12 failed\n"
        + "System.InvalidOperationException: Order 12 is in an invalid state\n"
        + "   at Shop.Orders.OrderService.Ship(Int32 id) in OrderService.cs:line 142\n"
        + "   at Shop.Api.OrdersController.Ship(Int32 id) in OrdersController.cs:line 58\n"
        + "2026-01-01 00:00:02.000 +00:00 [INF] Still running\n";

    [Fact]
    public async Task ReadAsync_PlainText_AttachesStackTraceLinesToTheirEntry()
    {
        var path = Write("app.log", PlainLog);

        var entries = new List<LogEntry>();
        await foreach (var entry in _reader.ReadAsync(path))
            entries.Add(entry);

        Assert.Equal(3, entries.Count);
        Assert.Null(entries[0].Exception);
        Assert.Equal("Order 12 failed", entries[1].Message);
        Assert.Equal(LogLevel.Error, entries[1].Level);
        Assert.StartsWith("System.InvalidOperationException", entries[1].Exception);
        Assert.Contains("OrdersController.cs:line 58", entries[1].Exception);
        Assert.Equal(3, entries[1].Exception!.Split('\n').Length);
        Assert.Null(entries[2].Exception);
        Assert.Equal("Still running", entries[2].Message);
    }

    [Fact]
    public async Task ReadEntriesAtOffsets_PlainText_ReturnsWholeMultiLineEntries()
    {
        var path = Write("app.log", PlainLog);
        var second = PlainLog.IndexOf("2026-01-01 00:00:01", StringComparison.Ordinal);
        var third = PlainLog.IndexOf("2026-01-01 00:00:02", StringComparison.Ordinal);
        var insideTrace = PlainLog.IndexOf("   at Shop.Orders", StringComparison.Ordinal);

        var entries = await _reader.ReadEntriesAtOffsetsAsync(
            path,
            new long[] { third, 0, second, insideTrace, 99_999 }
        );

        Assert.Equal("Starting up", entries[0]!.Message);
        Assert.Null(entries[0]!.Exception);
        Assert.Equal("Order 12 failed", entries[second]!.Message);
        Assert.Contains("OrdersController.cs:line 58", entries[second]!.Exception);
        Assert.Equal("Still running", entries[third]!.Message);

        // Not the start of an entry, and past the end of the file.
        Assert.Null(entries[insideTrace]);
        Assert.Null(entries[99_999]);
    }

    [Fact]
    public async Task ReadEntriesAtOffsets_Clef_ReadsManyNeighbouringEntries()
    {
        var builder = new StringBuilder();
        var offsets = new List<long>();
        for (var i = 0; i < 5000; i++)
        {
            offsets.Add(builder.Length);
            builder
                .Append("{\"@t\":\"2026-01-01T00:00:00Z\",\"@m\":\"entry ")
                .Append(i)
                .Append("\",\"Padding\":\"")
                .Append('p', 200)
                .Append("\"}\n");
        }
        var path = Write("many.clef", builder.ToString());

        var entries = await _reader.ReadEntriesAtOffsetsAsync(path, offsets);

        Assert.Equal(5000, entries.Count);
        for (var i = 0; i < offsets.Count; i += 250)
            Assert.Equal($"entry {i}", entries[offsets[i]]!.Message);
        Assert.Equal("entry 4999", entries[offsets[^1]]!.Message);
    }

    [Fact]
    public async Task ReadRange_StopsInFrontOfALineThatIsStillBeingWritten()
    {
        const string complete = "{\"@t\":\"2026-01-01T00:00:00Z\",\"@m\":\"whole\"}\n";
        const string partial = "{\"@t\":\"2026-01-01T00:00:01Z\",\"@m\":\"cut o";
        var path = Write("live.clef", complete + partial);

        var cursor = new ReadCursor();
        var entries = new List<LogEntry>();
        await foreach (var entry in _reader.ReadRangeAsync(path, 0, complete.Length + partial.Length, cursor))
            entries.Add(entry);

        Assert.Single(entries);
        Assert.Equal("whole", entries[0].Message);
        Assert.Equal(complete.Length, cursor.Position);

        // The writer finishes the line; the next read picks it up from the cursor, whole.
        File.AppendAllText(path, "ff\"}\n");
        var size = new FileInfo(path).Length;
        var next = new ReadCursor();
        entries.Clear();
        await foreach (var entry in _reader.ReadRangeAsync(path, cursor.Position, size, next))
            entries.Add(entry);

        Assert.Single(entries);
        Assert.Equal("cut off", entries[0].Message);
        Assert.Equal(size, next.Position);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }
}
