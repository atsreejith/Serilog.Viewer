using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Serilog.Viewer.Interfaces;

namespace Serilog.Viewer.Realtime.Hubs;

/// <summary>
/// SignalR hub for real-time log tailing.
/// Clients subscribe to specific file groups and receive new entries as they arrive.
/// </summary>
public sealed class LogTailHub : Hub
{
    private readonly ILogFileWatcher _watcher;
    private readonly ILogger<LogTailHub> _logger;

    public LogTailHub(ILogFileWatcher watcher, ILogger<LogTailHub> logger)
    {
        _watcher = watcher;
        _logger = logger;
    }

    public async Task SubscribeToFile(string fileName)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"file:{fileName}");
        _logger.LogDebug(
            "Client {ConnectionId} subscribed to file {FileName}",
            Context.ConnectionId,
            fileName
        );
    }

    public async Task UnsubscribeFromFile(string fileName)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"file:{fileName}");
    }

    public async Task SubscribeToAll()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "all-files");
    }

    public async Task UnsubscribeFromAll()
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "all-files");
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogDebug("Log tail client connected: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogDebug("Log tail client disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
