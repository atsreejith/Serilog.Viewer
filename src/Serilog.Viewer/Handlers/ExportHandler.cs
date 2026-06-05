using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Serilog.Viewer.Interfaces;
using Serilog.Viewer.Models;
using CoreLogLevel = Serilog.Viewer.Models.LogLevel;

namespace Serilog.Viewer.Handlers;

internal static class ExportHandler
{
    public static async Task<IResult> ExportCsv(
        HttpRequest request,
        ILogRepository repository,
        CancellationToken cancellationToken
    )
    {
        var query = BuildExportQuery(request);
        var exportQuery = new LogQuery
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
            PageSize = 50000,
            SortBy = query.SortBy,
            SortDescending = query.SortDescending,
        };
        var result = await repository.QueryAsync(exportQuery, cancellationToken);

        var csv = BuildCsv(result.Items);
        var bytes = Encoding.UTF8.GetBytes(csv);
        return Results.File(bytes, "text/csv", $"logs-export-{DateTime.UtcNow:yyyyMMddHHmmss}.csv");
    }

    public static async Task<IResult> ExportJson(
        HttpRequest request,
        ILogRepository repository,
        CancellationToken cancellationToken
    )
    {
        var query = BuildExportQuery(request);
        var exportQuery = new LogQuery
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
            PageSize = 50000,
            SortBy = query.SortBy,
            SortDescending = query.SortDescending,
        };
        var result = await repository.QueryAsync(exportQuery, cancellationToken);

        var json = System.Text.Json.JsonSerializer.Serialize(
            result.Items,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }
        );
        var bytes = Encoding.UTF8.GetBytes(json);
        return Results.File(
            bytes,
            "application/json",
            $"logs-export-{DateTime.UtcNow:yyyyMMddHHmmss}.json"
        );
    }

    private static string BuildCsv(IReadOnlyList<LogEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Timestamp,Level,Message,SourceContext,CorrelationId,RequestId,Exception");

        foreach (var e in entries)
        {
            sb.AppendLine(
                string.Join(
                    ",",
                    CsvEscape(e.Timestamp.ToString("O", CultureInfo.InvariantCulture)),
                    CsvEscape(e.Level.ToString()),
                    CsvEscape(e.Message),
                    CsvEscape(e.SourceContext ?? ""),
                    CsvEscape(e.CorrelationId ?? ""),
                    CsvEscape(e.RequestId ?? ""),
                    CsvEscape(e.Exception ?? "")
                )
            );
        }

        return sb.ToString();
    }

    private static string CsvEscape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    private static LogQuery BuildExportQuery(HttpRequest request)
    {
        var q = request.Query;
        DateTimeOffset? from = null;
        DateTimeOffset? to = null;
        if (DateTimeOffset.TryParse(q["from"], out var f))
            from = f;
        if (DateTimeOffset.TryParse(q["to"], out var t))
            to = t;

        return new LogQuery
        {
            FileName = q["file"],
            From = from,
            To = to,
            SearchText = q["search"],
            Page = 1,
            PageSize = 50000,
        };
    }
}
