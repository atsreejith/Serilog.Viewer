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

    private void AppendClefFile(string name, IEnumerable<string> lines)
    {
        File.AppendAllLines(Path.Combine(_tempDir, name), lines);
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
    public async Task GetFileAsync_ReturnsCreatedFile()
    {
        WriteClefFile(
            "download.clef",
            new[] { """{"@t":"2024-01-01T00:00:00Z","@l":"INF","@m":"hello"}""" }
        );

        var file = await _repo.GetFileAsync("download.clef", CancellationToken.None);

        Assert.NotNull(file);
        Assert.Equal("download.clef", file.Name);
    }

    [Theory]
    [InlineData("../secret.clef")]
    [InlineData("..\\secret.clef")]
    [InlineData("nested/../../secret.clef")]
    public async Task GetFileAsync_RejectsTraversal(string fileName)
    {
        var file = await _repo.GetFileAsync(fileName, CancellationToken.None);

        Assert.Null(file);
    }

    [Fact]
    public async Task DeleteFileAsync_RemovesCreatedFile()
    {
        WriteClefFile(
            "delete.clef",
            new[] { """{"@t":"2024-01-01T00:00:00Z","@l":"INF","@m":"hello"}""" }
        );

        var deleted = await _repo.DeleteFileAsync("delete.clef", CancellationToken.None);
        var files = await _repo.GetFilesAsync(CancellationToken.None);

        Assert.True(deleted);
        Assert.DoesNotContain(files, f => f.Name == "delete.clef");
    }

    [Fact]
    public async Task DeleteFileAsync_RejectsTraversal()
    {
        var deleted = await _repo.DeleteFileAsync("../secret.clef", CancellationToken.None);

        Assert.False(deleted);
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
    public async Task QueryAsync_PagesTimestampDescendingWithoutLoadingEveryRetainedEntry()
    {
        WriteClefFile(
            "paging.clef",
            Enumerable
                .Range(0, 25)
                .Select(i =>
                    $$$"""{"@t":"2024-01-01T00:{{{i:D2}}}:00Z","@l":"INF","@m":"entry {{{i}}}"}"""
                )
        );

        var result = await _repo.QueryAsync(
            new LogQuery
            {
                Page = 2,
                PageSize = 10,
                SortBy = "Timestamp",
                SortDescending = true,
            },
            CancellationToken.None
        );

        Assert.Equal(25, result.TotalCount);
        Assert.Equal(10, result.Items.Count);
        Assert.Equal("entry 14", result.Items[0].Message);
        Assert.Equal("entry 5", result.Items[^1].Message);
    }

    [Fact]
    public async Task QueryAsync_UpdatesIndexForAppendedEntries()
    {
        WriteClefFile(
            "append.clef",
            new[] { """{"@t":"2024-01-01T00:00:00Z","@l":"INF","@m":"first"}""" }
        );

        var first = await _repo.QueryAsync(new LogQuery { PageSize = 50 }, CancellationToken.None);

        AppendClefFile(
            "append.clef",
            new[] { """{"@t":"2024-01-01T00:01:00Z","@l":"ERR","@m":"second"}""" }
        );

        var second = await _repo.QueryAsync(new LogQuery { PageSize = 50 }, CancellationToken.None);

        Assert.Equal(1, first.TotalCount);
        Assert.Equal(2, second.TotalCount);
        Assert.Contains(second.Items, entry => entry.Message == "second");
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

    [Fact]
    public async Task GetStatsAsync_UsesUpdatedIndexForAppendedEntries()
    {
        WriteClefFile(
            "stats-append.clef",
            new[]
            {
                """{"@t":"2024-01-01T00:00:00Z","@l":"INF","@m":"first","SourceContext":"App.One"}""",
            }
        );

        var first = await _repo.GetStatsAsync(cancellationToken: CancellationToken.None);

        AppendClefFile(
            "stats-append.clef",
            new[]
            {
                """{"@t":"2024-01-01T01:00:00Z","@l":"ERR","@m":"second","SourceContext":"App.Two"}""",
            }
        );

        var second = await _repo.GetStatsAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(1, first.TotalLogs);
        Assert.Equal(2, second.TotalLogs);
        Assert.Equal(1, second.Errors);
        Assert.Contains(second.TopSources, source => source.Source == "App.Two");
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
