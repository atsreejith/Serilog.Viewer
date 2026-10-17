using Microsoft.Extensions.Logging;
using Serilog.Viewer.Infrastructure.Reading;
using Serilog.Viewer.Interfaces;
using Serilog.Viewer.Models;

namespace Serilog.Viewer.Infrastructure.Watching;

/// <summary>
/// Watches log files for new entries using FileSystemWatcher.
/// <para>
/// A chatty logger raises many change notifications per second for the same file. Each file is
/// therefore read by at most one pass at a time: further notifications during a pass only mark
/// the file dirty, and the pass loops until it is clean. Passes also start after a short delay so
/// a burst of writes is read once. Without this, every notification spawned its own read from
/// the same offset, which both multiplied the work and broadcast duplicate entries.
/// </para>
/// </summary>
public sealed class LogFileWatcher : ILogFileWatcher, IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(150);

    private readonly StreamingLogFileReader _reader;
    private readonly ILogger<LogFileWatcher> _logger;
    private readonly Dictionary<string, FileSystemWatcher> _watchers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TrackedFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    private sealed class TrackedFile
    {
        public long Offset;

        /// <summary>1 while a read pass is scheduled or running for this file.</summary>
        public int Scheduled;

        /// <summary>1 when a change arrived that no pass has consumed yet.</summary>
        public int Dirty;
    }

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

            Track(filePath, StreamingLogFileReader.GetFileSize(filePath));

            var watcher = new FileSystemWatcher(directory, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };

            watcher.Changed += (_, e) => OnFileChanged(e.FullPath);
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
                _files.Remove(filePath);
                _logger.LogDebug("Stopped watching file: {FilePath}", filePath);
            }
        }
    }

    /// <summary>
    /// Watches every file matching <paramref name="pattern"/> under <paramref name="folderPath"/>
    /// with a single FileSystemWatcher per pattern, rather than one per file. Existing files are
    /// tracked from their current end; files created later are tracked from the start.
    /// </summary>
    public void WatchAll(string folderPath, string pattern = "*.txt")
    {
        if (!Directory.Exists(folderPath))
            return;

        lock (_lock)
        {
            var key = Path.Combine(folderPath, pattern);
            if (_watchers.ContainsKey(key))
                return;

            foreach (
                var file in Directory.EnumerateFiles(
                    folderPath,
                    pattern,
                    SearchOption.AllDirectories
                )
            )
                Track(file, StreamingLogFileReader.GetFileSize(file));

            var folderWatcher = new FileSystemWatcher(folderPath, pattern)
            {
                NotifyFilter =
                    NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
            };
            folderWatcher.Changed += (_, e) => OnFileChanged(e.FullPath);
            folderWatcher.Created += (_, e) =>
            {
                lock (_lock)
                    Track(e.FullPath, 0);
                OnFileChanged(e.FullPath);
            };
            folderWatcher.Renamed += (_, e) => OnFileChanged(e.FullPath);
            _watchers[key] = folderWatcher;
            _logger.LogDebug("Watching folder {Folder} for {Pattern}", folderPath, pattern);
        }
    }

    public void UnwatchAll()
    {
        lock (_lock)
        {
            foreach (var watcher in _watchers.Values)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            _watchers.Clear();
            _files.Clear();
        }
        _logger.LogDebug("Stopped watching all files");
    }

    /// <summary>Must be called under <see cref="_lock"/>.</summary>
    private void Track(string filePath, long offset)
    {
        if (!_files.ContainsKey(filePath))
            _files[filePath] = new TrackedFile { Offset = offset };
    }

    private void OnFileChanged(string filePath)
    {
        TrackedFile? tracked;
        lock (_lock)
        {
            if (!_files.TryGetValue(filePath, out tracked))
            {
                // A file we did not see at WatchAll time, or a sub-folder. Start tracking from
                // its current end: history is served by the viewer's own index, live tail only
                // needs what is written from now on.
                if (!File.Exists(filePath))
                    return;
                Track(filePath, StreamingLogFileReader.GetFileSize(filePath));
                return;
            }
        }

        Volatile.Write(ref tracked.Dirty, 1);
        if (Interlocked.Exchange(ref tracked.Scheduled, 1) == 1)
            return; // a pass is already pending and will observe Dirty

        _ = Task.Run(() => DrainAsync(filePath, tracked));
    }

    private async Task DrainAsync(string filePath, TrackedFile tracked)
    {
        try
        {
            await Task.Delay(DebounceDelay);

            while (Interlocked.Exchange(ref tracked.Dirty, 0) == 1)
            {
                var currentSize = StreamingLogFileReader.GetFileSize(filePath);
                if (currentSize < tracked.Offset)
                    tracked.Offset = 0; // truncated or replaced
                if (currentSize == tracked.Offset)
                    continue;

                await foreach (
                    var entry in _reader.ReadFromOffsetAsync(filePath, tracked.Offset)
                )
                {
                    // Only report what existed when the size was sampled; anything appended
                    // since is picked up by the next pass from exactly that offset, so nothing
                    // is broadcast twice.
                    if (entry.LineOffset >= currentSize)
                        break;

                    _logger.LogTrace(
                        "New log entry detected in {File}: [{Level}] {Message}",
                        filePath,
                        entry.Level,
                        entry.Message
                    );
                    NewEntryDetected?.Invoke(this, entry);
                }

                tracked.Offset = currentSize;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error reading new log entries from {File}", filePath);
        }
        finally
        {
            Volatile.Write(ref tracked.Scheduled, 0);

            // A change that arrived between the loop's last check and the reset above saw
            // Scheduled == 1 and expects this pass to handle it; re-arm instead of losing it.
            if (Volatile.Read(ref tracked.Dirty) == 1)
                OnFileChanged(filePath);
        }
    }

    public void Dispose() => UnwatchAll();
}
