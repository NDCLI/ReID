using AutoMarkerReID.Application;
using AutoMarkerReID.Domain;

namespace AutoMarkerReID.Tests;

public sealed class GeometryAndPolicyTests
{
    [Fact]
    public void BoundingBoxNormalizesClampsAndComputesIou()
    {
        var box = new BoundingBox(90, 80, -10, -20).Clamp(70, 60);
        Assert.Equal(new BoundingBox(0, 0, 70, 60), box);
        Assert.Equal(0.25, new BoundingBox(0, 0, 20, 20).IntersectionOverUnion(new BoundingBox(0, 0, 10, 10)), 5);
        Assert.True(box.Contains(69, 59));
        Assert.False(box.Contains(70, 60));
    }

    [Fact]
    public void IdentityPolicyAcceptsBodyOnlyWhenAllOpenSetGatesPass()
    {
        var accepted = IdentityDecisionPolicy.Decide(Score(0.80f, 0.70f, 0.75f), 0.68f, false);
        var rejected = IdentityDecisionPolicy.Decide(Score(0.80f, 0.77f, 0.75f), 0.68f, false);
        Assert.True(accepted.Accepted);
        Assert.Equal(MatchSource.Body, accepted.Source);
        Assert.False(rejected.Accepted);
    }

    [Fact]
    public void IdentityPolicyCanRescueWithFaceOrAppearance()
    {
        var face = Score(0.65f, 0.64f, 0.63f) with { FaceScore = 0.80f, FaceMargin = 0.25f };
        var appearance = Score(0.75f, 0.72f, 0.72f) with { AppearanceScore = 0.90f, AppearanceMargin = 0.04f };
        Assert.Equal(MatchSource.Face, IdentityDecisionPolicy.Decide(face, 0.68f, false).Source);
        Assert.Equal(MatchSource.BodyWithAppearance, IdentityDecisionPolicy.Decide(appearance, 0.68f, true).Source);
    }

    [Fact]
    public void PostProcessorChoosesDominantIdentityRemovesSourceAndHonorsAutomaticReferenceLimit()
    {
        var source = new BoundingBox(0, 0, 100, 100);
        var matches = new[]
        {
            Match("Query_1", new BoundingBox(0, 0, 100, 100), 0.99f),
            Match("Query_1", new BoundingBox(120, 0, 200, 100), 0.80f),
            Match("Query_1", new BoundingBox(220, 0, 300, 100), 0.90f),
            Match("Query_1", new BoundingBox(320, 0, 400, 100), 0.70f),
            Match("Query_2", new BoundingBox(120, 130, 200, 230), 0.95f),
        };
        var queries = new Dictionary<string, QueryIdentity>(StringComparer.OrdinalIgnoreCase)
        {
            ["Query_1"] = Query("Query_1", 3),
            ["Query_2"] = Query("Query_2", 3),
        };
        var result = MatchPostProcessor.Apply(matches, queries, source);
        Assert.Equal(2, result.Count);
        Assert.All(result, item => Assert.Equal("Query_1", item.QueryId));
        Assert.DoesNotContain(result, item => item.BoundingBox == source);
        Assert.Collection(result,
            first => Assert.Equal(120, first.BoundingBox.X1),
            second => Assert.Equal(220, second.BoundingBox.X1));
    }

    [Fact]
    public void PostProcessorKeepsOnlyTopTwoRowsAndSpacesAdjacentBoxes()
    {
        var matches = new[]
        {
            Match("Query_1", new BoundingBox(100, 0, 161, 100), 0.90f),
            Match("Query_1", new BoundingBox(159, 0, 220, 100), 0.89f),
            Match("Query_1", new BoundingBox(100, 140, 160, 240), 0.88f),
            Match("Query_1", new BoundingBox(100, 280, 160, 380), 0.87f),
        };
        var queries = new Dictionary<string, QueryIdentity>(StringComparer.OrdinalIgnoreCase)
        {
            ["Query_1"] = Query("Query_1", 5),
        };

        var result = MatchPostProcessor.Apply(matches, queries);

        Assert.Equal(3, result.Count);
        Assert.Equal(2, result.Select(item => item.BoundingBox.CenterY).Distinct().Count());
        Assert.DoesNotContain(result, item => item.BoundingBox.Y1 == 280);
        var firstRow = result.Where(item => item.BoundingBox.Y1 == 0).OrderBy(item => item.BoundingBox.X1).ToArray();
        Assert.True(firstRow[1].BoundingBox.X1 - firstRow[0].BoundingBox.X2 >= ReIdDefaults.BoxMinimumGap);
    }

    [Fact]
    public void ManualSpacingDoesNotLimitNumberOfBoxes()
    {
        var matches = Enumerable.Range(0, 12)
            .Select(index => Match("Query_1", new BoundingBox(index * 70, 0, index * 70 + 60, 100), 1))
            .ToArray();

        Assert.Equal(12, MatchPostProcessor.EnsureMinimumHorizontalGap(matches).Length);
    }

    private static IdentityScore Score(float ensemble, float runnerUp, float reference) => new(
        "Query_1", ensemble, runnerUp, reference,
        new Dictionary<string, string> { ["m1"] = "Query_1", ["m2"] = "Query_1" }, "r1");

    private static MatchResult Match(string query, BoundingBox box, float score) => new(
        query, null, box, score, null, null, null, new Dictionary<string, float>(), null, MatchSource.Body);

    private static QueryIdentity Query(string id, int count) => new(id,
        Enumerable.Range(0, count).Select(index => new ReferenceImage($"r{index}", id, $"r{index}.png",
            new Dictionary<string, float[]>(), null, null, DateTimeOffset.UtcNow)).ToArray(), 0.68f);
}
