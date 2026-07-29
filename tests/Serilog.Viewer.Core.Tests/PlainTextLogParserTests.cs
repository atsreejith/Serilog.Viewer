using Serilog.Viewer.Infrastructure.Parsing;
using Serilog.Viewer.Models;

namespace Serilog.Viewer.Core.Tests;

public class PlainTextLogParserTests
{
    private readonly PlainTextLogParser _parser = new();

    [Fact]
    public void CanParse_MatchingFormat_ReturnsTrue()
    {
        Assert.True(_parser.CanParse("2024-01-01 12:00:00 [INF] Hello world"));
    }

    [Fact]
    public void CanParse_ClefLine_ReturnsFalse()
    {
        Assert.False(_parser.CanParse("""{"@t":"2024-01-01T12:00:00Z","@l":"INF","@m":"hello"}"""));
    }

    /// <summary>
    /// Serilog's default file outputTemplate is
    /// "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
    /// which renders the UTC offset after a SPACE. The parser previously required the offset to
    /// follow the seconds directly, so every line of the single most common Serilog format failed
    /// to parse and was silently dropped — a file in this format indexed to zero entries.
    /// </summary>
    [Theory]
    [InlineData("2026-01-01 00:00:00.016 +00:00 [INF] Default Serilog template")]
    [InlineData("2026-01-01 00:00:00.016 +05:30 [INF] Positive offset")]
    [InlineData("2026-01-01 00:00:00.016 -08:00 [INF] Negative offset")]
    public void CanParse_DefaultSerilogTemplateWithSpacedOffset_ReturnsTrue(string line)
    {
        Assert.True(_parser.CanParse(line));
    }

    [Fact]
    public void Parse_SpacedOffset_KeepsOffsetInTimestamp()
    {
        var entry = _parser.Parse(
            "2026-01-01 10:00:00.000 +05:30 [INF] Offset preserved",
            "app.log",
            0
        );

        Assert.NotNull(entry);
        Assert.Equal(TimeSpan.FromMinutes(330), entry.Timestamp.Offset);
        Assert.Equal("Offset preserved", entry.Message);
    }

    [Theory]
    [InlineData("2026-01-01 00:00:00.016+00:00 [INF] No space before offset")]
    [InlineData("2026-01-01T00:00:00.016Z [INF] ISO-8601 with Z")]
    [InlineData("2026-01-01 00:00:00.016 [INF] No offset at all")]
    public void CanParse_OtherSupportedTimestampShapes_StillParse(string line)
    {
        Assert.True(_parser.CanParse(line));
    }

    [Fact]
    public void Parse_MapsTimestamp()
    {
        var entry = _parser.Parse("2024-06-15 10:30:00 [WRN] A warning", "app.log", 0);
        Assert.NotNull(entry);
        Assert.Equal(2024, entry.Timestamp.Year);
        Assert.Equal(6, entry.Timestamp.Month);
        Assert.Equal(15, entry.Timestamp.Day);
    }

    [Fact]
    public void Parse_MapsLevelWarning()
    {
        var entry = _parser.Parse("2024-01-01 00:00:00 [WRN] Watch out", "app.log", 0);
        Assert.Equal(LogLevel.Warning, entry!.Level);
    }

    [Fact]
    public void Parse_MapsMessage()
    {
        var entry = _parser.Parse("2024-01-01 00:00:00 [INF] Hello world", "app.log", 0);
        Assert.Equal("Hello world", entry!.Message);
    }

    [Fact]
    public void Parse_UnrecognisedLine_ReturnsNull()
    {
        var entry = _parser.Parse("this is not a log line", "app.log", 0);
        Assert.Null(entry);
    }
}
