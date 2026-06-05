using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Serilog.Viewer;

internal sealed class LogViewerBuilder : ILogViewerBuilder
{
    private readonly List<Action<IEndpointRouteBuilder>> _registrations = new();

    public IServiceCollection Services { get; }
    public LogViewerOptions Options { get; }
    public IReadOnlyList<Action<IEndpointRouteBuilder>> EndpointRegistrations => _registrations;

    public LogViewerBuilder(IServiceCollection services, LogViewerOptions options)
    {
        Services = services;
        Options = options;
    }

    public void AddEndpointRegistration(Action<IEndpointRouteBuilder> registration) =>
        _registrations.Add(registration);
}
