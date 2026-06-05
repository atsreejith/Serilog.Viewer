using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Serilog.Viewer;

/// <summary>
/// Builder returned by <see cref="LogViewerExtensions.AddLogViewer"/>.
/// Extension packages (e.g. Serilog.Viewer.Realtime) use this to register
/// their own services and endpoints without coupling to the core package internals.
/// </summary>
public interface ILogViewerBuilder
{
    /// <summary>The application service collection.</summary>
    IServiceCollection Services { get; }

    /// <summary>The resolved options instance shared across the builder chain.</summary>
    LogViewerOptions Options { get; }

    /// <summary>Additional endpoint registrations contributed by extension packages.</summary>
    IReadOnlyList<Action<IEndpointRouteBuilder>> EndpointRegistrations { get; }

    /// <summary>
    /// Registers an additional endpoint mapping action that will be executed
    /// when <c>UseLogViewer</c> or <c>MapLogViewer</c> is called.
    /// </summary>
    void AddEndpointRegistration(Action<IEndpointRouteBuilder> registration);
}
