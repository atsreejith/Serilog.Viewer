using Serilog.Viewer.Interfaces;
using Serilog.Viewer.Models;

namespace Serilog.Viewer.Infrastructure.Parsing;

public sealed class LogParserFactory
{
    private readonly IReadOnlyList<ILogParser> _parsers;

    public LogParserFactory(IEnumerable<ILogParser> parsers)
    {
        _parsers = [.. parsers];
    }

    public ILogParser DetectParser(string sampleLine)
    {
        foreach (var parser in _parsers)
        {
            if (parser.CanParse(sampleLine))
                return parser;
        }
        return _parsers.First(p => p.Format == LogFileFormat.PlainText);
    }

    public IReadOnlyList<ILogParser> All => _parsers;
}
