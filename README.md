# Serilog.Viewer

A lightweight, embeddable log viewer for ASP.NET Core applications that use Serilog structured logging. Serves a modern React-based UI to browse, filter, and search log files directly from your running application.

## Packages

| Package | Description |
|---|---|
| `Serilog.Viewer` | Core middleware and log reading/parsing pipeline |
| `Serilog.Viewer.Realtime` | Optional SignalR extension for live log tailing |

## Getting Started

### 1. Install the package

```shell
dotnet add package Serilog.Viewer
```

### 2. Register services and middleware

```csharp
builder.Services.AddLogViewer(options =>
{
    options.LogFolder = "Logs";
});

app.UseLogViewer();
```

### 3. Open the viewer

Navigate to `/logviewer` in your browser.

## Optional: Real-time tailing

```shell
dotnet add package Serilog.Viewer.Realtime
```

```csharp
builder.Services.AddLogViewerRealtime();
```

## License

MIT © Sreejith
