using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;
using Serilog.Viewer.Interfaces;
using Serilog.Viewer.Models;
using CoreLogLevel = Serilog.Viewer.Models.LogLevel;

namespace Serilog.Viewer.Infrastructure.Parsing;

/// <summary>
/// Parses plain-text Serilog output (default outputTemplate style).
/// <para>
/// An entry starts on a line of the form <c>timestamp [LEVEL] message</c>. Any following line that
/// does not start an entry — an exception and its stack trace, or a message that contains line
/// breaks — belongs to the entry before it and becomes that entry's <see cref="LogEntry.Exception"/>.
/// </para>
/// </summary>
public sealed partial class PlainTextLogParser : ILogParser, IMultiLineLogParser, IIndexLineParser
{
    /// <summary>
    /// How much of a line is examined for the timestamp and level. Both are plain ASCII, so the
    /// header can be recognised on raw UTF-8 bytes and on a string by the same code.
    /// </summary>
    private const int MaxHeaderLength = 96;

    [GeneratedRegex(@"\{(\w+)=([^}]+)\}")]
    private static partial Regex InlinePropertyRegex();

    public LogFileFormat Format => LogFileFormat.PlainText;

    bool IIndexLineParser.AcceptsContinuationLines => true;

    public bool CanParse(string line) => TryParseHeader(line, out _);

    bool IMultiLineLogParser.IsEntryStart(string line) => TryParseHeader(line, out _);

    public LogEntry? Parse(string line, string fileName, long lineOffset) =>
        ParseEntry(line, null, fileName, lineOffset);

    LogEntry? IMultiLineLogParser.Parse(
        string line,
        string? continuation,
        string fileName,
        long lineOffset
    ) => ParseEntry(line, continuation, fileName, lineOffset);

    private static LogEntry? ParseEntry(
        string line,
        string? continuation,
        string fileName,
        long lineOffset
    )
    {
        if (!TryParseHeader(line, out var header))
            return null;

        var message = line.AsSpan(header.MessageStart).Trim().ToString();

        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (message.Contains('{'))
        {
            foreach (var match in InlinePropertyRegex().EnumerateMatches(message))
            {
                SplitInlineProperty(message.AsSpan(match.Index, match.Length), out var name, out var value);
                properties[name.ToString()] = value.ToString();
            }
        }

        if (TryGetTrailingSourceContext(message, out var trailingSource))
            properties["SourceContext"] = trailingSource.ToString();

        properties.TryGetValue("SourceContext", out var sourceContext);
        properties.TryGetValue("CorrelationId", out var correlationId);
        properties.TryGetValue("RequestId", out var requestId);

        var hasContinuation = !string.IsNullOrEmpty(continuation);

        return new LogEntry
        {
            Id = $"{fileName}:{lineOffset}",
            Timestamp = new DateTimeOffset(header.LocalTicks, new TimeSpan(header.OffsetTicks)),
            Level = header.Level,
            Message = message,
            RenderedMessage = message,
            Exception = hasContinuation ? continuation : null,
            SourceContext = sourceContext?.ToString(),
            CorrelationId = correlationId?.ToString(),
            RequestId = requestId?.ToString(),
            Properties = properties,
            RawJson = hasContinuation ? line + "\n" + continuation : line,
            LineOffset = lineOffset,
            FileName = fileName,
        };
    }

    bool IIndexLineParser.TryParseIndexLine(ReadOnlySpan<byte> utf8Line, ref IndexLineFields fields)
    {
        if (!TryParseHeader(utf8Line, out var header))
            return false;

        // UTF-8 never decodes to more UTF-16 units than it has bytes, so the byte length is a
        // safe buffer size.
        var messageBytes = utf8Line[header.MessageStart..];
        char[]? rented = null;
        Span<char> buffer =
            messageBytes.Length <= 1024
                ? stackalloc char[1024]
                : (rented = ArrayPool<char>.Shared.Rent(messageBytes.Length));

        try
        {
            var written = Encoding.UTF8.GetChars(messageBytes, buffer);
            ReadOnlySpan<char> message = buffer[..written].Trim();

            string? sourceContext = null;
            string? correlationId = null;
            string? requestId = null;

            if (message.Contains('{'))
            {
                foreach (var match in InlinePropertyRegex().EnumerateMatches(message))
                {
                    SplitInlineProperty(message.Slice(match.Index, match.Length), out var name, out var value);
                    if (name.Equals("SourceContext", StringComparison.OrdinalIgnoreCase))
                        sourceContext = value.ToString();
                    else if (name.Equals("CorrelationId", StringComparison.OrdinalIgnoreCase))
                        correlationId = value.ToString();
                    else if (name.Equals("RequestId", StringComparison.OrdinalIgnoreCase))
                        requestId = value.ToString();
                }
            }

            if (TryGetTrailingSourceContext(message, out var trailingSource))
                sourceContext = trailingSource.ToString();

            fields.TimestampUtcTicks = header.LocalTicks - header.OffsetTicks;
            fields.Level = header.Level;
            fields.Message = new string(
                message.Length > IndexLimits.MaxMessageChars
                    ? message[..IndexLimits.MaxMessageChars]
                    : message
            );
            fields.Exception = null;
            fields.SourceContext = sourceContext;
            fields.CorrelationId = correlationId;
            fields.RequestId = requestId;
            return true;
        }
        finally
        {
            if (rented is not null)
                ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>Splits one <c>{Name=Value}</c> match into its two parts.</summary>
    private static void SplitInlineProperty(
        ReadOnlySpan<char> match,
        out ReadOnlySpan<char> name,
        out ReadOnlySpan<char> value
    )
    {
        var inner = match[1..^1];
        var equals = inner.IndexOf('=');
        name = inner[..equals];
        value = inner[(equals + 1)..];
    }

    /// <summary>
    /// Recognises a message that ends in <c>[Some.Source.Context]</c>, the shape produced by
    /// output templates that append <c>[{SourceContext}]</c>.
    /// </summary>
    private static bool TryGetTrailingSourceContext(
        ReadOnlySpan<char> message,
        out ReadOnlySpan<char> sourceContext
    )
    {
        sourceContext = default;
        if (message.Length < 4 || message[^1] != ']')
            return false;

        var open = message.LastIndexOf('[');
        if (open < 0)
            return false;

        var inner = message[(open + 1)..^1];
        if (inner.Length < 2 || !char.IsAsciiLetter(inner[0]))
            return false;

        foreach (var c in inner)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_' && c != '.')
                return false;
        }

        sourceContext = inner;
        return true;
    }

    private struct Header
    {
        /// <summary>The timestamp as written in the line, in ticks.</summary>
        public long LocalTicks;

        /// <summary>Its UTC offset: the one written in the line, else the local time zone's.</summary>
        public long OffsetTicks;
        public CoreLogLevel Level;

        /// <summary>Index of the first character of the message.</summary>
        public int MessageStart;
    }

    private static bool TryParseHeader(ReadOnlySpan<char> line, out Header header)
    {
        Span<byte> ascii = stackalloc byte[MaxHeaderLength];
        var length = Math.Min(line.Length, MaxHeaderLength);
        for (var i = 0; i < length; i++)
        {
            var c = line[i];
            ascii[i] = c < 128 ? (byte)c : (byte)0xFF;
        }

        if (!TryParseHeaderPrefix(ascii[..length], out header))
            return false;

        while (header.MessageStart < line.Length && line[header.MessageStart] is ' ' or '\t')
            header.MessageStart++;
        return true;
    }

    private static bool TryParseHeader(ReadOnlySpan<byte> line, out Header header)
    {
        var prefix = line.Length > MaxHeaderLength ? line[..MaxHeaderLength] : line;
        if (!TryParseHeaderPrefix(prefix, out header))
            return false;

        while (header.MessageStart < line.Length && line[header.MessageStart] is (byte)' ' or (byte)'\t')
            header.MessageStart++;
        return true;
    }

    /// <summary>
    /// Recognises <c>yyyy-MM-dd[T ]HH:mm:ss[.fffffff][ ][Z|±HH[:]mm] [[]LEVEL[]]</c> at the start
    /// of <paramref name="line"/>. Hand-written rather than a regular expression because this
    /// runs once for every line of every file that is indexed.
    /// </summary>
    private static bool TryParseHeaderPrefix(ReadOnlySpan<byte> line, out Header header)
    {
        header = default;
        if (line.Length < 19)
            return false;

        if (
            !(
                TwoDigits(line, 0, out var century)
                && TwoDigits(line, 2, out var yearOfCentury)
                && line[4] == '-'
                && TwoDigits(line, 5, out var month)
                && line[7] == '-'
                && TwoDigits(line, 8, out var day)
                && (line[10] == ' ' || line[10] == 'T')
                && TwoDigits(line, 11, out var hour)
                && line[13] == ':'
                && TwoDigits(line, 14, out var minute)
                && line[16] == ':'
                && TwoDigits(line, 17, out var second)
            )
        )
            return false;

        var year = century * 100 + yearOfCentury;
        if (
            year < 1
            || month is < 1 or > 12
            || day < 1
            || day > DateTime.DaysInMonth(year, month)
            || hour > 23
            || minute > 59
            || second > 59
        )
            return false;

        var p = 19;
        long fraction = 0;
        if (p + 1 < line.Length && (line[p] == '.' || line[p] == ',') && IsDigit(line[p + 1]))
        {
            p++;
            var digits = 0;
            while (p < line.Length && IsDigit(line[p]))
            {
                if (digits < 7)
                {
                    fraction = fraction * 10 + (line[p] - '0');
                    digits++;
                }
                p++;
            }
            for (; digits < 7; digits++)
                fraction *= 10;
        }

        var localTicks = new DateTime(year, month, day, hour, minute, second).Ticks + fraction;

        // Optional UTC offset, optionally separated from the time by one space. Serilog's default
        // file template renders it as " +00:00".
        long offsetTicks = 0;
        var hasOffset = false;
        var q = p;
        if (q < line.Length && IsSpace(line[q]))
            q++;

        if (q < line.Length && (line[q] == 'Z' || line[q] == 'z'))
        {
            hasOffset = true;
            p = q + 1;
        }
        else if (
            q < line.Length
            && (line[q] == '+' || line[q] == '-')
            && TwoDigits(line, q + 1, out var offsetHours)
        )
        {
            var r = q + 3;
            if (r < line.Length && line[r] == ':')
                r++;

            if (TwoDigits(line, r, out var offsetMinutes))
            {
                var totalMinutes = offsetHours * 60 + offsetMinutes;
                if (offsetMinutes > 59 || totalMinutes > 14 * 60)
                    return false;

                offsetTicks = (line[q] == '-' ? -1 : 1) * totalMinutes * TimeSpan.TicksPerMinute;
                hasOffset = true;
                p = r + 2;
            }
        }

        if (!hasOffset)
        {
            // A timestamp without an offset is local time, as DateTimeOffset.Parse treats it.
            offsetTicks = TimeZoneInfo
                .Local.GetUtcOffset(new DateTime(localTicks, DateTimeKind.Unspecified))
                .Ticks;
        }

        var utcTicks = localTicks - offsetTicks;
        if (utcTicks < DateTime.MinValue.Ticks || utcTicks > DateTime.MaxValue.Ticks)
            return false;

        while (p < line.Length && IsSpace(line[p]))
            p++;
        if (p < line.Length && line[p] == '[')
            p++;

        var levelStart = p;
        while (p < line.Length && IsAsciiLetter(line[p]))
            p++;

        // The whole run of letters must be a level name. Matching only a prefix would read
        // "[Information]" as INF followed by the message "ormation] ...".
        if (!TryParseLevel(line[levelStart..p], out var level))
            return false;

        if (p < line.Length && line[p] == ']')
            p++;
        while (p < line.Length && IsSpace(line[p]))
            p++;

        header.LocalTicks = localTicks;
        header.OffsetTicks = offsetTicks;
        header.Level = level;
        header.MessageStart = p;
        return true;
    }

    /// <summary>
    /// Serilog level names in every width its <c>{Level}</c> formats produce: three letters
    /// (u3/w3), four letters (u4/w4) and the full name. Case-insensitive.
    /// </summary>
    private static bool TryParseLevel(ReadOnlySpan<byte> token, out CoreLogLevel level)
    {
        level = CoreLogLevel.Information;
        if (token.Length is < 3 or > 11)
            return false;

        Span<byte> upper = stackalloc byte[11];
        for (var i = 0; i < token.Length; i++)
            upper[i] = (byte)(token[i] & 0xDF);
        upper = upper[..token.Length];

        switch (upper.Length)
        {
            case 3:
                if (upper.SequenceEqual("INF"u8)) level = CoreLogLevel.Information;
                else if (upper.SequenceEqual("DBG"u8)) level = CoreLogLevel.Debug;
                else if (upper.SequenceEqual("WRN"u8)) level = CoreLogLevel.Warning;
                else if (upper.SequenceEqual("ERR"u8)) level = CoreLogLevel.Error;
                else if (upper.SequenceEqual("VRB"u8)) level = CoreLogLevel.Verbose;
                else if (upper.SequenceEqual("FTL"u8)) level = CoreLogLevel.Fatal;
                else return false;
                return true;
            case 4:
                if (upper.SequenceEqual("INFO"u8)) level = CoreLogLevel.Information;
                else if (upper.SequenceEqual("DBUG"u8)) level = CoreLogLevel.Debug;
                else if (upper.SequenceEqual("WARN"u8)) level = CoreLogLevel.Warning;
                else if (upper.SequenceEqual("EROR"u8)) level = CoreLogLevel.Error;
                else if (upper.SequenceEqual("VERB"u8)) level = CoreLogLevel.Verbose;
                else if (upper.SequenceEqual("FATL"u8)) level = CoreLogLevel.Fatal;
                else return false;
                return true;
            case 5:
                if (upper.SequenceEqual("DEBUG"u8)) level = CoreLogLevel.Debug;
                else if (upper.SequenceEqual("ERROR"u8)) level = CoreLogLevel.Error;
                else if (upper.SequenceEqual("FATAL"u8)) level = CoreLogLevel.Fatal;
                else return false;
                return true;
            case 7:
                if (upper.SequenceEqual("WARNING"u8)) level = CoreLogLevel.Warning;
                else if (upper.SequenceEqual("VERBOSE"u8)) level = CoreLogLevel.Verbose;
                else return false;
                return true;
            case 11:
                if (!upper.SequenceEqual("INFORMATION"u8))
                    return false;
                level = CoreLogLevel.Information;
                return true;
            default:
                return false;
        }
    }

    private static bool TwoDigits(ReadOnlySpan<byte> line, int index, out int value)
    {
        value = 0;
        if (index + 1 >= line.Length)
            return false;

        int tens = line[index] - '0';
        int ones = line[index + 1] - '0';
        if ((uint)tens > 9 || (uint)ones > 9)
            return false;

        value = tens * 10 + ones;
        return true;
    }

    private static bool IsDigit(byte b) => (uint)(b - '0') <= 9;

    private static bool IsSpace(byte b) => b == ' ' || b == '\t';

    private static bool IsAsciiLetter(byte b) => (uint)((b | 0x20) - 'a') <= 25;
}
