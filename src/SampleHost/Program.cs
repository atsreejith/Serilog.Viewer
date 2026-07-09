using Serilog;
using Serilog.Formatting.Compact;
using Serilog.Viewer;

var logFolder = Path.Combine(AppContext.BaseDirectory, "Logs");
Directory.CreateDirectory(logFolder);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .WriteTo.File(
        new CompactJsonFormatter(),
        Path.Combine(logFolder, "log-.clef"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 31
    )
    .WriteTo.File(
        new CompactJsonFormatter(),
        Path.Combine(logFolder, "log-.txt"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 31
    )
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

builder.Services.AddLogViewer(options =>
{
    options.LogFolder = logFolder;
    options.EnableBasicAuth = true;
    options.Username = "admin";
    options.Password = "admin";
    options.AuthRealm = "Serilog Viewer Sample";
    options.EnableFileDelete = true;
});

// Uncomment to enable real-time log tailing (requires Serilog.Viewer.Realtime):
// .AddLogViewerRealtime();

var app = builder.Build();

app.UseLogViewer();

app.MapGet("/", () => Results.Redirect("/logviewer"));
app.MapGet(
    "/generate",
    (ILogger<Program> log) =>
    {
        log.LogInformation("Manually triggered log generation");
        for (int i = 0; i < 5; i++)
        {
            log.LogInformation("Sample info log entry {Index}", i);
            log.LogWarning("Sample warning entry {Index}", i);
        }
        log.LogError(new Exception("Synthetic error"), "Sample error log entry");
        return Results.Ok("Generated 11 log entries");
    }
);

app.Run();
