using Microsoft.Extensions.Logging.Abstractions;
using Serilog.Viewer.Infrastructure.Parsing;
using Serilog.Viewer.Infrastructure.Repository;
using Serilog.Viewer.Interfaces;
using Serilog.Viewer.Models;

namespace Serilog.Viewer.Core.Tests;

public class FileSystemLogRepositoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileSystemLogRepository _repo;

    public FileSystemLogRepositoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "slv-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var parsers = new ILogParser[] { new ClefLogParser(), new PlainTextLogParser() };
        var factory = new LogParserFactory(parsers);
        var reader = new Serilog.Viewer.Infrastructure.Reading.StreamingLogFileReader(factory);
        _repo = new FileSystemLogRepository(
            _tempDir,
            reader,
            NullLogger<FileSystemLogRepository>.Instance
        );
    }

    private void WriteClefFile(string name, IEnumerable<string> lines)
    {
        File.WriteAllLines(Path.Combine(_tempDir, name), lines);
    }

    [Fact]
    public async Task GetFilesAsync_ReturnsCreatedFile()
    {
        WriteClefFile(
            "test.clef",
            new[] { """{"@t":"2024-01-01T00:00:00Z","@l":"INF","@m":"hello"}""" }
        );

        var files = await _repo.GetFilesAsync(CancellationToken.None);
        Assert.Single(files);
        Assert.Equal("test.clef", files[0].Name);
    }

    [Fact]
    public async Task QueryAsync_ReturnsParsedEntries()
    {
        WriteClefFile(
            "app.clef",
            new[]
            {
                """{"@t":"2024-01-01T00:00:00Z","@l":"INF","@m":"msg1"}""",
                """{"@t":"2024-01-02T00:00:00Z","@l":"WRN","@m":"msg2"}""",
                """{"@t":"2024-01-03T00:00:00Z","@l":"ERR","@m":"msg3"}""",
            }
        );

        var result = await _repo.QueryAsync(new LogQuery { PageSize = 50 }, CancellationToken.None);
        Assert.Equal(3, result.TotalCount);
        Assert.Equal(3, result.Items.Count);
    }

    [Fact]
    public async Task QueryAsync_FiltersByLevel()
    {
        WriteClefFile(
            "levels.clef",
            new[]
            {
                """{"@t":"2024-01-01T00:00:00Z","@l":"INF","@m":"info"}""",
                """{"@t":"2024-01-01T01:00:00Z","@l":"ERR","@m":"error"}""",
            }
        );

        var result = await _repo.QueryAsync(
            new LogQuery
            {
                Levels = new List<LogLevel> { LogLevel.Error },
                PageSize = 50,
            },
            CancellationToken.None
        );

        Assert.Equal(1, result.TotalCount);
        Assert.Equal(LogLevel.Error, result.Items[0].Level);
    }

    [Fact]
    public async Task QueryAsync_FiltersBySearchText()
    {
        WriteClefFile(
            "search.clef",
            new[]
            {
                """{"@t":"2024-01-01T00:00:00Z","@l":"INF","@m":"needle in a haystack"}""",
                """{"@t":"2024-01-01T01:00:00Z","@l":"INF","@m":"nothing special"}""",
            }
        );

        var result = await _repo.QueryAsync(
            new LogQuery { SearchText = "needle", PageSize = 50 },
            CancellationToken.None
        );

        Assert.Equal(1, result.TotalCount);
        Assert.Contains("needle", result.Items[0].Message);
    }

    [Fact]
    public async Task GetStatsAsync_ComputesTotalCount()
    {
        WriteClefFile(
            "stats.clef",
            Enumerable
                .Range(0, 10)
                .Select(i =>
                    $$$"""{"@t":"2024-01-01T0{{{i % 9}}}:00:00Z","@l":"INF","@m":"entry {{{i}}}"}"""
                )
        );

        var stats = await _repo.GetStatsAsync(cancellationToken: CancellationToken.None);
        Assert.Equal(10, stats.TotalLogs);
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
