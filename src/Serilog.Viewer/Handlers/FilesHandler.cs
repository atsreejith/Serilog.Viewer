using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Serilog.Viewer.Interfaces;
using Serilog.Viewer.Models;

namespace Serilog.Viewer.Handlers;

internal static class FilesHandler
{
    public static async Task<IResult> GetFiles(
        ILogRepository repository,
        CancellationToken cancellationToken
    )
    {
        var files = await repository.GetFilesAsync(cancellationToken);
        var result = files.Select(f => new
        {
            f.Name,
            f.SizeBytes,
            SizeFormatted = FormatBytes(f.SizeBytes),
            LastModified = f.LastModified,
            f.IsActive,
            Format = f.Format.ToString(),
            f.LineCount,
        });
        return Results.Ok(result);
    }

    public static async Task<IResult> DownloadFile(
        string fileName,
        ILogRepository repository,
        IOptions<LogViewerOptions> options,
        CancellationToken cancellationToken
    )
    {
        if (!options.Value.EnableFileDownload)
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var file = await repository.GetFileAsync(
            NormalizeRouteFileName(fileName),
            cancellationToken
        );
        if (file is null)
            return Results.NotFound();

        return Results.File(
            file.FullPath,
            "application/octet-stream",
            Path.GetFileName(file.Name),
            enableRangeProcessing: true
        );
    }

    public static async Task<IResult> DeleteFile(
        string fileName,
        ILogRepository repository,
        IOptions<LogViewerOptions> options,
        CancellationToken cancellationToken
    )
    {
        if (!options.Value.EnableFileDelete)
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var deleted = await repository.DeleteFileAsync(
            NormalizeRouteFileName(fileName),
            cancellationToken
        );

        return deleted ? Results.NoContent() : Results.NotFound();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    private static string NormalizeRouteFileName(string fileName) =>
        fileName.Replace('\\', '/');
}
