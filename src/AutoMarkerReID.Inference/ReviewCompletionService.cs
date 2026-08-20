using AutoMarkerReID.Application;
using AutoMarkerReID.Domain;

namespace AutoMarkerReID.Inference;

public sealed class ReviewCompletionService(
    IResultRepository results,
    IBoxRenderer renderer,
    IClipboardWriter clipboardWriter,
    IClipboardMonitor clipboardMonitor) : IReviewCompletionService
{
    public async Task CompleteAsync(ReviewSession session, ReviewOutcome outcome, CancellationToken cancellationToken)
    {
        switch (outcome.Decision)
        {
            case ReviewDecision.Cancel:
                return;
            case ReviewDecision.SaveAndCopy:
                var finalSession = outcome.Matches is null ? session : session with { Matches = outcome.Matches };
                await results.SaveAsync(finalSession, cancellationToken).ConfigureAwait(false);
                var marked = renderer.Draw(finalSession.Original, finalSession.Matches);
                clipboardMonitor.IgnoreNextWrite();
                await clipboardWriter.WriteImageAsync(marked, cancellationToken).ConfigureAwait(false);
                return;
            case ReviewDecision.RematchEditedImage when outcome.EditedImage is not null:
                return;
            default:
                throw new InvalidOperationException("Kết quả Review không hợp lệ.");
        }
    }
}
