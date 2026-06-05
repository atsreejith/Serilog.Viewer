using Serilog.Viewer.Models;

namespace Serilog.Viewer.Interfaces;

public interface ILogParser
{
    bool CanParse(string line);
    LogEntry? Parse(string line, string fileName, long lineOffset);
    LogFileFormat Format { get; }
}
