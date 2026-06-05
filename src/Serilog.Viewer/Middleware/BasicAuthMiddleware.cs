using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Serilog.Viewer.Middleware;

/// <summary>
/// Intercepts requests to the log viewer base path and enforces Basic Authentication
/// when LogViewerOptions.EnableBasicAuth is true.
/// </summary>
internal sealed class BasicAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly LogViewerOptions _options;
    private readonly ILogger<BasicAuthMiddleware> _logger;

    public BasicAuthMiddleware(
        RequestDelegate next,
        IOptions<LogViewerOptions> options,
        ILogger<BasicAuthMiddleware> logger
    )
    {
        _next = next;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (
            !_options.EnableBasicAuth
            || !context.Request.Path.StartsWithSegments(
                _options.BasePath,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            await _next(context);
            return;
        }

        if (IsAuthenticated(context))
        {
            _logger.LogDebug("Basic auth passed for {Path}", context.Request.Path);
            await _next(context);
            return;
        }

        _logger.LogDebug("Basic auth challenge issued for {Path}", context.Request.Path);
        context.Response.Headers.WWWAuthenticate = $"Basic realm=\"{_options.AuthRealm}\"";
        context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
        await context.Response.WriteAsync("Unauthorized");
    }

    private bool IsAuthenticated(HttpContext context)
    {
        var authHeader = context.Request.Headers.Authorization.ToString();
        if (!authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var encoded = authHeader["Basic ".Length..].Trim();
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var colonIndex = decoded.IndexOf(':');
            if (colonIndex < 0)
                return false;

            var username = decoded[..colonIndex];
            var password = decoded[(colonIndex + 1)..];

            var valid =
                string.Equals(username, _options.Username, StringComparison.Ordinal)
                && string.Equals(password, _options.Password, StringComparison.Ordinal);
            if (!valid)
                _logger.LogDebug(
                    "Basic auth failed: invalid credentials for user '{Username}'",
                    username
                );
            return valid;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Basic auth failed: malformed Authorization header");
            return false;
        }
    }
}
