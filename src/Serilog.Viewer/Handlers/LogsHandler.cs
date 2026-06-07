using Microsoft.AspNetCore.Http;
using Serilog.Viewer.Interfaces;
using Serilog.Viewer.Models;
using CoreLogLevel = Serilog.Viewer.Models.LogLevel;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Serilog.Viewer.Handlers;

internal static class LogsHandler
{
    public static async Task<IResult> GetLogs(
        HttpRequest request,
        ILogRepository repository,
        CancellationToken cancellationToken
    )
    {
        var query = BuildQuery(request);
        var beforeMemory = GC.GetTotalMemory(forceFullCollection: false);
        var start = Stopwatch.GetTimestamp();
        var result = await repository.QueryAsync(query, cancellationToken);
        var durationMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var afterMemory = GC.GetTotalMemory(forceFullCollection: false);

        return Results.Ok(
            new PagedResult<LogEntry>
            {
                Items = result.Items,
                TotalCount = result.TotalCount,
                Page = result.Page,
                PageSize = result.PageSize,
                Performance = new QueryPerformanceMetrics
                {
                    DurationMs = (long)Math.Round(durationMs),
                    ServerPeakMemoryBytes = Math.Max(beforeMemory, afterMemory),
                    ServerPeakMemoryFormatted = FormatBytes(Math.Max(beforeMemory, afterMemory)),
                },
            }
        );
    }

    public static async Task<IResult> GetDetails(
        string fileName,
        long offset,
        ILogRepository repository,
        CancellationToken cancellationToken
    )
    {
        var entry = await repository.GetEntryAsync(fileName, offset, cancellationToken);
        return entry is null ? Results.NotFound() : Results.Ok(entry);
    }

    public static async Task<IResult> GetStats(
        HttpRequest request,
        ILogRepository repository,
        CancellationToken cancellationToken
    )
    {
        var fileNames = request.Query["files"].ToArray();
        var stats = await repository.GetStatsAsync(
            fileNames.Length > 0 ? Array.ConvertAll(fileNames, f => f ?? string.Empty) : null,
            cancellationToken
        );
        return Results.Ok(stats);
    }

    public static async Task<IResult> Search(
        HttpRequest request,
        ILogRepository repository,
        CancellationToken cancellationToken
    )
    {
        var query = BuildQuery(request);

        var response = request.HttpContext.Response;
        response.ContentType = "application/x-ndjson";
        response.StatusCode = 200;

        await foreach (var entry in repository.StreamAsync(query, cancellationToken))
        {
            var line = System.Text.Json.JsonSerializer.Serialize(entry) + "\n";
            await response.WriteAsync(line, cancellationToken);
            await response.Body.FlushAsync(cancellationToken);
        }

        return Results.Empty;
    }

    private static LogQuery BuildQuery(HttpRequest request)
    {
        var q = request.Query;

        DateTimeOffset? from = null;
        DateTimeOffset? to = null;

        if (DateTimeOffset.TryParse(q["from"], out var fromParsed))
            from = fromParsed;
        if (DateTimeOffset.TryParse(q["to"], out var toParsed))
            to = toParsed;

        var levels = q["level"]
            .ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => Enum.TryParse<CoreLogLevel>(l, true, out _))
            .Select(l => Enum.Parse<CoreLogLevel>(l, true))
            .ToList();

        int.TryParse(q["page"], out var page);
        int.TryParse(q["pageSize"], out var pageSize);

        return new LogQuery
        {
            FileName = q["file"],
            FileNames = q["files"].Count > 0 ? [.. q["files"]!] : null,
            From = from,
            To = to,
            Levels = levels.Count > 0 ? levels : null,
            SearchText = q["search"],
            SourceContext = q["sourceContext"],
            CorrelationId = q["correlationId"],
            RequestId = q["requestId"],
            Page = page > 0 ? page : 1,
            PageSize = pageSize is > 0 and <= 1000 ? pageSize : 100,
            SortBy = q["sortBy"].FirstOrDefault() ?? "Timestamp",
            SortDescending = !string.Equals(
                q["sortDir"],
                "asc",
                StringComparison.OrdinalIgnoreCase
            ),
        };
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
