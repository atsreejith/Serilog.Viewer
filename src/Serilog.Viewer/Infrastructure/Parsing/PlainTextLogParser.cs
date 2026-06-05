using System.Text.RegularExpressions;
using Serilog.Viewer.Interfaces;
using Serilog.Viewer.Models;
using CoreLogLevel = Serilog.Viewer.Models.LogLevel;

namespace Serilog.Viewer.Infrastructure.Parsing;

/// <summary>
/// Parses plain-text Serilog output (default outputTemplate style).
/// </summary>
public sealed partial class PlainTextLogParser : ILogParser
{
    [GeneratedRegex(
        @"^(?<ts>\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:[Z]|[+-]\d{2}:\d{2})?)\s*\[?(?<level>VRB|DBG|INF|WRN|ERR|FTL|VERBOSE|DEBUG|INFORMATION|WARNING|ERROR|FATAL)\]?\s*(?<msg>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    )]
    private static partial Regex LogLineRegex();

    public LogFileFormat Format => LogFileFormat.PlainText;

    public bool CanParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;
        return LogLineRegex().IsMatch(line);
    }

    public LogEntry? Parse(string line, string fileName, long lineOffset)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var match = LogLineRegex().Match(line);
        if (!match.Success)
            return null;

        var timestamp = ParseTimestamp(match.Groups["ts"].Value);
        var level = ParseLevel(match.Groups["level"].Value);
        var message = match.Groups["msg"].Value.Trim();

        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        ExtractInlineProperties(message, properties);
        properties.TryGetValue("SourceContext", out var srcCtx);
        properties.TryGetValue("CorrelationId", out var corrId);
        properties.TryGetValue("RequestId", out var reqId);

        return new LogEntry
        {
            Id = $"{fileName}:{lineOffset}",
            Timestamp = timestamp,
            Level = level,
            Message = message,
            RenderedMessage = message,
            SourceContext = srcCtx?.ToString(),
            CorrelationId = corrId?.ToString(),
            RequestId = reqId?.ToString(),
            Properties = properties,
            RawJson = line,
            LineOffset = lineOffset,
            FileName = fileName,
        };
    }

    private static DateTimeOffset ParseTimestamp(string value)
    {
        if (DateTimeOffset.TryParse(value, out var dt))
            return dt;
        return DateTimeOffset.UtcNow;
    }

    private static CoreLogLevel ParseLevel(string value) =>
        value.ToUpperInvariant() switch
        {
            "VRB" or "VERBOSE" => CoreLogLevel.Verbose,
            "DBG" or "DEBUG" => CoreLogLevel.Debug,
            "WRN" or "WARNING" or "WARN" => CoreLogLevel.Warning,
            "ERR" or "ERROR" => CoreLogLevel.Error,
            "FTL" or "FATAL" => CoreLogLevel.Fatal,
            _ => CoreLogLevel.Information,
        };

    private static void ExtractInlineProperties(string message, Dictionary<string, object?> props)
    {
        var matches = Regex.Matches(message, @"\{(\w+)=([^}]+)\}");
        foreach (Match m in matches)
            props[m.Groups[1].Value] = m.Groups[2].Value;

        var scMatch = Regex.Match(message, @"\[([A-Za-z][A-Za-z0-9_.]+)\]\s*$");
        if (scMatch.Success)
            props["SourceContext"] = scMatch.Groups[1].Value;
    }
}
