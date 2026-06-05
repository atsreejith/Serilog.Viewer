namespace Serilog.Viewer.Models;

public sealed class LogFile
{
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public DateTimeOffset LastModified { get; init; }
    public DateTimeOffset? OldestEntry { get; init; }
    public DateTimeOffset? NewestEntry { get; init; }
    public long LineCount { get; init; }
    public LogFileFormat Format { get; init; }
    public bool IsActive { get; init; }
}

public enum LogFileFormat
{
    Clef,
    PlainText,
}
