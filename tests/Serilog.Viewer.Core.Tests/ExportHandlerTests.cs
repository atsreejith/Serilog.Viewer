using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog.Viewer.Handlers;
using Serilog.Viewer.Infrastructure.Parsing;
using Serilog.Viewer.Infrastructure.Reading;
using Serilog.Viewer.Infrastructure.Repository;
using Serilog.Viewer.Interfaces;

namespace Serilog.Viewer.Core.Tests;

/// <summary>
/// Exports are written to the response while the entries are read, and cover what the grid is
/// showing: the same files, levels and search.
/// </summary>
public class ExportHandlerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileSystemLogRepository _repo;

    public ExportHandlerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "slv-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var parsers = new ILogParser[] { new ClefLogParser(), new PlainTextLogParser() };
        var reader = new StreamingLogFileReader(new LogParserFactory(parsers));
        _repo = new FileSystemLogRepository(
            _tempDir,
            reader,
            NullLogger<FileSystemLogRepository>.Instance
        );

        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        string Line(string file, int i) =>
            $"{{\"@t\":\"{start.AddSeconds(i):O}\",\"@l\":\"{(i % 5 == 0 ? "Error" : "Information")}\",\"@m\":\"{file} entry {i}, with a comma and a \\\"quote\\\"\",\"SourceContext\":\"Tests.Export\"}}";

        File.WriteAllLines(Path.Combine(_tempDir, "a.clef"), Enumerable.Range(0, 2600).Select(i => Line("a", i)));
        File.WriteAllLines(Path.Combine(_tempDir, "b.clef"), Enumerable.Range(0, 100).Select(i => Line("b", i)));
    }

    /// <summary>
    /// Behaves like the server's response body: synchronous writes are refused. A handler that
    /// buffers into a writer and lets it flush synchronously works against a MemoryStream and
    /// fails in production.
    /// </summary>
    private sealed class AsyncOnlyStream : Stream
    {
        private readonly MemoryStream _inner = new();

        public byte[] ToArray() => _inner.ToArray();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() =>
            throw new InvalidOperationException("Synchronous operations are disallowed.");

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("Synchronous operations are disallowed.");

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            _inner.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            _inner.Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private async Task<(HttpResponse Response, string Body)> RunAsync(
        Func<HttpRequest, IResult> handler,
        string queryString
    )
    {
        var body = new AsyncOnlyStream();
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        context.Request.QueryString = new QueryString(queryString);
        context.Response.Body = body;

        await handler(context.Request).ExecuteAsync(context);
        return (context.Response, Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public async Task Csv_StreamsEveryMatchingEntry_NewestFirst()
    {
        var (response, csv) = await RunAsync(
            request => ExportHandler.ExportCsv(request, _repo, CancellationToken.None),
            "?files=a.clef"
        );

        Assert.Equal("text/csv", response.ContentType);
        Assert.Contains("attachment", response.Headers.ContentDisposition.ToString());

        var rows = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(r => r.TrimEnd('\r')).ToArray();
        Assert.Equal("Timestamp,Level,Message,SourceContext,CorrelationId,RequestId,Exception", rows[0]);
        Assert.Equal(2600 + 1, rows.Length);
        Assert.Contains("\"a entry 2599, with a comma and a \"\"quote\"\"\"", rows[1]);
        Assert.Contains("a entry 0,", rows[^1]);
        Assert.DoesNotContain(rows, r => r.Contains("b entry"));
    }

    [Fact]
    public async Task Export_HonoursTheLevelFilterTheGridSends()
    {
        var (_, csv) = await RunAsync(
            request => ExportHandler.ExportCsv(request, _repo, CancellationToken.None),
            "?level=Error"
        );

        var rows = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // Every fifth entry of both files is an error.
        Assert.Equal(520 + 20 + 1, rows.Length);
        Assert.All(rows.Skip(1), r => Assert.Contains(",Error,", r));
    }

    [Fact]
    public async Task Json_StreamsAnArrayOfFullEntries()
    {
        var (response, json) = await RunAsync(
            request => ExportHandler.ExportJson(request, _repo, CancellationToken.None),
            "?files=b.clef&sortDir=asc"
        );

        Assert.Equal("application/json", response.ContentType);

        using var document = JsonDocument.Parse(json);
        var entries = document.RootElement;
        Assert.Equal(100, entries.GetArrayLength());
        Assert.Equal("b entry 0, with a comma and a \"quote\"", entries[0].GetProperty("Message").GetString());
        Assert.Equal("Tests.Export", entries[99].GetProperty("SourceContext").GetString());
        Assert.NotEmpty(entries[0].GetProperty("RawJson").GetString()!);
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
