using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog.Viewer.Infrastructure;
using Serilog.Viewer.Interfaces;
using Serilog.Viewer.Middleware;

namespace Serilog.Viewer;

public static class LogViewerExtensions
{
    /// <summary>
    /// Registers Serilog Viewer services (infrastructure, API).
    /// Returns an <see cref="ILogViewerBuilder"/> that extension packages can extend,
    /// e.g. <c>.AddLogViewerRealtime()</c> from Serilog.Viewer.Realtime.
    /// </summary>
    public static ILogViewerBuilder AddLogViewer(
        this IServiceCollection services,
        Action<LogViewerOptions>? configure = null
    )
    {
        var options = new LogViewerOptions();
        configure?.Invoke(options);

        services.Configure<LogViewerOptions>(o =>
        {
            o.LogFolder = options.LogFolder;
            o.BasePath = options.BasePath;
            o.EnableBasicAuth = options.EnableBasicAuth;
            o.Username = options.Username;
            o.Password = options.Password;
            o.AuthRealm = options.AuthRealm;
        });

        services.AddLogViewerInfrastructure(options.LogFolder);

        var builder = new LogViewerBuilder(services, options);
        services.AddSingleton<ILogViewerBuilder>(builder);

        return builder;
    }

    /// <summary>
    /// Adds the Serilog Viewer middleware (Basic Auth) and maps all UI/API/hub endpoints.
    /// Call this after app.UseRouting() or on a WebApplication instance directly.
    /// </summary>
    public static IApplicationBuilder UseLogViewer(this IApplicationBuilder app)
    {
        app.UseMiddleware<BasicAuthMiddleware>();

        if (app is IEndpointRouteBuilder erb)
        {
            var builder = app.ApplicationServices.GetRequiredService<ILogViewerBuilder>();
            var logger = app
                .ApplicationServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Serilog.Viewer");
            LogViewerRouteRegistrar.Map(erb, builder, logger);
        }

        return app;
    }

    /// <summary>
    /// Maps Serilog Viewer endpoints on a WebApplication or IEndpointRouteBuilder.
    /// Use this when you need to control middleware ordering explicitly.
    /// </summary>
    public static IEndpointConventionBuilder MapLogViewer(this IEndpointRouteBuilder endpoints)
    {
        var builder = endpoints.ServiceProvider.GetRequiredService<ILogViewerBuilder>();
        var logger = endpoints
            .ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Serilog.Viewer");
        LogViewerRouteRegistrar.Map(endpoints, builder, logger);

        return endpoints.MapGet("/_logviewer_internal_noop", () => Results.NotFound());
    }
}
