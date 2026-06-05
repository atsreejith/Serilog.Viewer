using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Viewer.Interfaces;
using Serilog.Viewer.Realtime.Hubs;
using Serilog.Viewer.Realtime.Services;

namespace Serilog.Viewer.Realtime;

public static class RealtimeExtensions
{
    /// <summary>
    /// Adds real-time log tailing via SignalR to Serilog Viewer.
    /// <para>
    /// Usage: <c>services.AddLogViewer(...).AddLogViewerRealtime();</c>
    /// </para>
    /// Users who do not call this method get zero SignalR overhead — no hub,
    /// no background service, no JS SignalR bundle.
    /// </summary>
    public static ILogViewerBuilder AddLogViewerRealtime(this ILogViewerBuilder builder)
    {
        builder.Options.LiveTailEnabled = true;

        builder.Services.AddSignalR();

        var logFolder = builder.Options.LogFolder;
        builder.Services.AddSingleton<LogTailBroadcastService>(sp => new LogTailBroadcastService(
            sp.GetRequiredService<ILogFileWatcher>(),
            sp.GetRequiredService<IHubContext<LogTailHub>>(),
            sp.GetRequiredService<ILogRepository>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LogTailBroadcastService>>(),
            logFolder
        ));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<LogTailBroadcastService>());

        var basePath = builder.Options.BasePath.TrimEnd('/');
        builder.AddEndpointRegistration(endpoints =>
            endpoints.MapHub<LogTailHub>($"{basePath}/hubs/logtail")
        );

        return builder;
    }
}
