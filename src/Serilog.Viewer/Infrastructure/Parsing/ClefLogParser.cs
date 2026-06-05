using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Serilog.Viewer.Interfaces;
using Serilog.Viewer.Models;
using CoreLogLevel = Serilog.Viewer.Models.LogLevel;

namespace Serilog.Viewer.Infrastructure.Parsing;

/// <summary>
/// Parses Serilog Compact Log Event Format (CLEF) JSON lines.
/// </summary>
public sealed class ClefLogParser : ILogParser
{
    private static readonly JsonSerializerOptions _jsonOptions =
        new()
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };

    public LogFileFormat Format => LogFileFormat.Clef;

    public bool CanParse(string line)
    {
        var trimmed = line.AsSpan().TrimStart();
        return trimmed.StartsWith("{")
            && trimmed.Contains("\"@t\"".AsSpan(), StringComparison.Ordinal);
    }

    public LogEntry? Parse(string line, string fileName, long lineOffset)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            var timestamp = ParseTimestamp(root);
            var level = ParseLevel(root);
            var message = ParseMessage(root);
            var exception = ParseString(root, "@x");
            var properties = ParseProperties(root);

            properties.TryGetValue("SourceContext", out var srcCtx);
            properties.TryGetValue("CorrelationId", out var corrId);
            properties.TryGetValue("RequestId", out var reqId);
            properties.TryGetValue("TraceId", out var traceId);
            properties.TryGetValue("SpanId", out var spanId);

            return new LogEntry
            {
                Id = $"{fileName}:{lineOffset}",
                Timestamp = timestamp,
                Level = level,
                Message = message,
                RenderedMessage = ParseString(root, "@m") ?? ParseString(root, "@mt"),
                Exception = exception,
                SourceContext = srcCtx?.ToString(),
                CorrelationId = corrId?.ToString(),
                RequestId = reqId?.ToString(),
                TraceId = traceId?.ToString(),
                SpanId = spanId?.ToString(),
                Properties = properties,
                RawJson = line,
                LineOffset = lineOffset,
                FileName = fileName,
            };
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset ParseTimestamp(JsonElement root)
    {
        if (root.TryGetProperty("@t", out var t) && t.TryGetDateTimeOffset(out var dt))
            return dt;
        return DateTimeOffset.UtcNow;
    }

    private static CoreLogLevel ParseLevel(JsonElement root)
    {
        if (!root.TryGetProperty("@l", out var l))
            return CoreLogLevel.Information;
        return l.GetString()?.ToUpperInvariant() switch
        {
            "VERBOSE" or "VRB" => CoreLogLevel.Verbose,
            "DEBUG" or "DBG" => CoreLogLevel.Debug,
            "INFORMATION" or "INF" or "INFO" => CoreLogLevel.Information,
            "WARNING" or "WRN" or "WARN" => CoreLogLevel.Warning,
            "ERROR" or "ERR" => CoreLogLevel.Error,
            "FATAL" or "FTL" => CoreLogLevel.Fatal,
            _ => CoreLogLevel.Information,
        };
    }

    private static readonly Regex _templateToken =
        new(@"\{(@|\$)?(?<name>[^{}:]+?)(?::(?<format>[^{}]+?))?\}", RegexOptions.Compiled);

    private static string ParseMessage(JsonElement root)
    {
        if (root.TryGetProperty("@m", out var m) && m.ValueKind == JsonValueKind.String)
            return m.GetString() ?? string.Empty;

        if (!root.TryGetProperty("@mt", out var mt) || mt.ValueKind != JsonValueKind.String)
            return string.Empty;

        var template = mt.GetString() ?? string.Empty;

        return _templateToken.Replace(
            template,
            match =>
            {
                var name = match.Groups["name"].Value;
                if (root.TryGetProperty(name, out var prop))
                {
                    return prop.ValueKind switch
                    {
                        JsonValueKind.String => prop.GetString() ?? match.Value,
                        JsonValueKind.Number => prop.GetRawText(),
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        JsonValueKind.Null => "null",
                        _ => prop.GetRawText(),
                    };
                }
                return match.Value;
            }
        );
    }

    private static string? ParseString(JsonElement root, string key)
    {
        if (root.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    private static Dictionary<string, object?> ParseProperties(JsonElement root)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name.StartsWith('@'))
                continue;
            result[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.TryGetInt64(out var i)
                    ? i
                    : prop.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => prop.Value.GetRawText(),
            };
        }
        return result;
    }
}
