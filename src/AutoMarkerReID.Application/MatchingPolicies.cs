using AutoMarkerReID.Domain;

namespace AutoMarkerReID.Application;

public sealed record IdentityScore(
    string QueryId,
    float EnsembleScore,
    float RunnerUpScore,
    float BestReferenceScore,
    IReadOnlyDictionary<string, string> ModelWinners,
    string? BestReferenceId,
    float? FaceScore = null,
    float? FaceMargin = null,
    float? AppearanceScore = null,
    float? AppearanceMargin = null);

public sealed record IdentityDecision(bool Accepted, string? QueryId, MatchSource? Source, string Reason);

public static class IdentityDecisionPolicy
{
    public static IdentityDecision Decide(IdentityScore score, float calibratedThreshold, bool appearanceEnabled)
    {
        var modelAgreement = score.ModelWinners.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() <= 1;
        var margin = score.EnsembleScore - score.RunnerUpScore;
        var bodyAbsolute = score.EnsembleScore >= calibratedThreshold;
        var strongReference = score.BestReferenceScore >= ReIdDefaults.BestReferenceThreshold;

        if (bodyAbsolute && strongReference && modelAgreement && margin >= ReIdDefaults.AiMatchMargin)
        {
            return new IdentityDecision(true, score.QueryId, MatchSource.Body, "body");
        }

        if (score.FaceScore >= ReIdDefaults.FaceMatchThreshold && score.FaceMargin >= ReIdDefaults.FaceMatchMargin)
        {
            return new IdentityDecision(true, score.QueryId, MatchSource.Face, "face rescue");
        }

        if (appearanceEnabled && bodyAbsolute && strongReference && modelAgreement &&
            score.AppearanceScore >= ReIdDefaults.AppearanceFloor && score.AppearanceMargin >= ReIdDefaults.AppearanceMargin)
        {
            return new IdentityDecision(true, score.QueryId, MatchSource.BodyWithAppearance, "appearance tie-break");
        }

        return new IdentityDecision(false, null, null, "open-set gates failed");
    }
}

public static class MatchPostProcessor
{
    public static IReadOnlyList<MatchResult> Apply(
        IEnumerable<MatchResult> matches,
        IReadOnlyDictionary<string, QueryIdentity> queries,
        BoundingBox? sourceCard = null)
    {
        var materialized = matches
            .Where(match => sourceCard is null || match.BoundingBox.IntersectionOverUnion(sourceCard.Value) <= ReIdDefaults.NmsThreshold)
            .ToList();

        if (materialized.Count == 0)
        {
            return [];
        }

        var dominant = materialized
            .GroupBy(match => match.QueryId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new { QueryId = group.Key, Count = group.Count(), Score = group.Sum(match => match.Score) })
            .OrderByDescending(item => item.Count)
            .ThenByDescending(item => item.Score)
            .First().QueryId;

        var filtered = materialized
            .Where(match => string.Equals(match.QueryId, dominant, StringComparison.OrdinalIgnoreCase))
            .OrderBy(match => match.BoundingBox.CenterY)
            .ThenBy(match => match.BoundingBox.X1)
            .ToList();

        var rowCenters = ClusterRows(filtered.Select(match => match.BoundingBox.CenterY)).Take(ReIdDefaults.FastMaxRows).ToArray();
        filtered = filtered
            .Where(match => rowCenters.Any(center => Math.Abs(match.BoundingBox.CenterY - center) <= Math.Max(16, match.BoundingBox.Height / 2)))
            .ToList();

        if (queries.TryGetValue(dominant, out var query))
        {
            filtered = filtered
                .OrderByDescending(match => match.Score)
                .Take(query.MatchLimit)
                .OrderBy(match => match.BoundingBox.CenterY)
                .ThenBy(match => match.BoundingBox.X1)
                .ToList();
        }

        return EnsureMinimumHorizontalGap(AlignRows(filtered));
    }

    public static MatchResult[] EnsureMinimumHorizontalGap(IEnumerable<MatchResult> matches)
    {
        var rows = new List<List<MatchResult>>();
        foreach (var match in matches.OrderBy(item => item.BoundingBox.CenterY).ThenBy(item => item.BoundingBox.X1))
        {
            var row = rows.FirstOrDefault(items =>
                Math.Abs((int)items.Average(item => item.BoundingBox.CenterY) - match.BoundingBox.CenterY) <= 40);
            if (row is null) rows.Add([match]); else row.Add(match);
        }

        foreach (var row in rows)
        {
            row.Sort((left, right) => left.BoundingBox.X1.CompareTo(right.BoundingBox.X1));
            for (var index = 1; index < row.Count; index++)
            {
                var left = row[index - 1];
                var right = row[index];
                var deficit = left.BoundingBox.X2 + ReIdDefaults.BoxMinimumGap - right.BoundingBox.X1;
                if (deficit <= 0) continue;

                var trimLeft = (deficit + 1) / 2;
                var trimRight = deficit / 2;
                var leftX2 = Math.Max(left.BoundingBox.X1 + 8, left.BoundingBox.X2 - trimLeft);
                var rightX1 = Math.Min(right.BoundingBox.X2 - 8, right.BoundingBox.X1 + trimRight);
                row[index - 1] = left with
                {
                    BoundingBox = new BoundingBox(left.BoundingBox.X1, left.BoundingBox.Y1, leftX2, left.BoundingBox.Y2),
                };
                row[index] = right with
                {
                    BoundingBox = new BoundingBox(rightX1, right.BoundingBox.Y1, right.BoundingBox.X2, right.BoundingBox.Y2),
                };
            }
        }

        return rows.SelectMany(row => row)
            .OrderBy(item => item.BoundingBox.CenterY)
            .ThenBy(item => item.BoundingBox.X1)
            .ToArray();
    }

    private static MatchResult[] AlignRows(List<MatchResult> matches)
    {
        var rows = new List<List<MatchResult>>();
        foreach (var match in matches.OrderBy(item => item.BoundingBox.CenterY))
        {
            var row = rows.FirstOrDefault(items => Math.Abs((int)items.Average(item => item.BoundingBox.CenterY) - match.BoundingBox.CenterY) <= 40);
            if (row is null) rows.Add([match]); else row.Add(match);
        }

        return rows.SelectMany(row =>
        {
            var y1 = Median(row.Select(item => item.BoundingBox.Y1));
            var y2 = Median(row.Select(item => item.BoundingBox.Y2));
            return row.Select(item => item with
            {
                BoundingBox = new BoundingBox(item.BoundingBox.X1, y1, item.BoundingBox.X2, y2),
            });
        }).OrderBy(item => item.BoundingBox.CenterY).ThenBy(item => item.BoundingBox.X1).ToArray();
    }

    private static int Median(IEnumerable<int> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }

    private static IEnumerable<int> ClusterRows(IEnumerable<int> centers)
    {
        var clusters = new List<List<int>>();
        foreach (var center in centers.Order())
        {
            var cluster = clusters.FirstOrDefault(item => Math.Abs((int)item.Average() - center) <= 40);
            if (cluster is null)
            {
                clusters.Add([center]);
            }
            else
            {
                cluster.Add(center);
            }
        }

        return clusters.OrderBy(item => item.Average()).Select(item => (int)item.Average());
    }
}
