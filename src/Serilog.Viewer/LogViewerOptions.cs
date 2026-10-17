namespace Serilog.Viewer;

public sealed class LogViewerOptions
{
    /// <summary>Folder path containing .txt / .log / .json / .clef Serilog log files.</summary>
    public string LogFolder { get; set; } = "Logs";

    /// <summary>URL base path for the log viewer UI and API.</summary>
    public string BasePath { get; set; } = "/logviewer";

    /// <summary>Enable Basic Authentication for the log viewer dashboard.</summary>
    public bool EnableBasicAuth { get; set; } = false;

    /// <summary>Basic Auth username (required when EnableBasicAuth is true).</summary>
    public string? Username { get; set; }

    /// <summary>Basic Auth password (required when EnableBasicAuth is true).</summary>
    public string? Password { get; set; }

    /// <summary>Realm displayed in the Basic Auth challenge.</summary>
    public string AuthRealm { get; set; } = "Log Viewer";

    /// <summary>
    /// Whether real-time log tailing is enabled.
    /// Set automatically by Serilog.Viewer.Realtime when AddLogViewerRealtime() is called.
    /// </summary>
    public bool LiveTailEnabled { get; set; } = false;

    /// <summary>Allow log files to be downloaded from the viewer UI and API.</summary>
    public bool EnableFileDownload { get; set; } = true;

    /// <summary>
    /// Allow log files to be deleted from the viewer UI and API.
    /// Disabled by default because this is a destructive operation.
    /// </summary>
    public bool EnableFileDelete { get; set; } = false;

    /// <summary>
    /// Folder where the SQLite search index is stored. Defaults to a <c>.serilog-viewer</c>
    /// folder inside <see cref="LogFolder"/>. Point it elsewhere (e.g. local app data) if
    /// backup or log-shipping agents watch the log folder — the index is roughly 20% of the
    /// size of the logs it covers.
    /// </summary>
    public string? IndexFolder { get; set; }

    /// <summary>
    /// When true (default), large files are made queryable from their newest entries first and
    /// the rest is indexed in the background, using about one CPU core for a few seconds per
    /// 100 MB. Set to false on hosts where background CPU must be avoided; the first query over a
    /// large file then indexes it fully before returning.
    /// </summary>
    public bool BackgroundIndexing { get; set; } = true;
}
