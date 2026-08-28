using AutoMarkerReID.Domain;
using Microsoft.Extensions.Logging;

namespace AutoMarkerReID.Application;

// The Pictures\Screenshots copy of a direct capture only exists so the shot is
// never lost. Once the same capture is stored durably elsewhere — queries/ after
// collection, output/ after the user saves from Review — that copy is a
// duplicate and is removed. Captures that end nowhere (ignored, or Review
// cancelled) keep their copy.
public static class DirectCaptureCleanupPolicy
{
    public static bool ShouldDeleteSavedCopy(ImageJob job, ProcessingResult result) =>
        HasSavedCopy(job) && result is ProcessingResult.QueryCollected;

    public static bool ShouldDeleteSavedCopy(ImageJob job, ReviewOutcome outcome) =>
        HasSavedCopy(job) && outcome.Decision is ReviewDecision.SaveAndCopy;

    public static void TryDeleteSavedCopy(ImageJob job, ProcessingResult result, ILogger logger)
    {
        if (ShouldDeleteSavedCopy(job, result)) Delete(job, logger);
    }

    public static void TryDeleteSavedCopy(ImageJob job, ReviewOutcome outcome, ILogger logger)
    {
        if (ShouldDeleteSavedCopy(job, outcome)) Delete(job, logger);
    }

    private static bool HasSavedCopy(ImageJob job) =>
        job.Source is ImageJobSource.NewCapture or ImageJobSource.RepeatCapture &&
        !string.IsNullOrWhiteSpace(job.SourcePath);

    private static void Delete(ImageJob job, ILogger logger)
    {
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
    [LoggerMessage(EventId = 1010, Level = LogLevel.Warning, Message = "Không thể xóa bản sao ảnh chụp {path} sau khi ảnh đã được lưu.")]
    public static partial void DeleteFailed(ILogger logger, string path, Exception exception);
}
