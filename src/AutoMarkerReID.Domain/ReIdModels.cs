namespace AutoMarkerReID.Domain;

public sealed record ModelEmbedding(string ModelName, float[] Values);

public sealed record ReferenceImage(
    string Id,
    string QueryId,
    string ImagePath,
    IReadOnlyDictionary<string, float[]> Embeddings,
    string? Timestamp,
    float[]? AppearanceDescriptor,
    DateTimeOffset LastModified);

public sealed record QueryIdentity(
    string Id,
    IReadOnlyList<ReferenceImage> References,
    float CalibratedThreshold)
{
    public int MatchLimit => Math.Max(0, References.Count - 1);
}

public enum MatchSource
{
    Body,
    Face,
    BodyWithAppearance,
    Manual,
}

public sealed record MatchResult(
    string QueryId,
    string? ReferenceId,
    BoundingBox BoundingBox,
    float Score,
    float? Margin,
    float? BestReferenceScore,
    float? PixelScore,
    IReadOnlyDictionary<string, float> ModelScores,
    string? CardTimestamp,
    MatchSource Source,
    bool ManuallyEdited = false);

public sealed record RecognitionExplanation(
    BoundingBox BoundingBox,
    string? QueryId,
    float Score,
    float Threshold,
    float? Margin,
    float? BestReferenceScore,
    bool Accepted,
    string Reason,
    IReadOnlyDictionary<string, float> ModelScores);

public sealed record SavedResult(
    string Id,
    DateTimeOffset CreatedAt,
    string? DominantQueryId,
    string OriginalImagePath,
    string MarkedImagePath,
    IReadOnlyList<MatchResult> Matches);

public sealed record ReviewSession(
    Guid Id,
    ImageFrame Original,
    IReadOnlyList<MatchResult> Matches,
    DateTimeOffset CreatedAt,
    ImageJobSource Source,
    IReadOnlyList<RecognitionExplanation>? Explanations = null);

public enum ReviewDecision
{
    Cancel,
    SaveAndCopy,
    RematchEditedImage,
    // Re-run recognition on the same image after the user moved the threshold.
    Rematch,
}

public sealed record ReviewOutcome(
    ReviewDecision Decision,
    ImageFrame? EditedImage = null,
    IReadOnlyList<MatchResult>? Matches = null,
    float? MatchThresholdOverride = null,
    bool ResetMatchThreshold = false);
