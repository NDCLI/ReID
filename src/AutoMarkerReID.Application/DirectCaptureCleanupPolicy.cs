using AutoMarkerReID.Domain;
using Microsoft.Extensions.Logging;

namespace AutoMarkerReID.Application;

public static class DirectCaptureCleanupPolicy
{
    public static bool ShouldDeleteSavedCopy(ImageJob job, ProcessingResult result) =>
        result is ProcessingResult.QueryCollected &&
        job.Source is ImageJobSource.NewCapture or ImageJobSource.RepeatCapture &&
        !string.IsNullOrWhiteSpace(job.SourcePath);

    public static void TryDeleteSavedCopy(ImageJob job, ProcessingResult result, ILogger logger)
    {
        if (!ShouldDeleteSavedCopy(job, result))
        {
            return;
        }

        try
        {
            File.Delete(job.SourcePath!);
        }
        catch (Exception exception)
        {
            DirectCaptureCleanupLog.DeleteFailed(logger, job.SourcePath!, exception);
        }
    }
}

internal static partial class DirectCaptureCleanupLog
{
    [LoggerMessage(EventId = 1010, Level = LogLevel.Warning, Message = "Không thể xóa bản sao ảnh chụp {path} sau khi thêm vào Query.")]
    public static partial void DeleteFailed(ILogger logger, string path, Exception exception);
}
