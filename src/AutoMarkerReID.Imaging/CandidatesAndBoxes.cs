using AutoMarkerReID.Application;
using AutoMarkerReID.Domain;
using OpenCvSharp;

namespace AutoMarkerReID.Imaging;

public sealed class OpenCvCandidateGenerator : ICandidateGenerator
{
    public IReadOnlyList<CardCandidate> Generate(ImageFrame screenshot)
    {
        using var source = MatConversion.ToMat(screenshot);
        using var gray = new Mat();
        Cv2.CvtColor(source, gray, source.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        using var blurred = new Mat();
        Cv2.GaussianBlur(gray, blurred, new Size(3, 3), 0);
        using var edges = new Mat();
        Cv2.Canny(blurred, edges, 50, 150);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        Cv2.MorphologyEx(edges, edges, MorphTypes.Close, kernel, iterations: 2);
        Cv2.FindContours(edges, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        var minimumHeight = Math.Max(70, screenshot.Height / 9);
        var maximumHeight = Math.Max(minimumHeight + 1, (int)(screenshot.Height * 0.8));
        var candidates = contours
            .Select(Cv2.BoundingRect)
            .Where(rect => rect.Height >= minimumHeight && rect.Height <= maximumHeight)
            .Where(rect => rect.Width >= 45 && rect.Width <= rect.Height * 1.2)
            .Where(rect => rect.X >= screenshot.Width * ReIdDefaults.IgnoreLeftRatio)
            .Where(rect => rect.Bottom <= screenshot.Height * (1 - ReIdDefaults.IgnoreBottomRatio))
            .Select(rect => new CardCandidate(new BoundingBox(rect.Left, rect.Top, rect.Right, rect.Bottom), 1f, 0))
            .OrderByDescending(candidate => candidate.BoundingBox.Area)
            .Take(ReIdDefaults.MaxPixelCandidates)
            .ToList();

        var suppressed = NonMaximumSuppression(candidates, ReIdDefaults.NmsThreshold);
        var rowCenters = ClusterRows(suppressed.Select(candidate => candidate.BoundingBox.CenterY));
        var rowAssigned = suppressed
            .Select(candidate => candidate with
            {
                Row = rowCenters
                    .Select((center, index) => (Distance: Math.Abs(center - candidate.BoundingBox.CenterY), Index: index))
                    .MinBy(item => item.Distance).Index,
            })
            .Where(candidate => candidate.Row < ReIdDefaults.FastMaxRows)
            .ToList();
        var medianWidths = rowAssigned
            .GroupBy(candidate => candidate.Row)
            .ToDictionary(group => group.Key, group => Median(group.Select(candidate => candidate.BoundingBox.Width)));
        var acceptedRows = rowAssigned
            .Where(candidate => candidate.BoundingBox.Width <= medianWidths[candidate.Row] * 1.5)
            .ToList();
        var rejectedWide = rowAssigned
            .Where(candidate => candidate.BoundingBox.Width > medianWidths[candidate.Row] * 1.5)
            .ToList();
        RecoverLeadingCards(acceptedRows, rejectedWide, medianWidths);
        var withRows = acceptedRows
            .OrderBy(candidate => candidate.Row)
            .ThenBy(candidate => candidate.BoundingBox.X1)
            .ToList();

        if (withRows.Count >= 4)
        {
            var sourceIndex = withRows
                .Select((candidate, index) => (candidate, index))
                .OrderBy(item => item.candidate.Row)
                .ThenBy(item => item.candidate.BoundingBox.X1)
                .First().index;
            withRows[sourceIndex] = withRows[sourceIndex] with { IsSource = true };
        }

        return withRows;
    }

    private static List<CardCandidate> NonMaximumSuppression(List<CardCandidate> candidates, float threshold)
    {
        var result = new List<CardCandidate>();
        foreach (var candidate in candidates)
        {
            if (result.All(existing => existing.BoundingBox.IntersectionOverUnion(candidate.BoundingBox) <= threshold))
            {
                result.Add(candidate);
            }
        }

        return result;
    }

    private static int[] ClusterRows(IEnumerable<int> centers)
    {
        var clusters = new List<List<int>>();
        foreach (var center in centers.Order())
        {
            var cluster = clusters.FirstOrDefault(item => Math.Abs((int)item.Average() - center) <= 50);
            if (cluster is null)
            {
                clusters.Add([center]);
            }
            else
            {
                cluster.Add(center);
            }
        }

        return clusters.OrderBy(item => item.Average()).Select(item => (int)item.Average()).ToArray();
    }

    private static int Median(IEnumerable<int> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }

    private static void RecoverLeadingCards(List<CardCandidate> accepted,
        IReadOnlyList<CardCandidate> rejectedWide, Dictionary<int, int> medianWidths)
    {
        var pitches = accepted
            .GroupBy(candidate => candidate.Row)
            .SelectMany(group => group.OrderBy(candidate => candidate.BoundingBox.X1)
                .Zip(group.OrderBy(candidate => candidate.BoundingBox.X1).Skip(1),
                    (left, right) => right.BoundingBox.X1 - left.BoundingBox.X1))
            .Where(pitch => pitch > 0)
            .ToArray();
        if (pitches.Length == 0) return;
        var pitch = Median(pitches);

        foreach (var row in accepted.Select(candidate => candidate.Row).Distinct().ToArray())
        {
            var rowCards = accepted.Where(candidate => candidate.Row == row).OrderBy(candidate => candidate.BoundingBox.X1).ToArray();
            var wideRegions = rejectedWide.Where(candidate => candidate.Row == row).ToArray();
            if (rowCards.Length == 0 || wideRegions.Length == 0) continue;

            var y1 = Median(rowCards.Select(candidate => candidate.BoundingBox.Y1));
            var y2 = Median(rowCards.Select(candidate => candidate.BoundingBox.Y2));
            var cardWidth = medianWidths[row];
            var predictedLeft = rowCards[0].BoundingBox.X1 - pitch;
            while (predictedLeft >= 0)
            {
                var centerX = predictedLeft + cardWidth / 2;
                var centerY = y1 + (y2 - y1) / 2;
                if (!wideRegions.Any(region => region.BoundingBox.Contains(centerX, centerY))) break;
                accepted.Add(new CardCandidate(new BoundingBox(predictedLeft, y1, predictedLeft + cardWidth, y2), 1f, row));
                predictedLeft -= pitch;
            }
        }
    }
}

public sealed class OpenCvBoxRenderer : IBoxRenderer
{
    public ImageFrame Draw(ImageFrame image, IReadOnlyList<MatchResult> matches)
    {
        using var mat = MatConversion.ToMat(image);
        var colors = new[]
        {
            new Scalar(0, 0, 255),
            new Scalar(0, 200, 0),
            new Scalar(255, 80, 0),
            new Scalar(200, 0, 200),
        };
        var queryColors = matches.Select(match => match.QueryId).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select((query, index) => (query, color: colors[index % colors.Length]))
            .ToDictionary(item => item.query, item => item.color, StringComparer.OrdinalIgnoreCase);

        foreach (var match in matches)
        {
            var box = match.BoundingBox.Clamp(image.Width, image.Height);
            Cv2.Rectangle(mat, new Rect(box.X1, box.Y1, box.Width, box.Height), queryColors[match.QueryId], ReIdDefaults.BoxThickness);
        }

        return MatConversion.ToImageFrame(mat);
    }

    public BoundingBox SnapToCard(ImageFrame image, BoundingBox approximate)
    {
        var clamped = approximate.Clamp(image.Width, image.Height);
        if (clamped.Width < 8 || clamped.Height < 8) return clamped;

        using var mat = MatConversion.ToMat(image);
        using var gray = new Mat();
        using var gradient = new Mat();
        using var edges = new Mat();
        Cv2.CvtColor(mat, gray, mat.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        Cv2.Sobel(gray, gradient, MatType.CV_32F, 1, 0, ksize: 3);
        Cv2.ConvertScaleAbs(gradient, edges);

        var sampleTop = Math.Clamp(clamped.Y1 + clamped.Height / 12, 0, image.Height - 1);
        var sampleBottom = Math.Clamp(clamped.Y2 - clamped.Height / 12, sampleTop + 1, image.Height);
        var radius = Math.Clamp(clamped.Width / 3, 12, 18);
        var left = FindOuterHorizontalEdge(gray, edges, clamped.X1, radius, sampleTop, sampleBottom, true);
        var right = FindOuterHorizontalEdge(gray, edges, clamped.X2, radius, sampleTop, sampleBottom, false);
        var snapped = new BoundingBox(left, clamped.Y1, right, clamped.Y2).Clamp(image.Width, image.Height);
        return snapped.Width <= clamped.Width * 1.6 ? snapped : clamped;
    }

    private static int FindOuterHorizontalEdge(Mat gray, Mat edges, int expected, int radius,
        int sampleTop, int sampleBottom, bool left)
    {
        var width = gray.Cols;
        var nearStart = left ? expected - 3 : expected;
        var outerStart = left ? expected - radius : expected + 3;
        var outerEnd = left ? expected - 3 : expected + radius;
        if (nearStart < 0 || nearStart + 3 > width || outerStart < 0 || outerEnd >= width || outerEnd < outerStart)
            return Math.Clamp(expected, 0, width);

        var near = BandMean(gray, nearStart, 3, sampleTop, sampleBottom);
        var gutter = Enumerable.Range(outerStart, outerEnd - outerStart + 1)
            .Min(x => BandMean(gray, x, 1, sampleTop, sampleBottom));
        // Only search farther out when the pixels immediately outside the candidate are the
        // brighter gray card padding. If they are already the dark gutter, this is the outer edge.
        if (near <= gutter + 6) return Math.Clamp(expected, 0, width);

        var first = left ? Math.Max(0, expected - radius) : Math.Min(width - 1, expected + 3);
        var last = left ? Math.Max(0, expected - 3) : Math.Min(width - 1, expected + radius);
        if (last < first) return Math.Clamp(expected, 0, width);

        var strengths = new List<(int Position, double Strength)>();
        for (var x = first; x <= last; x++)
        {
            using var line = new Mat(edges, new Rect(x, sampleTop, 1, sampleBottom - sampleTop));
            strengths.Add((x, Cv2.Mean(line).Val0));
        }

        var mean = strengths.Average(item => item.Strength);
        var strongest = strengths.Max(item => item.Strength);
        if (strongest < 6 || strongest < mean * 1.25) return Math.Clamp(expected, 0, width);
        var edgeThreshold = Math.Max(6, Math.Max(mean * 1.20, strongest * 0.35));
        var outerEdge = left
            ? strengths.Where(item => item.Strength >= edgeThreshold).OrderByDescending(item => item.Position).FirstOrDefault()
            : strengths.Where(item => item.Strength >= edgeThreshold).OrderBy(item => item.Position).FirstOrDefault();
        return outerEdge.Strength > 0 ? outerEdge.Position : Math.Clamp(expected, 0, width);
    }

    private static double BandMean(Mat gray, int x, int width, int y1, int y2)
    {
        using var band = new Mat(gray, new Rect(x, y1, width, y2 - y1));
        return Cv2.Mean(band).Val0;
    }
}
