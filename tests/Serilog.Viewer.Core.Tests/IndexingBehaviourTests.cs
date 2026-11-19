using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog.Viewer.Infrastructure.Parsing;
using Serilog.Viewer.Infrastructure.Reading;
using Serilog.Viewer.Infrastructure.Repository;
using Serilog.Viewer.Interfaces;
using Serilog.Viewer.Models;

namespace Serilog.Viewer.Core.Tests;

/// <summary>
/// What the index must get right about the files themselves: entries that span lines, files that
/// are still being written, many files at once, and queries that combine levels.
/// </summary>
public class IndexingBehaviourTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileSystemLogRepository _repo;

    public IndexingBehaviourTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "slv-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var parsers = new ILogParser[] { new ClefLogParser(), new PlainTextLogParser() };
        var reader = new StreamingLogFileReader(new LogParserFactory(parsers));
        _repo = new FileSystemLogRepository(
            _tempDir,
            reader,
            NullLogger<FileSystemLogRepository>.Instance
        );
    }

    private string PathOf(string name) => Path.Combine(_tempDir, name);

    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>
    /// Writes a file and stamps it as modified in the future, which keeps it "still being written"
    /// for the indexer however slowly the test runs.
    /// </summary>
    private void WriteLive(string name, string content)
    {
        File.WriteAllText(PathOf(name), content, Utf8);
        File.SetLastWriteTimeUtc(PathOf(name), DateTime.UtcNow.AddHours(1));
    }

    private void AppendLive(string name, string content)
    {
        File.AppendAllText(PathOf(name), content, Utf8);
        File.SetLastWriteTimeUtc(PathOf(name), DateTime.UtcNow.AddHours(2));
    }

    private static LogQuery Light(int page = 1, int pageSize = 100) =>
        new()
        {
            Page = page,
            PageSize = pageSize,
            IncludeFullEntries = false,
        };

    private const string Trace =
        "System.InvalidOperationException: Order 12 is in an invalid state\n"
        + "   at Shop.Orders.OrderService.Ship(Int32 id) in OrderService.cs:line 142\n"
        + "   at Shop.Api.OrdersController.Ship(Int32 id) in OrdersController.cs:line 58\n";

    [Fact]
    public async Task PlainText_StackTracesAreIndexedSearchableAndReturnedWithTheEntry()
    {
        File.WriteAllText(
            PathOf("app.log"),
            "2026-01-01 00:00:00.000 +00:00 [INF] Starting up\n"
                + "2026-01-01 00:00:01.000 +00:00 [ERR] Order 12 failed\n"
                + Trace
                + "2026-01-01 00:00:02.000 +00:00 [INF] Still running\n"
        );

        // Three entries, not six lines and not three lines with the trace thrown away.
        var all = await _repo.QueryAsync(Light());
        Assert.Equal(3, all.TotalCount);

        // Text that only occurs in the stack trace finds the entry it belongs to.
        var found = await _repo.QueryAsync(
            new LogQuery { SearchText = "OrdersController.cs", IncludeFullEntries = false }
        );
        Assert.Equal(1, found.TotalCount);
        Assert.Equal("Order 12 failed", found.Items[0].Message);
        Assert.Contains("invalid state", found.Items[0].Exception);

        var detail = await _repo.GetEntryAsync(found.Items[0].FileName, found.Items[0].LineOffset);
        Assert.NotNull(detail);
        Assert.Equal(LogLevel.Error, detail.Level);
        Assert.Contains("OrderService.cs:line 142", detail.Exception);

        var full = await _repo.QueryAsync(new LogQuery { SearchText = "OrdersController.cs" });
        Assert.Contains("OrderService.cs:line 142", full.Items[0].Exception);
    }

    [Fact]
    public async Task PlainText_StackTraceWrittenAfterItsEntryWasIndexed_IsAttachedToThatEntry()
    {
        WriteLive(
            "app.log",
            "2026-01-01 00:00:00.000 +00:00 [INF] Starting up\n"
                + "2026-01-01 00:00:01.000 +00:00 [ERR] Order 12 failed\n"
        );
        Assert.Equal(2, (await _repo.QueryAsync(Light())).TotalCount);

        // The logger had only flushed the first line of the event when the index looked.
        AppendLive("app.log", Trace + "2026-01-01 00:00:02.000 +00:00 [INF] Still running\n");

        var all = await _repo.QueryAsync(Light());
        Assert.Equal(3, all.TotalCount);

        var found = await _repo.QueryAsync(
            new LogQuery { SearchText = "OrdersController.cs", IncludeFullEntries = false }
        );
        Assert.Equal(1, found.TotalCount);
        Assert.Equal("Order 12 failed", found.Items[0].Message);
    }

    [Fact]
    public async Task GetEntry_AtAnOffsetWhereNoEntryStarts_ReturnsNull()
    {
        const string first = "2026-01-01 00:00:00.000 +00:00 [ERR] Order 12 failed\n";
        File.WriteAllText(
            PathOf("app.log"),
            first + Trace + "2026-01-01 00:00:02.000 +00:00 [INF] Still running\n"
        );

        // Inside the stack trace. Reading on to the next line that parses would return
        // "Still running", which is not the entry at this offset.
        Assert.Null(await _repo.GetEntryAsync("app.log", first.Length));
        Assert.Equal("Order 12 failed", (await _repo.GetEntryAsync("app.log", 0))!.Message);
    }

    [Fact]
    public async Task Clef_HalfWrittenLastLine_IsIndexedOnceItIsComplete()
    {
        WriteLive(
            "live.clef",
            "{\"@t\":\"2026-01-01T00:00:00Z\",\"@m\":\"whole\"}\n"
                + "{\"@t\":\"2026-01-01T00:00:01Z\",\"@m\":\"cut o"
        );

        var before = await _repo.QueryAsync(Light());
        Assert.Equal(1, before.TotalCount);

        AppendLive("live.clef", "ff\"}\n{\"@t\":\"2026-01-01T00:00:02Z\",\"@m\":\"third\"}\n");

        var after = await _repo.QueryAsync(Light());
        Assert.Equal(3, after.TotalCount);
        Assert.Equal(
            new[] { "third", "cut off", "whole" },
            after.Items.Select(e => e.Message)
        );
    }

    [Fact]
    public async Task PlainText_HalfWrittenLastLine_IsNotIndexedTruncated()
    {
        WriteLive(
            "live.log",
            "2026-01-01 00:00:00.000 +00:00 [INF] whole\n"
                + "2026-01-01 00:00:01.000 +00:00 [INF] cut o"
        );

        var before = await _repo.QueryAsync(Light());
        Assert.Equal(1, before.TotalCount);

        AppendLive("live.log", "ff\n");

        var after = await _repo.QueryAsync(Light());
        Assert.Equal(2, after.TotalCount);
        Assert.Equal("cut off", after.Items[0].Message);
    }

    [Fact]
    public async Task LastLineWithoutLineBreak_IsIndexedWhenTheFileIsNotBeingWritten()
    {
        // A finished file whose last line simply has no trailing line break.
        File.WriteAllText(
            PathOf("done.log"),
            "2026-01-01 00:00:00.000 +00:00 [INF] first\n"
                + "2026-01-01 00:00:01.000 +00:00 [INF] last line, no line break"
        );
        File.SetLastWriteTimeUtc(PathOf("done.log"), DateTime.UtcNow.AddMinutes(-5));

        // And a JSON one that is still "live": a complete object is taken straight away.
        WriteLive(
            "live.clef",
            "{\"@t\":\"2026-01-01T00:00:02Z\",\"@m\":\"json first\"}\n"
                + "{\"@t\":\"2026-01-01T00:00:03Z\",\"@m\":\"json last\"}"
        );

        var result = await _repo.QueryAsync(Light());

        Assert.Equal(4, result.TotalCount);
        Assert.Contains(result.Items, e => e.Message == "last line, no line break");
        Assert.Contains(result.Items, e => e.Message == "json last");
    }

    [Fact]
    public async Task SeveralLevelsAtOnce_PageInTimeOrderWithCorrectTotals()
    {
        var levels = new[] { "INF", "WRN", "ERR", "DBG", "INF", "FTL" };
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        string Line(string file, int i) =>
            $"{{\"@t\":\"{start.AddSeconds(i):O}\",\"@l\":\"{levels[i % levels.Length]}\",\"@m\":\"{file}-{i}\"}}";

        // Interleaved timestamps across two files.
        File.WriteAllLines(PathOf("a.clef"), Enumerable.Range(0, 300).Where(i => i % 2 == 0).Select(i => Line("a", i)));
        File.WriteAllLines(PathOf("b.clef"), Enumerable.Range(0, 300).Where(i => i % 2 == 1).Select(i => Line("b", i)));

        static bool Wanted(int i) => (i % 6) is 1 or 2 or 5; // WRN, ERR, FTL
        var wanted = new List<LogLevel> { LogLevel.Warning, LogLevel.Error, LogLevel.Fatal };

        foreach (var descending in new[] { true, false })
        {
            var expectedAll = Enumerable.Range(0, 300).Where(Wanted).ToList();
            var expectedA = expectedAll.Where(i => i % 2 == 0).ToList();
            if (descending)
            {
                expectedAll.Reverse();
                expectedA.Reverse();
            }

            // All files, then one file: the two shapes the page query is built for.
            foreach (var (fileNames, expected) in new[]
            {
                ((IReadOnlyList<string>?)null, expectedAll),
                (new[] { "a.clef" }, expectedA),
            })
            {
                var seen = new List<string>();
                for (var page = 1; ; page++)
                {
                    var result = await _repo.QueryAsync(
                        new LogQuery
                        {
                            FileNames = fileNames,
                            Levels = wanted,
                            Page = page,
                            PageSize = 17,
                            SortDescending = descending,
                            IncludeFullEntries = false,
                        }
                    );
                    Assert.Equal(expected.Count, result.TotalCount);
                    if (result.Items.Count == 0)
                        break;
                    seen.AddRange(result.Items.Select(e => e.Message));
                }

                Assert.Equal(
                    expected.Select(i => $"{(i % 2 == 0 ? "a" : "b")}-{i}").ToList(),
                    seen
                );
            }
        }
    }

    [Fact]
    public async Task Totals_FollowFiltersAndFileSelection()
    {
        string Line(int i, string level, string extra = "") =>
            $"{{\"@t\":\"2026-01-01T00:00:{i % 60:D2}Z\",\"@l\":\"{level}\",\"@m\":\"m{i}\"{extra}}}";

        File.WriteAllLines(
            PathOf("one.clef"),
            Enumerable.Range(0, 40).Select(i => Line(i, i % 4 == 0 ? "ERR" : "INF", i == 7 ? ",\"RequestId\":\"req-7\"" : ""))
        );
        File.WriteAllLines(PathOf("two.clef"), Enumerable.Range(0, 20).Select(i => Line(i, "WRN")));

        async Task<int> Count(LogQuery query) => (await _repo.QueryAsync(query)).TotalCount;

        Assert.Equal(60, await Count(Light()));
        Assert.Equal(40, await Count(new LogQuery { FileNames = ["one.clef"], IncludeFullEntries = false }));
        Assert.Equal(10, await Count(new LogQuery { Levels = [LogLevel.Error], IncludeFullEntries = false }));
        Assert.Equal(30, await Count(new LogQuery { Levels = [LogLevel.Error, LogLevel.Warning], IncludeFullEntries = false }));
        Assert.Equal(0, await Count(new LogQuery { FileNames = ["two.clef"], Levels = [LogLevel.Error], IncludeFullEntries = false }));
        Assert.Equal(1, await Count(new LogQuery { RequestId = "REQ-7", IncludeFullEntries = false }));
        Assert.Equal(0, await Count(new LogQuery { SearchText = "nothing matches this", IncludeFullEntries = false }));

        // Totals keep up as a file grows.
        File.AppendAllLines(PathOf("two.clef"), Enumerable.Range(20, 5).Select(i => Line(i, "ERR")));
        Assert.Equal(65, await Count(Light()));
        Assert.Equal(15, await Count(new LogQuery { Levels = [LogLevel.Error], IncludeFullEntries = false }));
    }

    [Fact]
    public async Task FullEntries_AreStreamedInQueryOrder()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        File.WriteAllLines(
            PathOf("export.clef"),
            Enumerable
                .Range(0, 2500)
                .Select(i =>
                    $"{{\"@t\":\"{start.AddSeconds(i):O}\",\"@mt\":\"entry {{N}}\",\"N\":{i},\"Payload\":\"{new string('p', 300)}\"}}"
                )
        );

        var entries = new List<LogEntry>();
        await foreach (var entry in _repo.QueryFullEntriesAsync(new LogQuery { Page = 1, PageSize = 50000 }))
            entries.Add(entry);

        Assert.Equal(2500, entries.Count);
        Assert.Equal("entry 2499", entries[0].Message);
        Assert.Equal("entry 0", entries[^1].Message);
        Assert.All(entries, e => Assert.NotEmpty(e.RawJson));
        Assert.True(entries[0].Properties.ContainsKey("Payload"));
    }

    [Fact]
    public async Task ManyFiles_FirstQueryServesTheNewestAndTheRestIsBackfilled()
    {
        // Five files of about 9 MB. Together they exceed what one request indexes before it
        // answers, so this covers all three treatments: indexed whole, indexed from a window at
        // the end, and left entirely to the backfill.
        const int files = 5;
        const int entriesPerFile = 60_000;
        var padding = new string('x', 100);
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        for (var f = 0; f < files; f++)
        {
            var path = PathOf($"app-{f}.log");
            using (var writer = new StreamWriter(path, false, Utf8, 1 << 20))
            {
                for (var i = 0; i < entriesPerFile; i++)
                {
                    var ts = start.AddDays(f).AddSeconds(i);
                    writer.WriteLine($"{ts:yyyy-MM-dd HH:mm:ss.fff zzz} [INF] file{f}-entry-{i:D6} {padding}");
                }
            }
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(f - files));
        }

        var first = await _repo.QueryAsync(Light(pageSize: 10));

        // The newest entry of the newest file is there straight away, without waiting for the
        // other files to be read.
        Assert.Equal($"file{files - 1}-entry-{entriesPerFile - 1:D6} {padding}", first.Items[0].Message);
        Assert.InRange(first.TotalCount, entriesPerFile, files * entriesPerFile - 1);

        var deadline = DateTime.UtcNow.AddMinutes(3);
        var total = first.TotalCount;
        while (total != files * entriesPerFile && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250);
            total = (await _repo.QueryAsync(Light(pageSize: 1))).TotalCount;
        }
        Assert.Equal(files * entriesPerFile, total);

        // Every entry exactly once, in order, including across the points where a file was split
        // between the first request and the backfill.
        var oldest = await _repo.QueryAsync(
            new LogQuery { Page = 1, PageSize = 5, SortDescending = false, IncludeFullEntries = false }
        );
        Assert.StartsWith("file0-entry-000000", oldest.Items[0].Message);

        var stats = await _repo.GetStatsAsync();
        Assert.Equal(files * entriesPerFile, stats.TotalLogs);

        var oneFile = await _repo.QueryAsync(
            new LogQuery { FileNames = ["app-1.log"], Page = 1, PageSize = 1, IncludeFullEntries = false }
        );
        Assert.Equal(entriesPerFile, oneFile.TotalCount);
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
