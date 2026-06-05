using Microsoft.Extensions.Logging;
using Serilog.Viewer.Infrastructure.Reading;
using Serilog.Viewer.Interfaces;
using Serilog.Viewer.Models;

namespace Serilog.Viewer.Infrastructure.Watching;

/// <summary>
/// Watches log files for new entries using FileSystemWatcher.
/// </summary>
public sealed class LogFileWatcher : ILogFileWatcher, IDisposable
{
    private readonly StreamingLogFileReader _reader;
    private readonly ILogger<LogFileWatcher> _logger;
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new();
    private readonly Dictionary<string, long> _fileSizes = new();
    private readonly object _lock = new();

    public event EventHandler<LogEntry>? NewEntryDetected;

    public LogFileWatcher(StreamingLogFileReader reader, ILogger<LogFileWatcher> logger)
    {
        _reader = reader;
        _logger = logger;
    }

    public void Watch(string filePath)
    {
        lock (_lock)
        {
            if (_watchers.ContainsKey(filePath))
                return;

            var directory = Path.GetDirectoryName(filePath)!;
            var fileName = Path.GetFileName(filePath);

            _fileSizes[filePath] = StreamingLogFileReader.GetFileSize(filePath);

            var watcher = new FileSystemWatcher(directory, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };

            watcher.Changed += OnFileChanged;
            _watchers[filePath] = watcher;
            _logger.LogDebug("Watching file: {FilePath}", filePath);
        }
    }

    public void Unwatch(string filePath)
    {
        lock (_lock)
        {
            if (_watchers.TryGetValue(filePath, out var watcher))
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
                _watchers.Remove(filePath);
                _fileSizes.Remove(filePath);
                _logger.LogDebug("Stopped watching file: {FilePath}", filePath);
            }
        }
    }

    public void WatchAll(string folderPath, string pattern = "*.txt")
    {
        if (!Directory.Exists(folderPath))
            return;

        foreach (
            var file in Directory.EnumerateFiles(folderPath, pattern, SearchOption.AllDirectories)
        )
            Watch(file);

        lock (_lock)
        {
            if (!_watchers.ContainsKey(folderPath))
            {
                var folderWatcher = new FileSystemWatcher(folderPath, pattern)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true,
                };
                folderWatcher.Created += (_, e) => Watch(e.FullPath);
                _watchers[folderPath] = folderWatcher;
            }
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                long lastOffset;
                lock (_lock)
                    _fileSizes.TryGetValue(e.FullPath, out lastOffset);

                var currentSize = StreamingLogFileReader.GetFileSize(e.FullPath);
                if (currentSize <= lastOffset)
                    return;

                await foreach (var entry in _reader.ReadFromOffsetAsync(e.FullPath, lastOffset))
                {
                    _logger.LogTrace(
                        "New log entry detected in {File}: [{Level}] {Message}",
                        e.FullPath,
                        entry.Level,
                        entry.Message
                    );
                    NewEntryDetected?.Invoke(this, entry);
                }

                lock (_lock)
                    _fileSizes[e.FullPath] = currentSize;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error reading new log entries from {File}", e.FullPath);
            }
        });
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var watcher in _watchers.Values)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            _watchers.Clear();
        }
    }
}
