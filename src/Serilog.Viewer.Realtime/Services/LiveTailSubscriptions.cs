namespace Serilog.Viewer.Realtime.Services;

/// <summary>
/// Counts SignalR connections that currently hold at least one live-tail subscription, so the
/// file watcher only runs while somebody is actually looking. Without this the host process read
/// and serialized every appended log line for the lifetime of the application, whether or not
/// the viewer was open.
/// </summary>
public sealed class LiveTailSubscriptions
{
    private readonly Dictionary<string, int> _groupsPerConnection = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>Raised, outside the lock, whenever the number of subscribed connections changes.</summary>
    public event Action<int>? SubscriberCountChanged;

    public int Count
    {
        get
        {
            lock (_lock)
                return _groupsPerConnection.Count;
        }
    }

    public void Subscribed(string connectionId)
    {
        int count;
        var changed = false;
        lock (_lock)
        {
            var groups = _groupsPerConnection.GetValueOrDefault(connectionId) + 1;
            _groupsPerConnection[connectionId] = groups;
            changed = groups == 1;
            count = _groupsPerConnection.Count;
        }
        if (changed)
            SubscriberCountChanged?.Invoke(count);
    }

    public void Unsubscribed(string connectionId)
    {
        int count;
        var changed = false;
        lock (_lock)
        {
            if (_groupsPerConnection.TryGetValue(connectionId, out var groups))
            {
                if (groups <= 1)
                {
                    _groupsPerConnection.Remove(connectionId);
                    changed = true;
                }
                else
                {
                    _groupsPerConnection[connectionId] = groups - 1;
                }
            }
            count = _groupsPerConnection.Count;
        }
        if (changed)
            SubscriberCountChanged?.Invoke(count);
    }

    public void Disconnected(string connectionId)
    {
        int count;
        bool changed;
        lock (_lock)
        {
            changed = _groupsPerConnection.Remove(connectionId);
            count = _groupsPerConnection.Count;
        }
        if (changed)
            SubscriberCountChanged?.Invoke(count);
    }
}
