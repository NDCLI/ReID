using AutoMarkerReID.Application;
using AutoMarkerReID.Domain;

namespace AutoMarkerReID.Tests;

public sealed class ThresholdCalibratorTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TooFewReferencesKeepTheSharedDefault(int count)
    {
        // Below four references the pairwise sample is too small for a 10th
        // percentile to describe anything, so no per-Query threshold is fitted.
        var references = Enumerable.Range(0, count).Select(index => Reference(index, 0.95f)).ToArray();

        Assert.Equal(ReIdDefaults.AiMatchThreshold, ThresholdCalibrator.Calibrate(references));
    }

    [Fact]
    public void TightlyClusteredReferencesRaiseTheThreshold()
    {
        var references = Enumerable.Range(0, 6).Select(index => Reference(index, 0.99f)).ToArray();

        var threshold = ThresholdCalibrator.Calibrate(references);

        Assert.True(threshold > ReIdDefaults.AiMatchThreshold, $"expected above default, got {threshold}");
        Assert.InRange(threshold, 0.65f, 0.90f);
    }

    [Fact]
    public void ThresholdIsClampedIntoTheUsableBand()
    {
        var identical = Enumerable.Range(0, 5).Select(_ => Reference(0, 1f)).ToArray();
        var scattered = Enumerable.Range(0, 5).Select(index => Scattered(index)).ToArray();

        Assert.Equal(0.90f, ThresholdCalibrator.Calibrate(identical));
        Assert.Equal(0.65f, ThresholdCalibrator.Calibrate(scattered));
    }

    [Fact]
    public void ReferencesWithoutEmbeddingsFallBackToTheDefault()
    {
        var references = Enumerable.Range(0, 6)
            .Select(index => new ReferenceImage($"r{index}", "Query_1", $"r{index}.png",
                new Dictionary<string, float[]>(), null, null, DateTimeOffset.UtcNow))
            .ToArray();

        Assert.Equal(ReIdDefaults.AiMatchThreshold, ThresholdCalibrator.Calibrate(references));
    }

    [Fact]
    public void FaceEmbeddingsAreExcludedFromBodyCalibration()
    {
        var body = Enumerable.Range(0, 5).Select(index => Reference(index, 0.99f)).ToArray();
        var withFace = body
            .Select(reference => reference with
            {
                Embeddings = new Dictionary<string, float[]>(reference.Embeddings, StringComparer.OrdinalIgnoreCase)
                {
                    ["face"] = [0, 1, 0],
                },
            })
            .ToArray();

        Assert.Equal(ThresholdCalibrator.Calibrate(body), ThresholdCalibrator.Calibrate(withFace));
    }

    // Unit vectors spread over a small angle, so every pair scores near `similarity`.
    private static ReferenceImage Reference(int index, float similarity)
    {
        var angle = Math.Acos(Math.Clamp(similarity, -1, 1)) * index;
        return WithEmbedding(index, [(float)Math.Cos(angle), (float)Math.Sin(angle), 0]);
    }

    private static ReferenceImage Scattered(int index) =>
        WithEmbedding(index, index % 2 == 0 ? [1, 0, 0] : [0, 1, 0]);

    private static ReferenceImage WithEmbedding(int index, float[] embedding) => new(
        $"r{index}", "Query_1", $"r{index}.png",
        new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase) { ["body"] = embedding },
        null, null, DateTimeOffset.UtcNow);
}
