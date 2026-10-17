using Microsoft.Extensions.Logging.Abstractions;
using Serilog.Viewer.Infrastructure.Parsing;
using Serilog.Viewer.Infrastructure.Reading;
using Serilog.Viewer.Infrastructure.Repository;
using Serilog.Viewer.Interfaces;
using Serilog.Viewer.Models;

namespace Serilog.Viewer.Core.Tests;

/// <summary>
/// Covers the request-path behaviour that the index is responsible for: page ordering across
/// files, index-only entries versus re-read entries, search totals, and dashboard aggregates that
/// are cached per file but must still follow the file as it grows.
/// </summary>
public class QueryProjectionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileSystemLogRepository _repo;

    public QueryProjectionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "slv-proj-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var parsers = new ILogParser[] { new ClefLogParser(), new PlainTextLogParser() };
        var reader = new StreamingLogFileReader(new LogParserFactory(parsers));
        _repo = new FileSystemLogRepository(
            _tempDir,
            reader,
            NullLogger<FileSystemLogRepository>.Instance
        );
    }

    private static string Clef(
        DateTimeOffset ts,
        string level,
        string message,
        string source = "Tests.Source"
    ) =>
        $$$"""{"@t":"{{{ts:O}}}","@l":"{{{level}}}","@mt":"{{{message}}}","SourceContext":"{{{source}}}"}""";

    private void Write(string name, IEnumerable<string> lines) =>
        File.WriteAllLines(Path.Combine(_tempDir, name), lines);

    private void Append(string name, IEnumerable<string> lines) =>
        File.AppendAllLines(Path.Combine(_tempDir, name), lines);

    private static DateTimeOffset At(int seconds) =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds);

    [Fact]
    public async Task Query_AcrossFiles_IsNewestFirstWithStablePaging()
    {
        // Two files with interleaved timestamps: a.clef has even seconds, b.clef odd seconds.
        Write("a.clef", Enumerable.Range(0, 50).Select(i => Clef(At(i * 2), "INF", $"a-{i}")));
        Write("b.clef", Enumerable.Range(0, 50).Select(i => Clef(At(i * 2 + 1), "INF", $"b-{i}")));

        var all = new List<LogEntry>();
        for (var page = 1; page <= 5; page++)
        {
            var result = await _repo.QueryAsync(
                new LogQuery { Page = page, PageSize = 20, IncludeFullEntries = false }
            );
            Assert.Equal(100, result.TotalCount);
            all.AddRange(result.Items);
        }

        Assert.Equal(100, all.Count);
        Assert.Equal(100, all.Select(e => e.Message).Distinct().Count());
        var timestamps = all.Select(e => e.Timestamp).ToList();
        Assert.Equal(timestamps.OrderByDescending(t => t).ToList(), timestamps);
        Assert.Equal("b-49", all[0].Message);
        Assert.Equal("a-0", all[^1].Message);
    }

    [Fact]
    public async Task Query_IndexOnlyEntries_AgreeWithFullEntries()
    {
        Write(
            "mixed.clef",
            Enumerable.Range(0, 30).Select(i => Clef(At(i), i % 3 == 0 ? "ERR" : "INF", $"m-{i}", $"Src.{i % 4}"))
        );

        var query = new LogQuery { FileNames = ["mixed.clef"], Page = 1, PageSize = 30 };
        var full = await _repo.QueryAsync(query);
        var light = await _repo.QueryAsync(
            new LogQuery
            {
                FileNames = ["mixed.clef"],
                Page = 1,
                PageSize = 30,
                IncludeFullEntries = false,
            }
        );

        Assert.Equal(full.TotalCount, light.TotalCount);
        Assert.Equal(full.Items.Count, light.Items.Count);

        for (var i = 0; i < full.Items.Count; i++)
        {
            var f = full.Items[i];
            var l = light.Items[i];
            Assert.Equal(f.LineOffset, l.LineOffset);
            Assert.Equal(f.Timestamp.UtcDateTime, l.Timestamp.UtcDateTime);
            Assert.Equal(f.Level, l.Level);
            Assert.Equal(f.Message, l.Message);
            Assert.Equal(f.SourceContext, l.SourceContext);
            Assert.NotEmpty(f.RawJson);
            Assert.Empty(l.RawJson);
        }

        // The light entry carries enough to fetch the full one on demand.
        var detail = await _repo.GetEntryAsync(light.Items[0].FileName, light.Items[0].LineOffset);
        Assert.NotNull(detail);
        Assert.Equal(full.Items[0].RawJson, detail!.RawJson);
    }

    [Fact]
    public async Task Search_TotalCountCoversAllMatchesBeyondTheFirstPage()
    {
        Write(
            "search.clef",
            Enumerable.Range(0, 120).Select(i => Clef(At(i), "INF", i % 2 == 0 ? $"needle {i}" : $"hay {i}"))
        );

        var first = await _repo.QueryAsync(
            new LogQuery { SearchText = "needle", Page = 1, PageSize = 25, IncludeFullEntries = false }
        );
        Assert.Equal(60, first.TotalCount);
        Assert.Equal(25, first.Items.Count);
        Assert.All(first.Items, e => Assert.Contains("needle", e.Message));

        var beyond = await _repo.QueryAsync(
            new LogQuery { SearchText = "needle", Page = 4, PageSize = 25, IncludeFullEntries = false }
        );
        Assert.Empty(beyond.Items);
        Assert.Equal(60, beyond.TotalCount);
    }

    [Fact]
    public async Task Stats_FollowFileGrowth_AndMergeAcrossFiles()
    {
        Write("s1.clef", Enumerable.Range(0, 10).Select(i => Clef(At(i), "ERR", $"e-{i}", "Src.A")));
        Write("s2.clef", Enumerable.Range(0, 5).Select(i => Clef(At(100 + i), "WRN", $"w-{i}", "Src.B")));

        var stats = await _repo.GetStatsAsync();
        Assert.Equal(15, stats.TotalLogs);
        Assert.Equal(10, stats.Errors);
        Assert.Equal(5, stats.Warnings);
        Assert.Equal("Src.A", stats.TopSources[0].Source);
        Assert.Equal(10, stats.TopSources[0].Count);

        // Second call is served from the per-file cache and must be identical.
        var cached = await _repo.GetStatsAsync();
        Assert.Equal(stats.TotalLogs, cached.TotalLogs);
        Assert.Equal(stats.TopSources.Count, cached.TopSources.Count);

        // Growth of one file must invalidate only that file's cached aggregate.
        Append("s2.clef", Enumerable.Range(0, 20).Select(i => Clef(At(200 + i), "WRN", $"w2-{i}", "Src.B")));
        var grown = await _repo.GetStatsAsync();
        Assert.Equal(35, grown.TotalLogs);
        Assert.Equal(25, grown.Warnings);
        Assert.Equal("Src.B", grown.TopSources[0].Source);
        Assert.Equal(25, grown.TopSources[0].Count);

        // A file filter restricts the merge to the selected files.
        var only1 = await _repo.GetStatsAsync(["s1.clef"]);
        Assert.Equal(10, only1.TotalLogs);
        Assert.Single(only1.TopSources);
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
