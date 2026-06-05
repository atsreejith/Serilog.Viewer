using Serilog.Viewer.Models;

namespace Serilog.Viewer.Interfaces;

public interface ILogFileWatcher
{
    event EventHandler<LogEntry>? NewEntryDetected;
    void Watch(string filePath);
    void Unwatch(string filePath);
    void WatchAll(string folderPath, string pattern = "*.txt");
}
