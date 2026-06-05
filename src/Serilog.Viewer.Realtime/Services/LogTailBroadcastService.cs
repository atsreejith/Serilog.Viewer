using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog.Viewer.Interfaces;
using Serilog.Viewer.Models;
using Serilog.Viewer.Realtime.Hubs;

namespace Serilog.Viewer.Realtime.Services;

/// <summary>
/// Background service that bridges ILogFileWatcher events to SignalR clients.
/// </summary>
public sealed class LogTailBroadcastService : BackgroundService
{
    private readonly ILogFileWatcher _watcher;
    private readonly IHubContext<LogTailHub> _hubContext;
    private readonly ILogRepository _repository;
    private readonly ILogger<LogTailBroadcastService> _logger;
    private readonly string _logFolder;

    public LogTailBroadcastService(
        ILogFileWatcher watcher,
        IHubContext<LogTailHub> hubContext,
        ILogRepository repository,
        ILogger<LogTailBroadcastService> logger,
        string logFolder
    )
    {
        _watcher = watcher;
        _hubContext = hubContext;
        _repository = repository;
        _logger = logger;
        _logFolder = logFolder;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _watcher.NewEntryDetected += OnNewEntry;

        _watcher.WatchAll(_logFolder, "*.txt");
        _watcher.WatchAll(_logFolder, "*.log");
        _watcher.WatchAll(_logFolder, "*.json");
        _watcher.WatchAll(_logFolder, "*.clef");

        _logger.LogInformation("LogTailBroadcastService started, watching {LogFolder}", _logFolder);

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _watcher.NewEntryDetected -= OnNewEntry;
        return base.StopAsync(cancellationToken);
    }

    private void OnNewEntry(object? sender, LogEntry entry)
    {
        _ = BroadcastAsync(entry);
    }

    private async Task BroadcastAsync(LogEntry entry)
    {
        try
        {
            _logger.LogTrace(
                "Broadcasting [{Level}] {Message} from {FileName}",
                entry.Level,
                entry.Message,
                entry.FileName
            );

            await _hubContext
                .Clients.Group($"file:{entry.FileName}")
                .SendAsync("NewLogEntry", entry);

            await _hubContext.Clients.Group("all-files").SendAsync("NewLogEntry", entry);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error broadcasting new log entry");
        }
    }
}
