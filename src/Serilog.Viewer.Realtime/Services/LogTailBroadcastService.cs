using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog.Viewer.Interfaces;
using Serilog.Viewer.Models;
using Serilog.Viewer.Realtime.Hubs;

namespace Serilog.Viewer.Realtime.Services;

/// <summary>
/// Background service that bridges ILogFileWatcher events to SignalR clients. The watcher is only
/// active while at least one client is subscribed, so an idle viewer costs the host nothing.
/// </summary>
public sealed class LogTailBroadcastService : BackgroundService
{
    private readonly ILogFileWatcher _watcher;
    private readonly IHubContext<LogTailHub> _hubContext;
    private readonly ILogRepository _repository;
    private readonly LiveTailSubscriptions _subscriptions;
    private readonly ILogger<LogTailBroadcastService> _logger;
    private readonly string _logFolder;
    private readonly object _watchLock = new();
    private bool _watching;

    public LogTailBroadcastService(
        ILogFileWatcher watcher,
        IHubContext<LogTailHub> hubContext,
        ILogRepository repository,
        LiveTailSubscriptions subscriptions,
        ILogger<LogTailBroadcastService> logger,
        string logFolder
    )
    {
        _watcher = watcher;
        _hubContext = hubContext;
        _repository = repository;
        _subscriptions = subscriptions;
        _logger = logger;
        _logFolder = logFolder;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _watcher.NewEntryDetected += OnNewEntry;
        _subscriptions.SubscriberCountChanged += OnSubscriberCountChanged;
        OnSubscriberCountChanged(_subscriptions.Count);

        _logger.LogInformation(
            "LogTailBroadcastService started; {LogFolder} is watched while live-tail clients are connected",
            _logFolder
        );

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) { }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _watcher.NewEntryDetected -= OnNewEntry;
        _subscriptions.SubscriberCountChanged -= OnSubscriberCountChanged;
        lock (_watchLock)
        {
            if (_watching)
            {
                _watcher.UnwatchAll();
                _watching = false;
            }
        }
        return base.StopAsync(cancellationToken);
    }

    private void OnSubscriberCountChanged(int subscribers)
    {
        lock (_watchLock)
        {
            if (subscribers > 0 && !_watching)
            {
                _watcher.WatchAll(_logFolder, "*.txt");
                _watcher.WatchAll(_logFolder, "*.log");
                _watcher.WatchAll(_logFolder, "*.json");
                _watcher.WatchAll(_logFolder, "*.clef");
                _watching = true;
                _logger.LogDebug(
                    "Live tail: first subscriber connected, watching {LogFolder}",
                    _logFolder
                );
            }
            else if (subscribers == 0 && _watching)
            {
                _watcher.UnwatchAll();
                _watching = false;
                _logger.LogDebug(
                    "Live tail: last subscriber left, stopped watching {LogFolder}",
                    _logFolder
                );
            }
        }
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
