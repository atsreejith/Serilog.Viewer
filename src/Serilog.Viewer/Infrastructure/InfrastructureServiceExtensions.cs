using Microsoft.Extensions.DependencyInjection;
using Serilog.Viewer.Infrastructure.Indexing;
using Serilog.Viewer.Infrastructure.Parsing;
using Serilog.Viewer.Infrastructure.Reading;
using Serilog.Viewer.Infrastructure.Repository;
using Serilog.Viewer.Infrastructure.Watching;
using Serilog.Viewer.Interfaces;

namespace Serilog.Viewer.Infrastructure;

internal static class InfrastructureServiceExtensions
{
    public static IServiceCollection AddLogViewerInfrastructure(
        this IServiceCollection services,
        string logFolder
    )
    {
        services.AddSingleton<ILogParser, ClefLogParser>();
        services.AddSingleton<ILogParser, PlainTextLogParser>();
        services.AddSingleton<LogParserFactory>();
        services.AddSingleton<StreamingLogFileReader>();
        services.AddSingleton(sp => new LogFileIndexStore(
            logFolder,
            sp.GetRequiredService<StreamingLogFileReader>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LogFileIndexStore>>()
        ));
        services.AddSingleton<ILogRepository>(sp =>
        {
            var reader = sp.GetRequiredService<StreamingLogFileReader>();
            var indexStore = sp.GetRequiredService<LogFileIndexStore>();
            var logger =
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FileSystemLogRepository>>();
            return new FileSystemLogRepository(logFolder, reader, indexStore, logger);
        });
        services.AddSingleton<ILogFileWatcher, LogFileWatcher>();
        return services;
    }
}
