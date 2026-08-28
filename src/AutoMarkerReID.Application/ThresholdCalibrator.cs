using AutoMarkerReID.Domain;

namespace AutoMarkerReID.Application;

// A Query's accept threshold is derived from how similar its own reference
// images are to each other: the 10th percentile of intra-Query similarity, less
// a small slack. Below MinCalibrationReferences there are too few pairs for that
// percentile to mean anything — two references yield a single observation, one
// yields none — so those Queries keep the shared default instead of a threshold
// fitted to noise.
public static class ThresholdCalibrator
{
    private const float Slack = 0.05f;
    private const float Floor = 0.65f;
    private const float Ceiling = 0.90f;
    private const double Percentile = 0.10;

    public static float Calibrate(IReadOnlyList<ReferenceImage> references)
    {
        if (references.Count < ReIdDefaults.MinCalibrationReferences)
        {
            return ReIdDefaults.AiMatchThreshold;
        }

        var scores = new List<float>();
        for (var leftIndex = 0; leftIndex < references.Count; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < references.Count; rightIndex++)
            {
                var left = references[leftIndex].Embeddings;
                var right = references[rightIndex].Embeddings;
                foreach (var model in left.Keys
                             .Intersect(right.Keys, StringComparer.OrdinalIgnoreCase)
                             .Where(model => !model.Equals("face", StringComparison.OrdinalIgnoreCase)))
                {
                    scores.Add(Dot(left[model], right[model]));
                }
            }
        }

        if (scores.Count == 0)
        {
            return ReIdDefaults.AiMatchThreshold;
        }

        scores.Sort();
        var index = Math.Clamp((int)Math.Floor((scores.Count - 1) * Percentile), 0, scores.Count - 1);
        return Math.Clamp(scores[index] - Slack, Floor, Ceiling);
    }

    private static float Dot(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length != right.Length || left.Length == 0)
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
