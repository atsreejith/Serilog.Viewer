using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog.Viewer.Infrastructure.Parsing;
using Serilog.Viewer.Infrastructure.Reading;
using Serilog.Viewer.Infrastructure.Repository;
using Serilog.Viewer.Interfaces;
using Serilog.Viewer.Models;

namespace Serilog.Viewer.Core.Tests;

/// <summary>
/// Files above the tail-first threshold are indexed from a window at the END first, so the newest
/// entries (what the default newest-first view shows) are queryable before the rest of the file has
/// been read. The head is then backfilled in the background.
/// <para>
/// These tests exercise the invariant that matters: the split must be invisible in the final
/// result. Every entry is eventually indexed exactly once, in the right order, regardless of where
/// the tail window happened to start.
/// </para>
/// </summary>
public class TailFirstIndexingTests : IDisposable
{
    // Must exceed LogFileIndexStore.TailFirstThresholdBytes (32 MB) to take the tail-first path.
    private const int LargeFileEntries = 260_000;

    private readonly string _tempDir;
    private readonly FileSystemLogRepository _repo;

    public TailFirstIndexingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "slv-tailfirst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var parsers = new ILogParser[] { new ClefLogParser(), new PlainTextLogParser() };
        var reader = new StreamingLogFileReader(new LogParserFactory(parsers));
        _repo = new FileSystemLogRepository(
            _tempDir,
            reader,
            NullLogger<FileSystemLogRepository>.Instance
        );
    }

    /// <summary>
    /// Writes a plain-text log whose message is the entry's ordinal, so ordering and completeness
    /// can be asserted exactly. Padding makes the file large enough to trigger tail-first indexing
    /// without needing millions of entries.
    /// </summary>
    private string WriteLargeLog(string name, int entries)
    {
        var path = Path.Combine(_tempDir, name);
        var padding = new string('x', 100);
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        using var writer = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 20);
        for (var i = 0; i < entries; i++)
        {
            var ts = start.AddSeconds(i);
            writer.WriteLine(
                $"{ts:yyyy-MM-dd HH:mm:ss.fff zzz} [INF] entry-{i:D7} {padding}"
            );
        }
        return path;
    }

    /// <summary>
    /// Polls until the background head backfill stops adding rows, so assertions run against the
    /// settled index rather than a partially-filled one.
    /// </summary>
    private async Task<int> WaitForFullIndexAsync(int expected)
    {
        var deadline = DateTime.UtcNow.AddMinutes(3);
        var last = -1;
        while (DateTime.UtcNow < deadline)
        {
            var result = await _repo.QueryAsync(
                new LogQuery { Page = 1, PageSize = 1 },
                CancellationToken.None
            );
            if (result.TotalCount == expected)
                return result.TotalCount;

            last = result.TotalCount;
            await Task.Delay(250);
        }
        return last;
    }

    [Fact]
    public async Task LargeFile_FirstQueryReturnsNewestEntriesImmediately()
    {
        WriteLargeLog("big.txt", LargeFileEntries);

        var result = await _repo.QueryAsync(
            new LogQuery
            {
                Page = 1,
                PageSize = 10,
                SortBy = "Timestamp",
                SortDescending = true,
            },
            CancellationToken.None
        );

        // The tail window is indexed before the query returns, so the very newest entry is present
        // even though most of the file has not been read yet.
        Assert.NotEmpty(result.Items);
        Assert.Contains($"entry-{LargeFileEntries - 1:D7}", result.Items[0].Message);
    }

    [Fact]
    public async Task LargeFile_BackfillEventuallyIndexesEveryEntryExactlyOnce()
    {
        WriteLargeLog("big.txt", LargeFileEntries);

        // Kick off indexing, then wait for the background backfill to complete.
        await _repo.QueryAsync(new LogQuery { Page = 1, PageSize = 1 }, CancellationToken.None);
        var total = await WaitForFullIndexAsync(LargeFileEntries);

        Assert.Equal(LargeFileEntries, total);
    }

    [Fact]
    public async Task LargeFile_AfterBackfill_OldestEntryIsReachable()
    {
        WriteLargeLog("big.txt", LargeFileEntries);

        await _repo.QueryAsync(new LogQuery { Page = 1, PageSize = 1 }, CancellationToken.None);
        await WaitForFullIndexAsync(LargeFileEntries);

        // Ascending sort surfaces the very first entry in the file — it lives in the head, which
        // only the backfill indexes.
        var oldest = await _repo.QueryAsync(
            new LogQuery
            {
                Page = 1,
                PageSize = 1,
                SortBy = "Timestamp",
                SortDescending = false,
            },
            CancellationToken.None
        );

        Assert.Single(oldest.Items);
        Assert.Contains("entry-0000000", oldest.Items[0].Message);
    }

    [Fact]
    public async Task LargeFile_AfterBackfill_EntriesRemainInTimestampOrderAcrossTheSplit()
    {
        WriteLargeLog("big.txt", LargeFileEntries);

        await _repo.QueryAsync(new LogQuery { Page = 1, PageSize = 1 }, CancellationToken.None);
        await WaitForFullIndexAsync(LargeFileEntries);

        // Walk a page from the middle of the file, which is where the head/tail boundary falls.
        var page = await _repo.QueryAsync(
            new LogQuery
            {
                Page = 500,
                PageSize = 200,
                SortBy = "Timestamp",
                SortDescending = false,
            },
            CancellationToken.None
        );

        Assert.NotEmpty(page.Items);
        var timestamps = page.Items.Select(e => e.Timestamp).ToList();
        Assert.Equal(timestamps.OrderBy(t => t).ToList(), timestamps);

        // No duplicates: the head backfill and the tail window must not overlap.
        var messages = page.Items.Select(e => e.Message).ToList();
        Assert.Equal(messages.Count, messages.Distinct().Count());
    }

    [Fact]
    public async Task SmallFile_IsFullyIndexedOnFirstQuery()
    {
        // Below the tail-first threshold the old single-pass forward scan still applies, so the
        // full count must be correct immediately with no backfill involved.
        WriteLargeLog("small.txt", 500);

        var result = await _repo.QueryAsync(
            new LogQuery { Page = 1, PageSize = 1 },
            CancellationToken.None
        );

        Assert.Equal(500, result.TotalCount);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // A background backfill may still hold the index database open; the temp directory is
            // disposable either way.
        }
    }
}
