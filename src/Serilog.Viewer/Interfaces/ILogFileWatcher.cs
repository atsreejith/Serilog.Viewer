using Serilog.Viewer.Models;

namespace Serilog.Viewer.Interfaces;

public interface ILogFileWatcher
{
    event EventHandler<LogEntry>? NewEntryDetected;
    void Watch(string filePath);
    void Unwatch(string filePath);
    void WatchAll(string folderPath, string pattern = "*.txt");

    /// <summary>Stops every watcher started by <see cref="Watch"/> or <see cref="WatchAll"/>.</summary>
    void UnwatchAll();
}
