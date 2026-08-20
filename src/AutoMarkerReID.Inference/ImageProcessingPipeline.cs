using AutoMarkerReID.Application;
using AutoMarkerReID.Domain;

namespace AutoMarkerReID.Inference;

public sealed class ImageProcessingPipeline(
    IInterfaceDetector interfaceDetector,
    IQueryCollector queryCollector,
    IMatchEngine matchEngine,
    UserSelectionState selection) : IImageJobProcessor
{
    public async Task<ProcessingResult> ProcessAsync(ImageJob job, CancellationToken cancellationToken)
    {
        job.Image.Validate();
        if (interfaceDetector.IsReIdInterface(job.Image, out _))
        {
            var matches = await matchEngine.MatchAsync(job.Image, selection.RecognitionScope, cancellationToken).ConfigureAwait(false);
            return new ProcessingResult.ReviewRequired(new ReviewSession(
                Guid.NewGuid(),
                job.Image,
                matches,
                DateTimeOffset.Now,
                job.Source));
        }

        var collected = await queryCollector.TryCollectAsync(job.Image, selection.TargetQuery, cancellationToken).ConfigureAwait(false);
        return collected.Accepted
            ? new ProcessingResult.QueryCollected(selection.TargetQuery, collected.ImagePath!)
            : new ProcessingResult.Ignored(collected.Reason);
    }
}
