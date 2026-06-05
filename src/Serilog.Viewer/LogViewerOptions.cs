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
}
