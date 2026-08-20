using AutoMarkerReID.Application;
using AutoMarkerReID.Domain;
using AutoMarkerReID.Imaging;
using Microsoft.Extensions.Logging;

namespace AutoMarkerReID.Inference;

public sealed class OpenVinoMatchEngine(
    IModelRuntime runtime,
    IImageCodec codec,
    ICandidateGenerator candidateGenerator,
    IBoxRenderer boxRenderer,
    IOcrService ocr,
    QueryCatalog catalog,
    UserSelectionState selection,
    ILogger<OpenVinoMatchEngine> logger) : IMatchEngine
{
    private static readonly IReadOnlyDictionary<string, float> ModelWeights = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
    {
        ["osnet_0288"] = 0.25f,
        ["osnet_lct_0277"] = 0.75f,
        ["osnet_lct_0286"] = 1.00f,
    };

    public async Task<IReadOnlyList<MatchResult>> MatchAsync(ImageFrame screenshot, string? queryScope, CancellationToken cancellationToken)
    {
        var queries = catalog.Snapshot;
        var scopedQueries = queryScope is null
            ? queries
            : queries.Where(item => item.Key.Equals(queryScope, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        if (scopedQueries.Count == 0)
        {
            return [];
        }

        var candidates = candidateGenerator.Generate(screenshot);
        if (candidates.Count == 0)
        {
            MatchEngineLog.NoCandidates(logger);
            return [];
        }

        var source = candidates.FirstOrDefault(candidate => candidate.IsSource);

        var accepted = new List<MatchResult>();
        foreach (var candidate in candidates.Where(candidate => !candidate.IsSource))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var crop = codec.Crop(screenshot, candidate.BoundingBox);
                var timestamp = await ocr.ReadTimestampAsync(crop, cancellationToken).ConfigureAwait(false);
                var embeddings = await runtime.ExtractBodyEmbeddingsAsync(crop, cancellationToken).ConfigureAwait(false);
                if (embeddings.Count == 0)
                {
                    continue;
                }

                var evaluations = scopedQueries.Values
                    .Select(query => EvaluateQuery(query, embeddings, timestamp))
                    .Where(evaluation => evaluation is not null)
                    .Cast<QueryEvaluation>()
                    .OrderByDescending(evaluation => evaluation.EnsembleScore)
                    .ToList();
                if (evaluations.Count == 0)
                {
                    continue;
                }

                var winner = evaluations[0];
                var runnerUp = evaluations.Count > 1 ? evaluations[1].EnsembleScore : 0;
                var modelWinners = embeddings.Keys.ToDictionary(
                    model => model,
                    model => evaluations.OrderByDescending(evaluation => evaluation.ModelScores.GetValueOrDefault(model)).First().Query.Id,
                    StringComparer.OrdinalIgnoreCase);

                float? faceScore = null;
                float? faceMargin = null;
                var bodyMargin = winner.EnsembleScore - runnerUp;
                var bodyClearlyAccepted = winner.EnsembleScore >= winner.Query.CalibratedThreshold &&
                                          bodyMargin >= ReIdDefaults.AiMatchMargin &&
                                          winner.BestReferenceScore >= ReIdDefaults.BestReferenceThreshold &&
                                          modelWinners.Values.All(query => query.Equals(winner.Query.Id, StringComparison.OrdinalIgnoreCase));
                if (!bodyClearlyAccepted)
                {
                    var face = await runtime.ExtractFaceEmbeddingAsync(crop, cancellationToken).ConfigureAwait(false);
                    if (face is not null)
                    {
                        faceScore = BestFaceScore(winner.Query, face);
                        var competingFace = evaluations.Skip(1).Select(evaluation => BestFaceScore(evaluation.Query, face)).DefaultIfEmpty(0).Max();
                        faceMargin = faceScore - competingFace;
                    }
                }

                float? appearanceScore = null;
                float? appearanceMargin = null;
                if (selection.AppearanceEnabled)
                {
                    var descriptor = LbpDescriptor.Create(crop);
                    if (descriptor is not null)
                    {
                        appearanceScore = BestAppearanceScore(winner.Query, descriptor);
                        var competingAppearance = evaluations.Skip(1)
                            .Select(evaluation => BestAppearanceScore(evaluation.Query, descriptor))
                            .DefaultIfEmpty(0)
                            .Max();
                        appearanceMargin = appearanceScore - competingAppearance;
                    }
                }

                var score = new IdentityScore(
                    winner.Query.Id,
                    winner.EnsembleScore,
                    runnerUp,
                    winner.BestReferenceScore,
                    modelWinners,
                    winner.BestReference?.Id,
                    faceScore,
                    faceMargin,
                    appearanceScore,
                    appearanceMargin);
                var threshold = selection.MatchThresholdOverride ?? winner.Query.CalibratedThreshold;
                var decision = IdentityDecisionPolicy.Decide(score, threshold, selection.AppearanceEnabled);
                if (!decision.Accepted || decision.Source is null)
                {
                    continue;
                }

                if (timestamp is not null && !string.Equals(timestamp, winner.BestReference?.Timestamp, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                accepted.Add(new MatchResult(
                    winner.Query.Id,
                    winner.BestReference?.Id,
                    boxRenderer.SnapToCard(screenshot, candidate.BoundingBox),
                    winner.EnsembleScore,
                    bodyMargin,
                    winner.BestReferenceScore,
                    candidate.PixelScore,
                    winner.ModelScores,
                    timestamp,
                    decision.Source.Value));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                MatchEngineLog.CandidateFailed(logger, candidate.BoundingBox.X1, candidate.BoundingBox.Y1, exception);
            }
        }

        var postProcessed = MatchPostProcessor.Apply(accepted, scopedQueries, source?.BoundingBox);
        return postProcessed;
    }

    private static QueryEvaluation? EvaluateQuery(QueryIdentity query, IReadOnlyDictionary<string, float[]> candidate, string? timestamp)
    {
        IReadOnlyList<ReferenceImage> references = query.References;
        if (timestamp is not null)
        {
            references = references.Where(reference => string.Equals(reference.Timestamp, timestamp, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (references.Count == 0)
            {
                return null;
            }
        }

        var modelScores = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        ReferenceImage? bestReference = null;
        var bestReferenceScore = float.NegativeInfinity;
        double weightedScore = 0;
        double totalWeight = 0;
        foreach (var model in candidate)
        {
            var referenceScores = references
                .Where(reference => reference.Embeddings.TryGetValue(model.Key, out var stored) && stored.Length == model.Value.Length)
                .Select(reference => (Reference: reference, Score: Dot(model.Value, reference.Embeddings[model.Key])))
                .OrderByDescending(item => item.Score)
                .ToArray();
            if (referenceScores.Length == 0)
            {
                continue;
            }

            var identityScore = referenceScores.Take(ReIdDefaults.TopReferenceCount).Average(item => item.Score);
            modelScores[model.Key] = identityScore;
            var weight = ModelWeights.GetValueOrDefault(model.Key, 1f);
            weightedScore += identityScore * weight;
            totalWeight += weight;
            if (referenceScores[0].Score > bestReferenceScore)
            {
                bestReferenceScore = referenceScores[0].Score;
                bestReference = referenceScores[0].Reference;
            }
        }

        return totalWeight <= 0 || bestReference is null
            ? null
            : new QueryEvaluation(query, (float)(weightedScore / totalWeight), bestReferenceScore, bestReference, modelScores);
    }

    private static float BestFaceScore(QueryIdentity query, float[] face) => query.References
        .Where(reference => reference.Embeddings.TryGetValue("face", out var stored) && stored.Length == face.Length)
        .Select(reference => Dot(face, reference.Embeddings["face"]))
        .DefaultIfEmpty(0)
        .Max();

    private static float BestAppearanceScore(QueryIdentity query, float[] descriptor) => query.References
        .Where(reference => reference.AppearanceDescriptor is { Length: 512 })
        .Select(reference => LbpDescriptor.Similarity(descriptor, reference.AppearanceDescriptor))
        .DefaultIfEmpty(0)
        .Max();

    private static float Dot(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        float score = 0;
        for (var index = 0; index < left.Length; index++)
        {
            score += left[index] * right[index];
        }

        return score;
    }

    private sealed record QueryEvaluation(
        QueryIdentity Query,
        float EnsembleScore,
        float BestReferenceScore,
        ReferenceImage BestReference,
        IReadOnlyDictionary<string, float> ModelScores);
}

internal static partial class MatchEngineLog
{
    [LoggerMessage(EventId = 3200, Level = LogLevel.Debug, Message = "Không tìm thấy candidate card.")]
    public static partial void NoCandidates(ILogger logger);

    [LoggerMessage(EventId = 3201, Level = LogLevel.Error, Message = "Candidate tại ({x},{y}) inference thất bại.")]
    public static partial void CandidateFailed(ILogger logger, int x, int y, Exception exception);
}
