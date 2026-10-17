using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Serilog.Viewer.Interfaces;
using Serilog.Viewer.Realtime.Services;

namespace Serilog.Viewer.Realtime.Hubs;

/// <summary>
/// SignalR hub for real-time log tailing.
/// Clients subscribe to specific file groups and receive new entries as they arrive.
/// </summary>
public sealed class LogTailHub : Hub
{
    private readonly ILogFileWatcher _watcher;
    private readonly LiveTailSubscriptions _subscriptions;
    private readonly ILogger<LogTailHub> _logger;

    public LogTailHub(
        ILogFileWatcher watcher,
        LiveTailSubscriptions subscriptions,
        ILogger<LogTailHub> logger
    )
    {
        _watcher = watcher;
        _subscriptions = subscriptions;
        _logger = logger;
    }

    public async Task SubscribeToFile(string fileName)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"file:{fileName}");
        _subscriptions.Subscribed(Context.ConnectionId);
        _logger.LogDebug(
            "Client {ConnectionId} subscribed to file {FileName}",
            Context.ConnectionId,
            fileName
        );
    }

    public async Task UnsubscribeFromFile(string fileName)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"file:{fileName}");
        _subscriptions.Unsubscribed(Context.ConnectionId);
    }

    public async Task SubscribeToAll()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "all-files");
        _subscriptions.Subscribed(Context.ConnectionId);
    }

    public async Task UnsubscribeFromAll()
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "all-files");
        _subscriptions.Unsubscribed(Context.ConnectionId);
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogDebug("Log tail client connected: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogDebug("Log tail client disconnected: {ConnectionId}", Context.ConnectionId);
        _subscriptions.Disconnected(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
