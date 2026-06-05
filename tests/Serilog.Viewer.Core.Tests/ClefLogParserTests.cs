using Serilog.Viewer.Infrastructure.Parsing;
using Serilog.Viewer.Models;

namespace Serilog.Viewer.Core.Tests;

public class ClefLogParserTests
{
    private readonly ClefLogParser _parser = new();

    [Fact]
    public void CanParse_ValidClefLine_ReturnsTrue()
    {
        var line = """{"@t":"2024-01-01T12:00:00.000Z","@l":"INF","@m":"Hello"}""";
        Assert.True(_parser.CanParse(line));
    }

    [Fact]
    public void CanParse_PlainTextLine_ReturnsFalse()
    {
        Assert.False(_parser.CanParse("2024-01-01 12:00:00 [INF] Hello world"));
    }

    [Fact]
    public void Parse_MapsTimestamp()
    {
        var line = """{"@t":"2024-06-15T10:30:00.000Z","@l":"INF","@m":"Test"}""";
        var entry = _parser.Parse(line, "test.clef", 0);
        Assert.NotNull(entry);
        Assert.Equal(2024, entry.Timestamp.Year);
        Assert.Equal(6, entry.Timestamp.Month);
    }

    [Fact]
    public void Parse_MapsLevelInformation()
    {
        var line = """{"@t":"2024-01-01T00:00:00Z","@l":"INF","@m":"info"}""";
        var entry = _parser.Parse(line, "test.clef", 0);
        Assert.Equal(LogLevel.Information, entry!.Level);
    }

    [Fact]
    public void Parse_MapsLevelError()
    {
        var line = """{"@t":"2024-01-01T00:00:00Z","@l":"ERR","@m":"err"}""";
        var entry = _parser.Parse(line, "test.clef", 0);
        Assert.Equal(LogLevel.Error, entry!.Level);
    }

    [Fact]
    public void Parse_MapsException()
    {
        var line =
            """{"@t":"2024-01-01T00:00:00Z","@l":"ERR","@m":"oops","@x":"System.Exception: bad"}""";
        var entry = _parser.Parse(line, "test.clef", 0);
        Assert.Equal("System.Exception: bad", entry!.Exception);
    }

    [Fact]
    public void Parse_ExtraPropertiesGoToPropertiesDict()
    {
        var line =
            """{"@t":"2024-01-01T00:00:00Z","@l":"INF","@m":"msg","UserId":42,"Tag":"abc"}""";
        var entry = _parser.Parse(line, "test.clef", 0);
        Assert.NotNull(entry!.Properties);
        Assert.True(entry.Properties.ContainsKey("UserId"));
        Assert.True(entry.Properties.ContainsKey("Tag"));
    }

    [Fact]
    public void Parse_InvalidJson_ReturnsNull()
    {
        var entry = _parser.Parse("not-json", "test.clef", 0);
        Assert.Null(entry);
    }

    [Fact]
    public void Parse_EmptyMessage_DoesNotThrow()
    {
        var line = """{"@t":"2024-01-01T00:00:00Z","@l":"DBG","@m":""}""";
        var entry = _parser.Parse(line, "test.clef", 0);
        Assert.NotNull(entry);
        Assert.Equal(string.Empty, entry.Message);
    }
}
