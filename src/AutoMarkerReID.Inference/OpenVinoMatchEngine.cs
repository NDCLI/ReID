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
    private IReadOnlyList<RecognitionExplanation> _lastExplanations = [];

    public IReadOnlyList<RecognitionExplanation> LastExplanations => Volatile.Read(ref _lastExplanations);

    private static readonly IReadOnlyDictionary<string, float> ModelWeights = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
    {
        ["osnet_0288"] = 0.25f,
        ["osnet_lct_0277"] = 0.75f,
        ["osnet_lct_0286"] = 1.00f,
    };

    public async Task<IReadOnlyList<MatchResult>> MatchAsync(ImageFrame screenshot, string? queryScope, CancellationToken cancellationToken)
    {
        var explanations = new List<RecognitionExplanation>();
        Volatile.Write(ref _lastExplanations, []);
        var queries = catalog.Snapshot;
        if (queries.Count == 0 || queryScope is not null && !queries.ContainsKey(queryScope))
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
        string? sourceTimestamp = null;
        var requiredQueryId = queryScope;
        if (source is not null)
        {
            var sourceCrop = codec.Crop(screenshot, source.BoundingBox);
            sourceTimestamp = await ocr.ReadTimestampAsync(sourceCrop, cancellationToken).ConfigureAwait(false);
            if (queryScope is null)
            {
                var sourceEmbeddings = await runtime.ExtractBodyEmbeddingsAsync(sourceCrop, cancellationToken).ConfigureAwait(false);
                var sourceEvaluations = queries.Values
                    .Select(query => EvaluateQuery(query, sourceEmbeddings, sourceTimestamp))
                    .Where(evaluation => evaluation is not null)
                    .Cast<QueryEvaluation>()
                    .OrderByDescending(evaluation => evaluation.EnsembleScore)
                    .ToList();
                if (sourceEvaluations.Count == 0)
                {
                    explanations.Add(new RecognitionExplanation(source.BoundingBox, null, 0, 0, null, null,
                        false, "Không xác định được Query của card nguồn.", new Dictionary<string, float>()));
                    Volatile.Write(ref _lastExplanations, explanations);
                    return [];
                }

                var sourceWinner = sourceEvaluations[0];
                var sourceScore = CreateIdentityScore(sourceEvaluations, sourceEmbeddings.Keys);
                var sourceThreshold = selection.MatchThresholdOverride ?? sourceWinner.Query.CalibratedThreshold;
                var sourceTimestampMatched = sourceTimestamp is not null &&
                                             string.Equals(sourceTimestamp, sourceWinner.BestReference?.Timestamp, StringComparison.OrdinalIgnoreCase);
                var sourceDecision = IdentityDecisionPolicy.Decide(sourceScore, sourceThreshold, false, sourceTimestampMatched);
                if (!sourceDecision.Accepted)
                {
                    explanations.Add(new RecognitionExplanation(source.BoundingBox, sourceWinner.Query.Id,
                        sourceWinner.EnsembleScore, sourceThreshold,
                        sourceWinner.EnsembleScore - (sourceEvaluations.Count > 1 ? sourceEvaluations[1].EnsembleScore : 0),
                        sourceWinner.BestReferenceScore, false,
                        "Không xác định chắc chắn Query của card nguồn: " + ExplainDecision(sourceDecision, sourceScore, sourceThreshold),
                        sourceWinner.ModelScores));
                    Volatile.Write(ref _lastExplanations, explanations);
                    return [];
                }

                requiredQueryId = sourceWinner.Query.Id;
            }
        }

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
                    explanations.Add(new RecognitionExplanation(candidate.BoundingBox, null, 0, 0, null, null,
                        false, "Không trích xuất được đặc trưng từ card.", new Dictionary<string, float>()));
                    continue;
                }

                var evaluations = queries.Values
                    .Select(query => EvaluateQuery(query, embeddings, timestamp))
                    .Where(evaluation => evaluation is not null)
                    .Cast<QueryEvaluation>()
                    .OrderByDescending(evaluation => evaluation.EnsembleScore)
                    .ToList();
                if (evaluations.Count == 0)
                {
                    explanations.Add(new RecognitionExplanation(candidate.BoundingBox, null, 0, 0, null, null,
                        false, $"Không có reference cùng timestamp {timestamp}.",
                        new Dictionary<string, float>()));
                    continue;
                }

                var winner = evaluations[0];
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

                var score = CreateIdentityScore(evaluations, embeddings.Keys, appearanceScore, appearanceMargin);
                var bodyMargin = score.EnsembleScore - score.RunnerUpScore;
                var threshold = selection.MatchThresholdOverride ?? winner.Query.CalibratedThreshold;
                if (requiredQueryId is not null &&
                    !string.Equals(winner.Query.Id, requiredQueryId, StringComparison.OrdinalIgnoreCase))
                {
                    explanations.Add(new RecognitionExplanation(candidate.BoundingBox, winner.Query.Id,
                        winner.EnsembleScore, threshold, bodyMargin, winner.BestReferenceScore,
                        false, $"Bị loại: {winner.Query.Id} thắng nhưng phạm vi yêu cầu {requiredQueryId}.", winner.ModelScores));
                    continue;
                }
                var timestampMatched = timestamp is not null &&
                                       string.Equals(timestamp, winner.BestReference?.Timestamp, StringComparison.OrdinalIgnoreCase);
                var decision = IdentityDecisionPolicy.Decide(score, threshold, selection.AppearanceEnabled, timestampMatched);
                var acceptedByGates = decision.Accepted && decision.Source is not null;
                var reason = ExplainDecision(decision, score, threshold);
                if (!decision.Accepted || decision.Source is null)
                {
                    explanations.Add(new RecognitionExplanation(candidate.BoundingBox, winner.Query.Id,
                        winner.EnsembleScore, threshold, bodyMargin, winner.BestReferenceScore,
                        false, reason, winner.ModelScores));
                    continue;
                }

                if (timestamp is not null && !string.Equals(timestamp, winner.BestReference?.Timestamp, StringComparison.OrdinalIgnoreCase))
                {
                    explanations.Add(new RecognitionExplanation(candidate.BoundingBox, winner.Query.Id,
                        winner.EnsembleScore, threshold, bodyMargin, winner.BestReferenceScore,
                        false, $"Bị loại: timestamp {timestamp} không khớp reference.", winner.ModelScores));
                    continue;
                }

                var hasVisibleFace = await runtime.HasVisibleFaceAsync(crop, cancellationToken).ConfigureAwait(false);
                reason += hasVisibleFace
                    ? " Hướng: thấy mặt, đang quay về camera."
                    : " Hướng: không thấy mặt, có thể quay lưng hoặc quay nghiêng.";
                if (timestamp is null)
                    reason += " OCR không đọc được timestamp; đã dùng AI với toàn bộ reference.";

                explanations.Add(new RecognitionExplanation(candidate.BoundingBox, winner.Query.Id,
                    winner.EnsembleScore, threshold, bodyMargin, winner.BestReferenceScore,
                    acceptedByGates, reason, winner.ModelScores));

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
                explanations.Add(new RecognitionExplanation(candidate.BoundingBox, null, 0, 0, null, null,
                    false, $"Bị loại: lỗi xử lý candidate ({exception.GetType().Name}).", new Dictionary<string, float>()));
            }
        }

        var postProcessed = MatchPostProcessor.Apply(accepted, queries, source?.BoundingBox, sourceTimestamp);
        var retainedBoxes = postProcessed.Select(match => match.BoundingBox).ToHashSet();
        var finalExplanations = explanations.Select(item =>
            item.Accepted && !retainedBoxes.Contains(boxRenderer.SnapToCard(screenshot, item.BoundingBox))
                ? item with { Accepted = false, Reason = "Đạt ngưỡng nhưng bị hậu xử lý loại (Query trội, giới hạn số lượng hoặc vị trí trùng)." }
                : item).ToArray();
        Volatile.Write(ref _lastExplanations, finalExplanations);
        return postProcessed;
    }

    private static IdentityScore CreateIdentityScore(
        IReadOnlyList<QueryEvaluation> evaluations,
        IEnumerable<string> modelNames,
        float? appearanceScore = null,
        float? appearanceMargin = null)
    {
        var winner = evaluations[0];
        var runnerUp = evaluations.Count > 1 ? evaluations[1].EnsembleScore : 0;
        var modelWinners = modelNames.ToDictionary(
            model => model,
            model => evaluations.OrderByDescending(evaluation => evaluation.ModelScores.GetValueOrDefault(model)).First().Query.Id,
            StringComparer.OrdinalIgnoreCase);
        return new IdentityScore(
            winner.Query.Id,
            winner.EnsembleScore,
            runnerUp,
            winner.BestReferenceScore,
            modelWinners,
            winner.BestReference?.Id,
            appearanceScore,
            appearanceMargin);
    }

    private static string ExplainDecision(IdentityDecision decision, IdentityScore score, float threshold)
    {
        if (decision.Accepted)
        {
            if (decision.Reason == "timestamp rescue")
            {
                return "Đạt nhờ timestamp khớp và reference rất mạnh.";
            }

            return decision.Source switch
            {
                MatchSource.BodyWithAppearance => "Đạt nhờ LBP phân xử kết quả sát ngưỡng.",
                _ => "Đạt score, margin và đồng thuận model.",
            };
        }

        var reasons = new List<string>();
        if (score.EnsembleScore < threshold) reasons.Add($"score {score.EnsembleScore:P0} < ngưỡng {threshold:P0}");
        if (score.EnsembleScore - score.RunnerUpScore < ReIdDefaults.AiMatchMargin) reasons.Add("margin với Query kế tiếp quá thấp");
        if (score.BestReferenceScore < ReIdDefaults.BestReferenceThreshold) reasons.Add("reference tốt nhất chưa đủ mạnh");
        if (score.ModelWinners.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1) reasons.Add("các model không đồng thuận");
        return reasons.Count == 0 ? "Không vượt qua các điều kiện open-set." : "Bị loại: " + string.Join(", ", reasons) + ".";
    }

    private static QueryEvaluation? EvaluateQuery(QueryIdentity query, IReadOnlyDictionary<string, float[]> candidate, string? timestamp)
    {
        var references = timestamp is null
            ? query.References.ToArray()
            : query.References
                .Where(reference => string.Equals(reference.Timestamp, timestamp, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        if (references.Length == 0)
        {
            return null;
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
    [LoggerMessage(EventId = 3200, Level = LogLevel.Debug, Message = "Không tìm thấy thẻ kết quả phù hợp để phân tích.")]
    public static partial void NoCandidates(ILogger logger);

    [LoggerMessage(EventId = 3201, Level = LogLevel.Error, Message = "Không thể nhận diện thẻ kết quả tại vị trí ({x},{y}).")]
    public static partial void CandidateFailed(ILogger logger, int x, int y, Exception exception);
}
