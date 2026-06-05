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
