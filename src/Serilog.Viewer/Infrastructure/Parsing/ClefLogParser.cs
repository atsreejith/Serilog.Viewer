using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using Serilog.Viewer.Interfaces;
using Serilog.Viewer.Models;
using CoreLogLevel = Serilog.Viewer.Models.LogLevel;

namespace Serilog.Viewer.Infrastructure.Parsing;

/// <summary>
/// Parses Serilog Compact Log Event Format (CLEF) JSON lines.
/// </summary>
public sealed class ClefLogParser : ILogParser, IIndexLineParser
{
    public LogFileFormat Format => LogFileFormat.Clef;

    bool IIndexLineParser.AcceptsContinuationLines => false;

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

    // ------------------------------------------------------------------------------------------
    // Index fast path
    //
    // Indexing parses every line of every file but keeps seven fields of each. The path above
    // decodes the line to a string, builds a JsonDocument from it (transcoding back to UTF-8),
    // materialises every property into a dictionary and renders the whole message — then nearly
    // all of that is discarded. The path below makes a single forward pass over the UTF-8 bytes,
    // remembers where each property's value sits, and decodes only what the index keeps.
    //
    // It must report exactly what the path above reports for the same line. Anything it cannot
    // prove it handles identically (escaped property names, invalid UTF-8 inside an escaped
    // string) is handed to the full parse instead.
    // ------------------------------------------------------------------------------------------

    private enum FastResult
    {
        Parsed,
        Invalid,
        NeedsFullParse,
    }

    private struct ValueRange
    {
        public int Start;
        public int Length;
        public JsonTokenType Type;
    }

    private struct PropertyRange
    {
        public int NameStart;
        public int NameLength;
        public ValueRange Value;
    }

    bool IIndexLineParser.TryParseIndexLine(ReadOnlySpan<byte> utf8Line, ref IndexLineFields fields)
    {
        FastResult result;
        try
        {
            result = TryParseIndexLineFast(utf8Line, ref fields);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            result = FastResult.NeedsFullParse;
        }

        return result switch
        {
            FastResult.Parsed => true,
            FastResult.Invalid => false,
            _ => TryParseIndexLineViaFullParse(utf8Line, ref fields),
        };
    }

    private bool TryParseIndexLineViaFullParse(ReadOnlySpan<byte> utf8Line, ref IndexLineFields fields)
    {
        var entry = Parse(Encoding.UTF8.GetString(utf8Line), string.Empty, 0);
        if (entry is null)
            return false;

        fields.TimestampUtcTicks = entry.Timestamp.UtcTicks;
        fields.Level = entry.Level;
        fields.Message = IndexLimits.Truncate(entry.Message, IndexLimits.MaxMessageChars);
        fields.Exception = entry.Exception is null
            ? null
            : IndexLimits.Truncate(entry.Exception, IndexLimits.MaxExceptionChars);
        fields.SourceContext = entry.SourceContext;
        fields.CorrelationId = entry.CorrelationId;
        fields.RequestId = entry.RequestId;
        return true;
    }

    private static FastResult TryParseIndexLineFast(ReadOnlySpan<byte> line, ref IndexLineFields fields)
    {
        var reader = new Utf8JsonReader(line);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return FastResult.Invalid;

        PropertyRange[]? rented = null;
        Span<PropertyRange> properties = stackalloc PropertyRange[32];
        var count = 0;

        try
        {
            ValueRange m = default;
            ValueRange mt = default;
            ValueRange x = default;
            var hasTimestamp = false;
            long timestampTicks = 0;
            var level = CoreLogLevel.Information;
            string? sourceContext = null;
            string? correlationId = null;
            string? requestId = null;

            while (true)
            {
                if (!reader.Read())
                    return FastResult.Invalid;
                if (reader.TokenType == JsonTokenType.EndObject)
                    break;

                // Property name. An escaped name would have to be unescaped before it could be
                // compared; it never occurs in Serilog output, so leave it to the full parse.
                if (reader.ValueIsEscaped)
                    return FastResult.NeedsFullParse;

                var nameStart = (int)reader.TokenStartIndex + 1;
                var nameLength = reader.ValueSpan.Length;
                var name = line.Slice(nameStart, nameLength);

                if (!reader.Read())
                    return FastResult.Invalid;

                var type = reader.TokenType;
                var valueStart = (int)reader.TokenStartIndex;

                // 0 = not an id field, 1 = SourceContext, 2 = CorrelationId, 3 = RequestId
                var idField = 0;
                string? idValue = null;

                if (nameLength >= 2 && name[0] == (byte)'@')
                {
                    if (name.SequenceEqual("@t"u8))
                    {
                        if (type != JsonTokenType.String)
                            return FastResult.Invalid;
                        hasTimestamp = reader.TryGetDateTimeOffset(out var parsed);
                        timestampTicks = parsed.UtcTicks;
                    }
                    else if (name.SequenceEqual("@l"u8))
                    {
                        if (type == JsonTokenType.String)
                            level = ParseLevel(ref reader);
                        else if (type == JsonTokenType.Null)
                            level = CoreLogLevel.Information;
                        else
                            return FastResult.Invalid;
                    }
                }
                else
                {
                    if (Ascii.EqualsIgnoreCase(name, "SourceContext"u8))
                        idField = 1;
                    else if (Ascii.EqualsIgnoreCase(name, "CorrelationId"u8))
                        idField = 2;
                    else if (Ascii.EqualsIgnoreCase(name, "RequestId"u8))
                        idField = 3;

                    if (idField != 0)
                        idValue = ScalarToString(ref reader);
                }

                if (type is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    reader.Skip();

                var value = new ValueRange
                {
                    Start = valueStart,
                    Length = (int)reader.BytesConsumed - valueStart,
                    Type = type,
                };

                if (idField != 0)
                {
                    if (type is JsonTokenType.StartObject or JsonTokenType.StartArray)
                        idValue = Encoding.UTF8.GetString(line.Slice(value.Start, value.Length));

                    switch (idField)
                    {
                        case 1:
                            sourceContext = idValue;
                            break;
                        case 2:
                            correlationId = idValue;
                            break;
                        default:
                            requestId = idValue;
                            break;
                    }
                }
                else if (nameLength >= 2 && name[0] == (byte)'@')
                {
                    if (name.SequenceEqual("@m"u8))
                        m = value;
                    else if (name.SequenceEqual("@mt"u8))
                        mt = value;
                    else if (name.SequenceEqual("@x"u8))
                        x = value;
                }

                if (count == properties.Length)
                {
                    var bigger = ArrayPool<PropertyRange>.Shared.Rent(properties.Length * 2);
                    properties.CopyTo(bigger);
                    if (rented is not null)
                        ArrayPool<PropertyRange>.Shared.Return(rented);
                    rented = bigger;
                    properties = bigger;
                }

                properties[count++] = new PropertyRange
                {
                    NameStart = nameStart,
                    NameLength = nameLength,
                    Value = value,
                };
            }

            // Anything but white space after the closing brace makes the line invalid JSON.
            if (reader.Read())
                return FastResult.Invalid;

            string message;
            if (m.Type == JsonTokenType.String)
                message = DecodeString(line, m, IndexLimits.MaxMessageChars);
            else if (mt.Type == JsonTokenType.String)
                message = RenderTemplate(line, mt, properties[..count]);
            else
                message = string.Empty;

            fields.TimestampUtcTicks = hasTimestamp ? timestampTicks : DateTimeOffset.UtcNow.UtcTicks;
            fields.Level = level;
            fields.Message = message;
            fields.Exception =
                x.Type == JsonTokenType.String
                    ? DecodeString(line, x, IndexLimits.MaxExceptionChars)
                    : null;
            fields.SourceContext = sourceContext;
            fields.CorrelationId = correlationId;
            fields.RequestId = requestId;
            return FastResult.Parsed;
        }
        finally
        {
            if (rented is not null)
                ArrayPool<PropertyRange>.Shared.Return(rented);
        }
    }

    private static CoreLogLevel ParseLevel(ref Utf8JsonReader reader)
    {
        if (reader.ValueIsEscaped)
        {
            return reader.GetString()?.ToUpperInvariant() switch
            {
                "VERBOSE" or "VRB" => CoreLogLevel.Verbose,
                "DEBUG" or "DBG" => CoreLogLevel.Debug,
                "WARNING" or "WRN" or "WARN" => CoreLogLevel.Warning,
                "ERROR" or "ERR" => CoreLogLevel.Error,
                "FATAL" or "FTL" => CoreLogLevel.Fatal,
                _ => CoreLogLevel.Information,
            };
        }

        var value = reader.ValueSpan;
        if (Is(value, "VERBOSE"u8) || Is(value, "VRB"u8))
            return CoreLogLevel.Verbose;
        if (Is(value, "DEBUG"u8) || Is(value, "DBG"u8))
            return CoreLogLevel.Debug;
        if (Is(value, "WARNING"u8) || Is(value, "WRN"u8) || Is(value, "WARN"u8))
            return CoreLogLevel.Warning;
        if (Is(value, "ERROR"u8) || Is(value, "ERR"u8))
            return CoreLogLevel.Error;
        if (Is(value, "FATAL"u8) || Is(value, "FTL"u8))
            return CoreLogLevel.Fatal;
        return CoreLogLevel.Information;

        static bool Is(ReadOnlySpan<byte> value, ReadOnlySpan<byte> name) =>
            Ascii.EqualsIgnoreCase(value, name);
    }

    /// <summary>
    /// The string form the full parse gives a property value once it has been through the
    /// property dictionary. Containers are handled by the caller, from their raw text.
    /// </summary>
    private static string? ScalarToString(ref Utf8JsonReader reader) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var integer)
                ? integer.ToString()
                : reader.GetDouble().ToString(),
            JsonTokenType.True => bool.TrueString,
            JsonTokenType.False => bool.FalseString,
            _ => null,
        };

    /// <summary>
    /// Decodes a JSON string value, keeping at most <paramref name="maxChars"/> characters.
    /// </summary>
    private static string DecodeString(ReadOnlySpan<byte> line, ValueRange range, int maxChars)
    {
        var inner = line.Slice(range.Start + 1, range.Length - 2);
        if (inner.IndexOf((byte)'\\') < 0)
        {
            // No escapes: the bytes are the text. Decode only a prefix long enough to be sure of
            // holding maxChars characters, so a huge value that is about to be cut to a few
            // hundred characters is never decoded in full.
            if (maxChars < (int.MaxValue - 4) / 3 && inner.Length > maxChars * 3 + 4)
                inner = inner[..(maxChars * 3 + 4)];

            return IndexLimits.Truncate(Encoding.UTF8.GetString(inner), maxChars);
        }

        // Escaped. Applications log whole payloads — an HTTP response body, a SQL batch — as one
        // string of tens of kilobytes, of which the index keeps a few hundred characters, so
        // unescape only as far as needed.
        if (maxChars <= IndexLimits.MaxExceptionChars)
        {
            char[]? rented = null;
            Span<char> buffer =
                maxChars <= 512 ? stackalloc char[512] : (rented = ArrayPool<char>.Shared.Rent(maxChars));
            try
            {
                var written = UnescapePrefix(inner, buffer[..maxChars]);
                if (written >= 0)
                    return new string(buffer[..written]);
            }
            finally
            {
                if (rented is not null)
                    ArrayPool<char>.Shared.Return(rented);
            }
        }

        var reader = new Utf8JsonReader(line.Slice(range.Start, range.Length));
        reader.Read();
        return IndexLimits.Truncate(reader.GetString() ?? string.Empty, maxChars);
    }

    /// <summary>
    /// Unescapes the body of a JSON string (the text between the quotes) into
    /// <paramref name="destination"/>, stopping when it is full. Returns the number of characters
    /// written, or -1 for anything it will not vouch for — an unpaired surrogate escape, or a
    /// character that does not fit whole in what is left of the destination — which the caller
    /// then decodes the long way.
    /// </summary>
    private static int UnescapePrefix(ReadOnlySpan<byte> inner, Span<char> destination)
    {
        var written = 0;
        var i = 0;

        while (i < inner.Length && written < destination.Length)
        {
            var escape = inner[i..].IndexOf((byte)'\\');
            var literal = escape < 0 ? inner[i..] : inner.Slice(i, escape);

            if (literal.Length > 0)
            {
                var status = Utf8.ToUtf16(
                    literal,
                    destination[written..],
                    out _,
                    out var charsWritten
                );
                written += charsWritten;

                if (status == OperationStatus.DestinationTooSmall)
                    return written == destination.Length ? written : -1;
            }

            if (escape < 0)
                break;

            i += escape + 1;
            if (i >= inner.Length)
                return -1;
            if (written == destination.Length)
                break;

            char c;
            switch (inner[i++])
            {
                case (byte)'"':
                    c = '"';
                    break;
                case (byte)'\\':
                    c = '\\';
                    break;
                case (byte)'/':
                    c = '/';
                    break;
                case (byte)'b':
                    c = '\b';
                    break;
                case (byte)'f':
                    c = '\f';
                    break;
                case (byte)'n':
                    c = '\n';
                    break;
                case (byte)'r':
                    c = '\r';
                    break;
                case (byte)'t':
                    c = '\t';
                    break;
                case (byte)'u':
                    if (!TryReadHex4(inner, i, out c))
                        return -1;
                    i += 4;

                    if (char.IsLowSurrogate(c))
                        return -1;

                    if (char.IsHighSurrogate(c))
                    {
                        // Must be followed by the escape of its low half.
                        if (
                            i + 6 > inner.Length
                            || inner[i] != (byte)'\\'
                            || inner[i + 1] != (byte)'u'
                            || !TryReadHex4(inner, i + 2, out var low)
                            || !char.IsLowSurrogate(low)
                        )
                            return -1;

                        destination[written++] = c;
                        if (written == destination.Length)
                            return written;

                        c = low;
                        i += 6;
                    }
                    break;
                default:
                    return -1;
            }

            destination[written++] = c;
        }

        return written;
    }

    private static bool TryReadHex4(ReadOnlySpan<byte> text, int index, out char value)
    {
        value = default;
        if (index + 4 > text.Length)
            return false;

        var result = 0;
        for (var k = 0; k < 4; k++)
        {
            int b = text[index + k];
            int digit;
            if (b >= '0' && b <= '9')
                digit = b - '0';
            else if ((b | 0x20) >= 'a' && (b | 0x20) <= 'f')
                digit = (b | 0x20) - 'a' + 10;
            else
                return false;

            result = (result << 4) | digit;
        }

        value = (char)result;
        return true;
    }

    /// <summary>
    /// Renders a message template against the line's properties, stopping once the index's message
    /// limit is reached. Mirrors <see cref="ParseMessage"/> and its token expression.
    /// </summary>
    private static string RenderTemplate(
        ReadOnlySpan<byte> line,
        ValueRange templateRange,
        ReadOnlySpan<PropertyRange> properties
    )
    {
        var template = DecodeString(line, templateRange, int.MaxValue);
        if (!template.Contains('{'))
            return IndexLimits.Truncate(template, IndexLimits.MaxMessageChars);

        Span<char> output = stackalloc char[IndexLimits.MaxMessageChars];
        var written = 0;
        var text = template.AsSpan();
        var position = 0;

        while (position < text.Length && written < output.Length)
        {
            var open = text[position..].IndexOf('{');
            if (open < 0)
            {
                Append(text[position..], output, ref written);
                break;
            }

            Append(text.Slice(position, open), output, ref written);
            open += position;

            if (TryMatchToken(text, open, out var nameStart, out var nameLength, out var end))
            {
                if (TryFindProperty(line, properties, text.Slice(nameStart, nameLength), out var value))
                    AppendValue(line, value, output, ref written);
                else
                    Append(text[open..end], output, ref written);

                position = end;
            }
            else
            {
                Append("{", output, ref written);
                position = open + 1;
            }
        }

        return new string(output[..written]);
    }

    /// <summary>
    /// Matches <c>{[@|$]name[:format]}</c> at <paramref name="open"/>, the same shape as
    /// <see cref="_templateToken"/>.
    /// </summary>
    private static bool TryMatchToken(
        ReadOnlySpan<char> text,
        int open,
        out int nameStart,
        out int nameLength,
        out int end
    )
    {
        nameLength = 0;
        end = 0;
        nameStart = open + 1;
        if (nameStart >= text.Length)
            return false;

        var afterPrefix = text[nameStart] is '@' or '$' ? nameStart + 1 : nameStart;
        var k = afterPrefix;
        while (k < text.Length && text[k] != '{' && text[k] != '}' && text[k] != ':')
            k++;

        // With nothing after the prefix, the prefix character is itself the name ("{@}").
        if (k > afterPrefix)
            nameStart = afterPrefix;

        nameLength = k - nameStart;
        if (nameLength == 0 || k >= text.Length)
            return false;

        if (text[k] == '}')
        {
            end = k + 1;
            return true;
        }

        if (text[k] != ':')
            return false;

        var formatStart = k + 1;
        var f = formatStart;
        while (f < text.Length && text[f] != '{' && text[f] != '}')
            f++;

        if (f == formatStart || f >= text.Length || text[f] != '}')
            return false;

        end = f + 1;
        return true;
    }

    /// <summary>Finds the last property called <paramref name="name"/>, as JsonElement does.</summary>
    private static bool TryFindProperty(
        ReadOnlySpan<byte> line,
        ReadOnlySpan<PropertyRange> properties,
        ReadOnlySpan<char> name,
        out ValueRange value
    )
    {
        value = default;
        var maxBytes = Encoding.UTF8.GetMaxByteCount(name.Length);
        Span<byte> utf8Name = maxBytes <= 512 ? stackalloc byte[512] : new byte[maxBytes];
        var nameBytes = Encoding.UTF8.GetBytes(name, utf8Name);
        utf8Name = utf8Name[..nameBytes];

        for (var i = properties.Length - 1; i >= 0; i--)
        {
            ref readonly var property = ref properties[i];
            if (
                property.NameLength == nameBytes
                && line.Slice(property.NameStart, property.NameLength).SequenceEqual(utf8Name)
            )
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    private static void AppendValue(
        ReadOnlySpan<byte> line,
        ValueRange value,
        Span<char> output,
        ref int written
    )
    {
        switch (value.Type)
        {
            case JsonTokenType.String:
                var inner = line.Slice(value.Start + 1, value.Length - 2);
                if (inner.IndexOf((byte)'\\') < 0)
                {
                    AppendUtf8(inner, output, ref written);
                }
                else
                {
                    var unescaped = UnescapePrefix(inner, output[written..]);
                    if (unescaped >= 0)
                        written += unescaped;
                    else
                        Append(DecodeString(line, value, output.Length - written), output, ref written);
                }
                break;
            case JsonTokenType.True:
                Append("true", output, ref written);
                break;
            case JsonTokenType.False:
                Append("false", output, ref written);
                break;
            case JsonTokenType.Null:
                Append("null", output, ref written);
                break;
            default:
                // Numbers, objects and arrays render as their raw JSON text.
                AppendUtf8(line.Slice(value.Start, value.Length), output, ref written);
                break;
        }
    }

    private static void Append(ReadOnlySpan<char> text, Span<char> output, ref int written)
    {
        var count = Math.Min(text.Length, output.Length - written);
        text[..count].CopyTo(output[written..]);
        written += count;
    }

    private static void AppendUtf8(ReadOnlySpan<byte> utf8, Span<char> output, ref int written)
    {
        var remaining = output.Length - written;
        if (remaining == 0)
            return;

        if (utf8.Length <= remaining)
        {
            // UTF-8 never decodes to more UTF-16 units than it has bytes, so this always fits.
            Utf8.ToUtf16(utf8, output[written..], out _, out var charsWritten);
            written += charsWritten;
            return;
        }

        var take = Math.Min(utf8.Length, remaining * 3 + 4);
        Append(Encoding.UTF8.GetString(utf8[..take]), output, ref written);
    }
}
