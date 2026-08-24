using AutoMarkerReID.Application;
using AutoMarkerReID.Domain;
using AutoMarkerReID.Inference;

namespace AutoMarkerReID.Tests;

public sealed class PipelineSnapshotTests
{
    [Fact]
    public async Task ReportsTargetQueryUsedForAsyncCollection()
    {
        var selection = new UserSelectionState { TargetQuery = "Query_2" };
        var collector = new WaitingCollector();
        var pipeline = new ImageProcessingPipeline(new PersonCaptureDetector(), collector, new UnusedMatchEngine(), selection);

        var image = new ImageFrame(1, 1, 3, ImagePixelFormat.Bgr24, [0, 0, 0]);
        var processing = pipeline.ProcessAsync(ImageJob.Create(image, ImageJobSource.NewCapture), CancellationToken.None);
        await collector.Started.Task;
        selection.TargetQuery = "Query_3";
        collector.Complete();

        var result = Assert.IsType<ProcessingResult.QueryCollected>(await processing);
        Assert.Equal("Query_2", collector.TargetQuery);
        Assert.Equal("Query_2", result.QueryId);
    }

    private sealed class PersonCaptureDetector : IInterfaceDetector
    {
        public bool IsReIdInterface(ImageFrame image, out float score)
        {
            score = 0;
            return false;
        }
    }

    private sealed class WaitingCollector : IQueryCollector
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? TargetQuery { get; private set; }

        public async Task<QueryCollectionResult> TryCollectAsync(ImageFrame image, string targetQueryId, CancellationToken cancellationToken)
        {
            TargetQuery = targetQueryId;
            Started.SetResult();
            await _completion.Task.WaitAsync(cancellationToken);
            return new QueryCollectionResult(true, string.Empty, "reference.png");
        }

        public void Complete() => _completion.SetResult();
    }

    private sealed class UnusedMatchEngine : IMatchEngine
    {
        public Task<IReadOnlyList<MatchResult>> MatchAsync(ImageFrame screenshot, string? queryScope, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Match engine should not be called for a person capture.");
    }
}
