using AutoMarkerReID.Application;
using AutoMarkerReID.Domain;
using AutoMarkerReID.Imaging;
using Microsoft.Extensions.Logging;

namespace AutoMarkerReID.Inference;

public sealed class EngineInitializer(
    IModelRuntime runtime,
    IQueryRepository queryRepository,
    IFeatureCache cache,
    IImageCodec codec,
    IOcrService ocr,
    QueryCatalog catalog,
    ILogger<EngineInitializer> logger) : IEngineInitializer
{
    public bool IsReady { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await runtime.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await ocr.WarmupAsync(cancellationToken).ConfigureAwait(false);
        await cache.RemoveOrphansAsync(cancellationToken).ConfigureAwait(false);
        await BuildMissingReferencesAsync(progress: null, cancellationToken).ConfigureAwait(false);
        IsReady = runtime.IsAvailable;
    }

    public async Task RebuildCacheAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        IsReady = false;
        await cache.DeleteAllAsync(cancellationToken).ConfigureAwait(false);
        await BuildMissingReferencesAsync(progress, cancellationToken).ConfigureAwait(false);
        IsReady = runtime.IsAvailable;
    }

    private async Task BuildMissingReferencesAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var queries = await queryRepository.LoadAsync(cancellationToken).ConfigureAwait(false);
        var total = Math.Max(1, queries.Sum(query => query.References.Count));
        var completed = 0;
        var rebuilt = new List<QueryIdentity>(queries.Count);
        foreach (var query in queries)
        {
            var references = new List<ReferenceImage>(query.References.Count);
            foreach (var reference in query.References)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resolved = reference;
                if (reference.Embeddings.Count == 0)
                {
                    try
                    {
                        var image = codec.Decode(await File.ReadAllBytesAsync(reference.ImagePath, cancellationToken).ConfigureAwait(false));
                        var embeddings = new Dictionary<string, float[]>(
                            await runtime.ExtractBodyEmbeddingsAsync(image, cancellationToken).ConfigureAwait(false),
                            StringComparer.OrdinalIgnoreCase);
                        var face = await runtime.ExtractFaceEmbeddingAsync(image, cancellationToken).ConfigureAwait(false);
                        if (face is not null)
                        {
                            embeddings["face"] = face;
                        }

                        resolved = reference with
                        {
                            Embeddings = embeddings,
                            Timestamp = await ocr.ReadTimestampAsync(image, cancellationToken).ConfigureAwait(false),
                            AppearanceDescriptor = LbpDescriptor.Create(image),
                            LastModified = new DateTimeOffset(File.GetLastWriteTimeUtc(reference.ImagePath), TimeSpan.Zero),
                        };
                        await cache.WriteAsync(resolved, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        EngineInitializerLog.ReferenceBuildFailed(logger, reference.ImagePath, exception);
                    }
                }

                if (resolved.Embeddings.Count > 0)
                {
                    references.Add(resolved);
                }

                completed++;
                progress?.Report((double)completed / total);
            }

            rebuilt.Add(query with { References = references, CalibratedThreshold = Calibrate(references) });
        }

        catalog.Replace(rebuilt);
    }

    private static float Calibrate(List<ReferenceImage> references)
    {
        var scores = new List<float>();
        for (var leftIndex = 0; leftIndex < references.Count; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < references.Count; rightIndex++)
            {
                foreach (var model in references[leftIndex].Embeddings.Keys
                             .Intersect(references[rightIndex].Embeddings.Keys, StringComparer.OrdinalIgnoreCase)
                             .Where(model => !model.Equals("face", StringComparison.OrdinalIgnoreCase)))
                {
                    scores.Add(Dot(references[leftIndex].Embeddings[model], references[rightIndex].Embeddings[model]));
                }
            }
        }

        if (scores.Count == 0)
        {
            return ReIdDefaults.AiMatchThreshold;
        }

        scores.Sort();
        var index = Math.Clamp((int)Math.Floor((scores.Count - 1) * 0.10), 0, scores.Count - 1);
        return Math.Clamp(scores[index] - 0.05f, 0.65f, 0.90f);
    }

    private static float Dot(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length != right.Length)
        {
            return 0;
        }

        float score = 0;
        for (var index = 0; index < left.Length; index++)
        {
            score += left[index] * right[index];
        }

        return score;
    }
}

internal static partial class EngineInitializerLog
{
    [LoggerMessage(EventId = 3100, Level = LogLevel.Error, Message = "Không thể tạo cache cho reference {path}.")]
    public static partial void ReferenceBuildFailed(ILogger logger, string path, Exception exception);
}
