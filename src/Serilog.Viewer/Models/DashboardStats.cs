namespace Serilog.Viewer.Models;

public sealed class DashboardStats
{
    public long TotalLogs { get; init; }
    public long Errors { get; init; }
    public long Warnings { get; init; }
    public long Fatals { get; init; }
    public long Verboses { get; init; }
    public long Debugs { get; init; }
    public long Informations { get; init; }
    public int ActiveFiles { get; init; }
    public long TotalFileSizeBytes { get; init; }
    public IReadOnlyList<TimeSeriesPoint> ErrorsByHour { get; init; } = [];
    public IReadOnlyList<TimeSeriesPoint> LogsByDay { get; init; } = [];
    public IReadOnlyList<LevelDistributionPoint> LogsByLevel { get; init; } = [];
    public IReadOnlyList<SourceDistributionPoint> TopSources { get; init; } = [];
}

public sealed class TimeSeriesPoint
{
    public DateTimeOffset Timestamp { get; init; }
    public long Count { get; init; }
}

public sealed class LevelDistributionPoint
{
    public string Level { get; init; } = string.Empty;
    public long Count { get; init; }
    public string Color { get; init; } = string.Empty;
}

public sealed class SourceDistributionPoint
{
    public string Source { get; init; } = string.Empty;
    public long Count { get; init; }
}
