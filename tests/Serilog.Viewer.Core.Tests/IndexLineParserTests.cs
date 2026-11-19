using System.Text;
using Serilog.Viewer.Infrastructure.Parsing;
using Serilog.Viewer.Interfaces;
using Serilog.Viewer.Models;

namespace Serilog.Viewer.Core.Tests;

/// <summary>
/// The index is built by a fast path that reads fields straight from the UTF-8 bytes of a line,
/// while the details view and exports use the full parse. The grid shows the first and the drawer
/// the second, so for every line the two must tell the same story: same decision about whether
/// the line is an entry, same timestamp, level, message and ids.
/// </summary>
public class IndexLineParserTests
{
    private static string Long(char c, int count) => new(c, count);

    /// <summary>A CLEF line with a fixed timestamp followed by <paramref name="body"/>.</summary>
    private static string Clef(string body = "")
    {
        body = body.Trim();
        return "{\"@t\":\"2024-01-01T12:00:00.1234567Z\"" + (body.Length > 0 ? "," + body : "") + "}";
    }

    public static IEnumerable<object[]> ClefLines()
    {
        var lines = new List<string>
        {
            Clef(""" "@mt":"Hello {Name}, you are {Age} years old","Name":"Ada","Age":36,"SourceContext":"App.Greeter" """),
            Clef(""" "@m":"plain message","@l":"Warning" """),
            Clef(""" "@m":"x","@l":"ERR" """),
            Clef(""" "@m":"x","@l":"fatal" """),
            Clef(""" "@m":"x","@l":"Debug" """),
            Clef(""" "@m":"x","@l":"verbose" """),
            Clef(""" "@m":"x","@l":"warn" """),
            Clef(""" "@m":"x","@l":"weird" """),
            Clef(""" "@m":"x","@l":null """),
            Clef(""" "@m":"rendered wins","@mt":"template {A}","A":1 """),
            Clef(""" "@m":5,"@mt":"template used {A}","A":1 """),
            Clef(""" "@mt":"Took {Elapsed:0.000} ms for {@Request} and {$Raw}","Elapsed":12.5,"Request":{"Path":"/a","Query":{"x":1}},"Raw":[1,2,3] """),
            Clef(""" "@mt":"Missing {Nope} here" """),
            Clef(""" "@mt":"Literal {{braces}} and {Value}","Value":true """),
            Clef(""" "@mt":"{A} {B} {C}","A":null,"B":false,"C":"x" """),
            Clef(""" "@mt":"Path {Path} said \"{Quote}\"\nnext","Path":"C:\\logs\\app.log","Quote":"hi \u00e9\ud83d\ude00" """),
            Clef(""" "@mt":"{A}|{B}","A":"tab\there \/ slash \b\f back\\slash","B":"\u0041\u00DF\u6771\uD83D\uDE00" """),
            Clef(""" "@m":"escaped \"message\" with\r\nline breaks and \u00e9" """),
            Clef(""" "@mt":"Grüße {Name} 😀","Name":"Zoë" """),
            Clef(""" "@mt":"{Größe} cm","Größe":5 """),
            Clef(""" "@m":"boom","@l":"Error","@x":"System.Exception: boom\r\n   at A.B()\r\n   at C.D()" """),
            Clef(""" "@m":"ids","sourcecontext":"lower.ctx","REQUESTID":"R1","correlationId":"C1" """),
            Clef(""" "@m":"ids","RequestId":12345,"CorrelationId":true,"SourceContext":{"a":1} """),
            Clef(""" "@m":"ids","RequestId":1.5,"CorrelationId":null,"SourceContext":[1,"two"] """),
            Clef(""" "@m":"ids","RequestId":"first","requestid":"last" """),
            Clef(""" "@mt":"{X}","X":1,"X":2 """),
            Clef(""" "@mt":"{} {:} {@} {a:} {a{b}} {unclosed","a":"A","b":"B","@":"at" """),
            Clef(""" "@mt":"{N} {E} {Neg}","N":12.50,"E":1e5,"Neg":-7 """),
            Clef(""" "@mt":"{A}","\u0041":"escaped-name" """),
            "   " + Clef(""" "@m":"surrounded by white space" """) + "   ",
            Clef(),
            """{"@t":"2024-01-01T12:00:00+05:30","@m":"offset"}""",
            """{"@t":"not a timestamp","@m":"falls back to now"}""",
            """{"@m":"no timestamp at all"}""",
            // Messages and exceptions longer than the index keeps.
            Clef("\"@m\":\"" + Long('m', 2000) + "\""),
            Clef("\"@m\":\"" + Long('é', 700) + "\""),
            Clef("\"@mt\":\"" + Long('a', 400) + " {Value} tail\",\"Value\":\"" + Long('v', 400) + "\""),
            Clef("\"@mt\":\"" + Long('a', 510) + "{Emoji}{Emoji}\",\"Emoji\":\"😀😀\""),
            Clef("\"@mt\":\"" + Long('a', 500) + "{Obj}\",\"Obj\":{\"k\":\"" + Long('z', 100) + "\"}"),
            Clef(
                "\"@mt\":\"" + Long('a', 500) + "{Escaped}\",\"Escaped\":\""
                    + string.Concat(Enumerable.Repeat("\\n", 50)) + "\""
            ),
            Clef(
                "\"@m\":\"x\",\"@x\":\""
                    + string.Concat(Enumerable.Repeat("   at Some.Frame()\\r\\n", 600)) + "\""
            ),
            // A whole payload logged as one escaped string, of which the index keeps a prefix.
            Clef(
                "\"@mt\":\"Raw response for {Id}: {Body}\",\"Id\":7,\"Body\":\""
                    + string.Concat(Enumerable.Repeat("{\\\"name\\\":\\\"caf\\u00e9\\\",\\\"path\\\":\\\"C:\\\\tmp\\\"}\\r\\n", 2000))
                    + "\""
            ),
            // Escaped surrogate pairs landing on, and one character either side of, the cut.
            Clef("\"@mt\":\"" + Long('a', 510) + "{E}\",\"E\":\"\\uD83D\\uDE00\\uD83D\\uDE00\""),
            Clef("\"@mt\":\"" + Long('a', 511) + "{E}\",\"E\":\"\\uD83D\\uDE00\\uD83D\\uDE00\""),
            Clef("\"@mt\":\"" + Long('a', 509) + "{E}\",\"E\":\"x\\uD83D\\uDE00\\uD83D\\uDE00\""),
            Clef("\"@m\":\"" + Long('a', 511) + "\\uD83D\\uDE00 tail\""),
        };

        // More properties than the parser keeps on the stack.
        var many = string.Join(",", Enumerable.Range(0, 80).Select(i => $"\"P{i}\":{i}"));
        lines.Add(Clef("\"@mt\":\"first {P0} last {P79}\"," + many));

        var notEntries = new[]
        {
            "not-json",
            Clef(""" "@m":"x" """) + " trailing",
            Clef(""" "@m":"x" """).TrimEnd('}'),
            "[1,2]",
            "\"string\"",
            """{"@t":123,"@m":"x"}""",
            Clef(""" "@m":"x","@l":5 """),
            Clef(""" "@m":"bad escape \q" """),
        };

        return lines
            .Select(l => new object[] { l, true })
            .Concat(notEntries.Select(l => new object[] { l, false }));
    }

    public static IEnumerable<object[]> PlainLines()
    {
        var lines = new[]
        {
            "2026-01-01 00:00:00.016 +00:00 [INF] Default template",
            "2026-01-01 10:00:00.000 +05:30 [WRN] Positive offset",
            "2026-01-01 00:00:00.1234567 -08:00 [FTL] Seven digits",
            "2026-01-01 00:00:00.123456789 [VRB] Nine digits, local time",
            "2026-01-01T00:00:00.016Z [ERR] ISO-8601 with Z",
            "2026-01-01 00:00:00 [DBG] No fraction, no offset",
            "2026-01-01 00:00:00.016+00:00 [INF] No space before offset",
            "2026-01-01 00:00:00.016 +0000 [INF] Offset without colon",
            "2026-01-01 00:00:00,123 [INF] Comma fraction",
            "2026-01-01 00:00:00.016 +00:00 INF No brackets",
            "2026-01-01 00:00:00.016 +00:00 [Information] Full name",
            "2026-01-01 00:00:00.016 +00:00 [Error] Full name",
            "2026-01-01 00:00:00.016 +00:00 [warning] Lower case",
            "2026-01-01 00:00:00.016 +00:00 [WARN] Four letters",
            "2026-01-01 00:00:00.016 +00:00 [INFO] Four letters",
            "2026-01-01 00:00:00.016 +00:00 [EROR] Four letters",
            "2026-01-01 00:00:00.016 +00:00 [INF] Order {OrderId=12} for {Customer=acme} {RequestId=abc} [Shop.Orders.OrderService]",
            "2026-01-01 00:00:00.016 +00:00 [INF] {sourcecontext=lower.case} {CORRELATIONID=X}",
            "2026-01-01 00:00:00.016 +00:00 [INF]    padded message   ",
            "2026-01-01 00:00:00.016 +00:00 [INF] ünïcödé message 😀 [Ünï.Ctx]",
            "2026-01-01 00:00:00.016 +00:00 [DBG] Executed DbCommand [Parameters=[@p0='1'], CommandType='Text']",
            "2026-01-01 00:00:00.016 +00:00 [INF] " + Long('x', 2000) + " [Long.Message.Source]",
            "2026-01-01 00:00:00.016 +00:00 [INF] " + Long('é', 700),
            "2026-01-01 00:00:00.016 +00:00 [INF]",
        };

        var notEntries = new[]
        {
            "this is not a log line",
            "   at Shop.Orders.OrderService.Process(Int32 orderId) in C:\\src\\OrderService.cs:line 87",
            "System.InvalidOperationException: Order 12 is in an invalid state",
            "2026-13-01 00:00:00 [INF] No such month",
            "2026-02-30 00:00:00 [INF] No such day",
            "2026-01-01 00:00:00 [INFRA] Not a level",
            "2026-01-01 00:00:00 [XYZ] Not a level",
            "2026-01-01 00:00:00.016 +99:00 [INF] Impossible offset",
            "",
        };

        return lines
            .Select(l => new object[] { l, true })
            .Concat(notEntries.Select(l => new object[] { l, false }));
    }

    [Theory]
    [MemberData(nameof(ClefLines))]
    public void Clef_IndexFieldsAgreeWithFullParse(string line, bool isEntry) =>
        AssertAgrees(new ClefLogParser(), line, isEntry, compareTimestamp: line.Contains("\"@t\":\"20"));

    [Theory]
    [MemberData(nameof(PlainLines))]
    public void PlainText_IndexFieldsAgreeWithFullParse(string line, bool isEntry) =>
        AssertAgrees(new PlainTextLogParser(), line, isEntry, compareTimestamp: true);

    private static void AssertAgrees(
        ILogParser parser,
        string line,
        bool isEntry,
        bool compareTimestamp
    )
    {
        var full = parser.Parse(line, "file.log", 0);

        var fields = default(IndexLineFields);
        var parsed = ((IIndexLineParser)parser).TryParseIndexLine(
            Encoding.UTF8.GetBytes(line),
            ref fields
        );

        // Both parsers must reach the verdict the test data expects, not merely the same one:
        // two parsers that both reject a line they should accept would otherwise agree.
        Assert.Equal(isEntry, full is not null);
        Assert.Equal(isEntry, parsed);
        if (full is null)
            return;

        if (compareTimestamp)
            Assert.Equal(full.Timestamp.UtcTicks, fields.TimestampUtcTicks);

        Assert.Equal(full.Level, fields.Level);
        Assert.Equal(IndexLimits.Truncate(full.Message, IndexLimits.MaxMessageChars), fields.Message);
        Assert.Equal(
            full.Exception is null
                ? null
                : IndexLimits.Truncate(full.Exception, IndexLimits.MaxExceptionChars),
            fields.Exception
        );
        Assert.Equal(full.SourceContext, fields.SourceContext);
        Assert.Equal(full.CorrelationId, fields.CorrelationId);
        Assert.Equal(full.RequestId, fields.RequestId);
    }

    [Fact]
    public void Clef_RendersTemplateTheSameWayAsTheFullParse()
    {
        var parser = new ClefLogParser();
        var line =
            """{"@t":"2024-01-01T00:00:00Z","@mt":"User {User} did {Action} in {Elapsed:0.0} ms","User":"ada","Action":"login","Elapsed":3.25}""";

        var fields = default(IndexLineFields);
        Assert.True(((IIndexLineParser)parser).TryParseIndexLine(Encoding.UTF8.GetBytes(line), ref fields));
        Assert.Equal("User ada did login in 3.25 ms", fields.Message);
    }

    [Theory]
    [InlineData("[Information]", LogLevel.Information)]
    [InlineData("[Error]", LogLevel.Error)]
    [InlineData("[Warning]", LogLevel.Warning)]
    [InlineData("[Verbose]", LogLevel.Verbose)]
    [InlineData("[Debug]", LogLevel.Debug)]
    [InlineData("[Fatal]", LogLevel.Fatal)]
    [InlineData("[INFO]", LogLevel.Information)]
    [InlineData("[WARN]", LogLevel.Warning)]
    [InlineData("[EROR]", LogLevel.Error)]
    [InlineData("[err]", LogLevel.Error)]
    public void PlainText_FullAndFourLetterLevelNames_DoNotLeakIntoTheMessage(
        string level,
        LogLevel expected
    )
    {
        // The level used to be matched by prefix, so "[Information] Hello" parsed as level INF
        // with the message "ormation] Hello".
        var entry = new PlainTextLogParser().Parse(
            $"2026-01-01 00:00:00.016 +00:00 {level} Hello world",
            "app.log",
            0
        );

        Assert.NotNull(entry);
        Assert.Equal(expected, entry.Level);
        Assert.Equal("Hello world", entry.Message);
    }

    [Fact]
    public void PlainText_ContinuationLinesBecomeTheException()
    {
        IMultiLineLogParser parser = new PlainTextLogParser();
        const string trace = "System.InvalidOperationException: bad state\n   at Shop.Orders.Process()";

        Assert.True(parser.IsEntryStart("2026-01-01 00:00:00.016 +00:00 [ERR] Order failed"));
        Assert.False(parser.IsEntryStart("   at Shop.Orders.Process()"));
        Assert.False(parser.IsEntryStart("System.InvalidOperationException: bad state"));

        var entry = parser.Parse(
            "2026-01-01 00:00:00.016 +00:00 [ERR] Order failed",
            trace,
            "app.log",
            42
        );

        Assert.NotNull(entry);
        Assert.Equal("Order failed", entry.Message);
        Assert.Equal(trace, entry.Exception);
        Assert.Contains("Order failed", entry.RawJson);
        Assert.Contains("at Shop.Orders.Process()", entry.RawJson);
    }
}
