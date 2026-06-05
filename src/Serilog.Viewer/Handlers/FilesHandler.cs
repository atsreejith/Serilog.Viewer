using Microsoft.AspNetCore.Http;
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
}
