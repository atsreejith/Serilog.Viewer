using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Serilog.Viewer.Handlers;

namespace Serilog.Viewer.Middleware;

/// <summary>
/// Registers log viewer API routes and serves the embedded React SPA.
/// Called from UseLogViewer() / MapLogViewer() after Basic Auth middleware.
/// </summary>
internal static class LogViewerRouteRegistrar
{
    private static readonly Assembly _assembly = typeof(LogViewerRouteRegistrar).Assembly;
    private static readonly string _resourcePrefix = "Serilog.Viewer.wwwroot.";

    public static void Map(
        IEndpointRouteBuilder endpoints,
        ILogViewerBuilder builder,
        ILogger logger
    )
    {
        var options = builder.Options;
        var basePath = options.BasePath.TrimEnd('/');

        logger.LogInformation(
            "Serilog.Viewer mapped at {BasePath} (BasicAuth={BasicAuth}, LogFolder={LogFolder}, LiveTail={LiveTail})",
            basePath,
            options.EnableBasicAuth,
            options.LogFolder,
            options.LiveTailEnabled
        );

        // API routes
        var api = endpoints.MapGroup($"{basePath}/api");

        api.MapGet("files", FilesHandler.GetFiles);
        api.MapGet("files/{fileName}/download", FilesHandler.DownloadFile);
        api.MapGet("files/download/{**fileName}", FilesHandler.DownloadFile);
        api.MapDelete("files/{**fileName}", FilesHandler.DeleteFile);
        api.MapGet("logs", LogsHandler.GetLogs);
        api.MapGet("logs/stats", LogsHandler.GetStats);
        api.MapGet("search", LogsHandler.Search);
        api.MapGet("details", LogsHandler.GetDetails);
        api.MapPost("export/csv", ExportHandler.ExportCsv);
        api.MapPost("export/json", ExportHandler.ExportJson);

        // Config endpoint consumed by the React SPA
        api.MapGet(
            "config",
            () =>
                Results.Ok(
                    new
                    {
                        liveTailEnabled = options.LiveTailEnabled,
                        fileDownloadEnabled = options.EnableFileDownload,
                        fileDeleteEnabled = options.EnableFileDelete,
                    }
                )
        );

        // Extension registrations (e.g. SignalR hub from Serilog.Viewer.Realtime)
        foreach (var registration in builder.EndpointRegistrations)
            registration(endpoints);

        // Embedded React SPA
        static async Task ServeSpa(HttpContext context, string basePath, ILogger logger)
        {
            var requestPath = context.Request.Path.Value ?? string.Empty;
            var relativePath =
                requestPath.Length > basePath.Length
                    ? requestPath[basePath.Length..].TrimStart('/')
                    : string.Empty;

            logger.LogDebug(
                "SPA request: {RequestPath} -> resource: '{RelativePath}'",
                requestPath,
                relativePath
            );

            var stream = OpenEmbeddedResource(relativePath);
            var isFallback = stream == null;

            if (isFallback)
            {
                logger.LogDebug(
                    "No embedded resource for '{RelativePath}', falling back to index.html",
                    relativePath
                );
                stream = OpenEmbeddedResource("index.html");
            }

            if (stream == null)
            {
                logger.LogWarning(
                    "index.html not found in embedded resources - React assets may not be embedded"
                );
                context.Response.StatusCode = 404;
                return;
            }

            using (stream)
            {
                var fileName =
                    isFallback || string.IsNullOrEmpty(relativePath)
                        ? "index.html"
                        : Path.GetFileName(relativePath);

                context.Response.ContentType = GetContentType(fileName);

                if (IsHashedAsset(fileName))
                    context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                else
                    context.Response.Headers.CacheControl = "no-cache";

                await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
            }
        }

        endpoints.MapGet(basePath, context => ServeSpa(context, basePath, logger));
        endpoints.MapGet($"{basePath}/{{**slug}}", context => ServeSpa(context, basePath, logger));
    }

    private static Stream? OpenEmbeddedResource(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || relativePath == "/")
            relativePath = "index.html";

        var resourceSuffix = relativePath.Replace('/', '.').Replace('\\', '.');
        var resourceName = _resourcePrefix + resourceSuffix;

        return _assembly.GetManifestResourceStream(resourceName);
    }

    private static bool IsHashedAsset(string fileName)
    {
        return System.Text.RegularExpressions.Regex.IsMatch(
            fileName,
            @"-[A-Za-z0-9_]{8}\.(js|css|woff2?)$"
        );
    }

    private static string GetContentType(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "application/javascript",
            ".mjs" => "application/javascript",
            ".css" => "text/css",
            ".json" => "application/json",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            ".map" => "application/json",
            _ => "application/octet-stream",
        };
}
