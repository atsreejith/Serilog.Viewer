using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Serilog.Viewer.Infrastructure.Repository;
using Serilog.Viewer.Interfaces;
using Serilog.Viewer.Models;

namespace Serilog.Viewer.Handlers;

/// <summary>
/// Exports the entries matching the current filters as a download.
/// <para>
/// The response is written as the entries are read. Building it in memory first meant holding
/// every exported entry, then the whole document as a string, then the same document again as
/// bytes — three copies of up to 50,000 full entries for one request.
/// </para>
/// </summary>
internal static class ExportHandler
{
    private const int MaxExportedEntries = 50000;

    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public static IResult ExportCsv(
        HttpRequest request,
        ILogRepository repository,
        CancellationToken cancellationToken
    )
    {
        var query = BuildExportQuery(request);

        return Results.Stream(
            async stream =>
            {
                await using var writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 64 * 1024,
                    leaveOpen: true
                );

                var row = new StringBuilder(1024);
                row.AppendLine(
                    "Timestamp,Level,Message,SourceContext,CorrelationId,RequestId,Exception"
                );

                await foreach (var e in ReadEntriesAsync(repository, query, cancellationToken))
                {
                    AppendCsv(row, e.Timestamp.ToString("O", CultureInfo.InvariantCulture)).Append(',');
                    AppendCsv(row, e.Level.ToString()).Append(',');
                    AppendCsv(row, e.Message).Append(',');
                    AppendCsv(row, e.SourceContext ?? "").Append(',');
                    AppendCsv(row, e.CorrelationId ?? "").Append(',');
                    AppendCsv(row, e.RequestId ?? "").Append(',');
                    AppendCsv(row, e.Exception ?? "").AppendLine();

                    // Asynchronous writes only: the server refuses synchronous response I/O.
                    await writer.WriteAsync(row, cancellationToken);
                    row.Clear();
                }

                await writer.WriteAsync(row, cancellationToken);
                await writer.FlushAsync(cancellationToken);
            },
            "text/csv",
            $"logs-export-{DateTime.UtcNow:yyyyMMddHHmmss}.csv"
        );
    }

    public static IResult ExportJson(
        HttpRequest request,
        ILogRepository repository,
        CancellationToken cancellationToken
    )
    {
        var query = BuildExportQuery(request);

        return Results.Stream(
            stream =>
                JsonSerializer.SerializeAsync(
                    stream,
                    ReadEntriesAsync(repository, query, cancellationToken),
                    _jsonOptions,
                    cancellationToken
                ),
            "application/json",
            $"logs-export-{DateTime.UtcNow:yyyyMMddHHmmss}.json"
        );
    }

    private static IAsyncEnumerable<LogEntry> ReadEntriesAsync(
        ILogRepository repository,
        LogQuery query,
        CancellationToken cancellationToken
    ) =>
        repository is FileSystemLogRepository fileSystem
            ? fileSystem.QueryFullEntriesAsync(query, cancellationToken)
            : ReadPageAsync(repository, query, cancellationToken);

    /// <summary>For any other repository: the same entries through its ordinary paged query.</summary>
    private static async IAsyncEnumerable<LogEntry> ReadPageAsync(
        ILogRepository repository,
        LogQuery query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var result = await repository.QueryAsync(query, cancellationToken);
        foreach (var entry in result.Items)
            yield return entry;
    }

    private static StringBuilder AppendCsv(StringBuilder row, string value)
    {
        if (value.AsSpan().IndexOfAny(',', '"', '\n') < 0)
            return row.Append(value);

        row.Append('"');
        foreach (var c in value)
        {
            if (c == '"')
                row.Append('"');
            row.Append(c);
        }
        return row.Append('"');
    }

    /// <summary>
    /// The export covers what the grid is showing: the same files, levels, time range, search and
    /// sort order, without paging.
    /// </summary>
    private static LogQuery BuildExportQuery(HttpRequest request)
    {
        var query = LogsHandler.BuildQuery(request);

        return new LogQuery
        {
            FileName = query.FileName,
            FileNames = query.FileNames,
            From = query.From,
            To = query.To,
            Levels = query.Levels,
            SearchText = query.SearchText,
            SourceContext = query.SourceContext,
            CorrelationId = query.CorrelationId,
            RequestId = query.RequestId,
            Page = 1,
            PageSize = MaxExportedEntries,
            SortBy = query.SortBy,
            SortDescending = query.SortDescending,
            IncludeFullEntries = true,
        };
    }
}
