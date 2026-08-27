using AutoMarkerReID.Domain;

namespace AutoMarkerReID.Application;

public interface IEngineInitializer
{
    bool IsReady { get; }
    Task InitializeAsync(CancellationToken cancellationToken);
    Task RebuildCacheAsync(IProgress<double>? progress, CancellationToken cancellationToken);
}

public interface IClipboardMonitor
{
    bool IsSuspended { get; }
    Task RunAsync(Func<ImageJob, CancellationToken, ValueTask> onImage, CancellationToken cancellationToken);
    void SetSuspended(bool suspended);
    void IgnoreNextWrite();
    void SynchronizeGeneration();
}

public interface IClipboardWriter
{
    Task WriteImageAsync(ImageFrame image, CancellationToken cancellationToken);
}

public interface IScreenCaptureService
{
    BoundingBox VirtualScreenBounds { get; }
    Task<ImageFrame> CaptureAsync(BoundingBox region, CancellationToken cancellationToken);
}

public interface IFileTrashService
{
    Task MoveToRecycleBinAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken);
}

public interface IImageJobProcessor
{
    Task<ProcessingResult> ProcessAsync(ImageJob job, CancellationToken cancellationToken);
}

public interface IReviewCompletionService
{
    Task CompleteAsync(ReviewSession session, ReviewOutcome outcome, CancellationToken cancellationToken);
}

public interface IQueryRepository
{
    string RootPath { get; }
    Task<IReadOnlyList<QueryIdentity>> LoadAsync(CancellationToken cancellationToken);
    Task<string> EnsureQueryAsync(string queryId, CancellationToken cancellationToken);
    Task<string> AddReferenceAsync(string queryId, ImageFrame image, CancellationToken cancellationToken);
    Task DeleteScopeAsync(string? queryId, CancellationToken cancellationToken);
    Task DeleteAllAsync(CancellationToken cancellationToken);
}

public interface IResultRepository
{
    string RootPath { get; }
    Task<SavedResult> SaveAsync(ReviewSession session, CancellationToken cancellationToken);
    Task<IReadOnlyList<SavedResult>> ListAsync(CancellationToken cancellationToken);
    Task UpdateMatchesAsync(SavedResult result, IReadOnlyList<MatchResult> matches, CancellationToken cancellationToken);
    Task MoveToRecycleBinAsync(SavedResult result, CancellationToken cancellationToken);
    Task DeleteAllAsync(CancellationToken cancellationToken);
}

public interface IFeatureCache
{
    Task<ReferenceImage?> TryReadAsync(string queryId, string imagePath, CancellationToken cancellationToken);
    Task WriteAsync(ReferenceImage reference, CancellationToken cancellationToken);
    Task DeleteAllAsync(CancellationToken cancellationToken);
    Task RemoveOrphansAsync(CancellationToken cancellationToken);
}

public interface IModelRuntime : IAsyncDisposable
{
    bool IsAvailable { get; }
    IReadOnlyList<string> ActiveBodyModels { get; }
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, float[]>> ExtractBodyEmbeddingsAsync(ImageFrame image, CancellationToken cancellationToken);
    Task<bool> HasVisibleFaceAsync(ImageFrame image, CancellationToken cancellationToken);
}

public interface IOcrService
{
    bool IsReady => true;
    Task WarmupAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    Task<string?> ReadTimestampAsync(ImageFrame card, CancellationToken cancellationToken);
}

public interface IImageCodec
{
    ImageFrame Decode(ReadOnlySpan<byte> encoded);
    byte[] EncodePng(ImageFrame image);
    ImageFrame Crop(ImageFrame image, BoundingBox bounds);
}

public interface IInterfaceDetector
{
    bool IsReIdInterface(ImageFrame image, out float score);
}

public interface ICandidateGenerator
{
    IReadOnlyList<CardCandidate> Generate(ImageFrame screenshot);
}

public interface IQueryCollector
{
    Task<QueryCollectionResult> TryCollectAsync(ImageFrame image, string targetQueryId, CancellationToken cancellationToken);
}

public interface IMatchEngine
{
    IReadOnlyList<RecognitionExplanation> LastExplanations => [];
    Task<IReadOnlyList<MatchResult>> MatchAsync(ImageFrame screenshot, string? queryScope, CancellationToken cancellationToken);
}

public interface IBoxRenderer
{
    ImageFrame Draw(ImageFrame image, IReadOnlyList<MatchResult> matches);
    BoundingBox SnapToCard(ImageFrame image, BoundingBox approximate);
    BoundingBox? FindCardAtPoint(ImageFrame image, int x, int y);
}

public sealed record CardCandidate(BoundingBox BoundingBox, float PixelScore, int Row, bool IsSource = false);

public abstract record ProcessingResult
{
    private ProcessingResult()
    {
    }

    public sealed record Ignored(string Reason) : ProcessingResult;
    public sealed record QueryCollected(string QueryId, string ImagePath) : ProcessingResult;
    public sealed record ReviewRequired(ReviewSession Session) : ProcessingResult;
}

public sealed record QueryCollectionResult(bool Accepted, string Reason, string? ImagePath = null);
