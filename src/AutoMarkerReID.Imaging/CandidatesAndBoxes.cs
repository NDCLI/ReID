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
        var regularGrid = DetectRegularGrid(gray);
        if (regularGrid.Count >= 4)
        {
            regularGrid = regularGrid.Take(ReIdDefaults.FastMaxCards).ToList();
            regularGrid[0] = regularGrid[0] with { IsSource = true };
            return regularGrid;
        }

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
            .Take(ReIdDefaults.FastMaxCards)
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

    private static List<CardCandidate> DetectRegularGrid(Mat gray)
    {
        var screenHeight = gray.Rows;
        var screenWidth = gray.Cols;
        var contentX = (int)(screenWidth * 0.30);
        var minimumBandHeight = Math.Max(80, (int)(screenHeight * 0.15));
        var rowBands = new List<(int Top, int Bottom)>();
        int? bandStart = null;
        for (var y = 0; y < screenHeight; y++)
        {
            var activePixels = 0;
            for (var x = contentX; x < screenWidth; x++)
            {
                if (gray.At<byte>(y, x) > 45) activePixels++;
            }

            var active = activePixels / (double)Math.Max(1, screenWidth - contentX) > 0.12;
            if (active && bandStart is null)
            {
                bandStart = y;
            }
            else if (!active && bandStart is { } start)
            {
                if (y - start >= minimumBandHeight) rowBands.Add((start, y));
                bandStart = null;
            }
        }

        if (bandStart is { } finalStart && screenHeight - finalStart >= minimumBandHeight)
            rowBands.Add((finalStart, screenHeight));
        rowBands = rowBands.Take(ReIdDefaults.FastMaxRows).ToList();
        if (rowBands.Count == 0) return [];

        var minimumCardWidth = (int)(screenWidth * 0.04);
        var maximumCardWidth = (int)(screenWidth * 0.14);
        List<(int Left, int Right)> RowSegments((int Top, int Bottom) row)
        {
            var scanTop = Math.Min(row.Top + 6, row.Bottom - 1);
            var scanBottom = Math.Min(row.Top + 28, row.Bottom);
            if (scanBottom <= scanTop) return [];

            var segments = new List<(int Left, int Right)>();
            int? segmentStart = null;
            for (var x = (int)(screenWidth * 0.28); x < screenWidth; x++)
            {
                var active = false;
                for (var y = scanTop; y < scanBottom; y++)
                {
                    if (gray.At<byte>(y, x) <= 30) continue;
                    active = true;
                    break;
                }

                if (active && segmentStart is null)
                {
                    segmentStart = x;
                }
                else if (!active && segmentStart is { } start)
                {
                    if (x - start >= minimumCardWidth && x - start <= maximumCardWidth)
                        segments.Add((start, x));
                    segmentStart = null;
                }
            }

            if (segmentStart is { } finalSegment &&
                screenWidth - finalSegment >= minimumCardWidth &&
                screenWidth - finalSegment <= maximumCardWidth)
                segments.Add((finalSegment, screenWidth));
            return segments;
        }

        var portraitMaximum = (int)(screenWidth * 0.09);
        var modelSegments = rowBands
            .Select(RowSegments)
            .Select(segments => segments.Where(segment => segment.Right - segment.Left <= portraitMaximum).ToList())
            .OrderByDescending(segments => segments.Count)
            .FirstOrDefault() ?? [];
        if (modelSegments.Count < 4) return [];

        var cardWidth = Median(modelSegments.Select(segment => segment.Right - segment.Left));
        var starts = modelSegments.Select(segment => segment.Left).Order().ToArray();
        var pitches = starts.Zip(starts.Skip(1), (left, right) => right - left)
            .Where(pitch => pitch < cardWidth * 1.5)
            .ToArray();
        var pitch = pitches.Length == 0 ? cardWidth + 4 : Median(pitches);
        var firstX = starts[0];
        if (cardWidth < 40 || pitch <= cardWidth || pitch > cardWidth * 1.4) return [];

        List<(int Left, int Right)> ProjectedColumns()
        {
            var columns = new List<(int Left, int Right)>();
            for (var x = firstX; x + cardWidth <= screenWidth && columns.Count < 20; x += pitch)
                columns.Add((x, Math.Min(screenWidth, x + cardWidth)));
            return columns;
        }

        var result = new List<CardCandidate>();
        for (var row = 0; row < rowBands.Count; row++)
        {
            var columns = RowSegments(rowBands[row]);
            var projected = ProjectedColumns();
            if (columns.Count < 4)
            {
                columns = projected;
            }
            else if (pitches.Length > 0)
            {
                var regularColumns = columns
                    .Where(column => column.Right - column.Left <= portraitMaximum)
                    .ToArray();
                var alignmentTolerance = Math.Max(6, cardWidth / 6);
                var alignedColumns = regularColumns.Count(column => projected.Any(model =>
                    Math.Abs((model.Left + model.Right - column.Left - column.Right) / 2) <= alignmentTolerance));
                if (regularColumns.Length >= 3 && alignedColumns >= Math.Ceiling(regularColumns.Length * 0.75))
                {
                    foreach (var column in projected)
                    {
                        var center = column.Left + ((column.Right - column.Left) / 2);
                        if (!columns.Any(existing => center >= existing.Left && center < existing.Right))
                            columns.Add(column);
                    }

                    columns = columns.OrderBy(column => column.Left).ToList();
                }
            }
            foreach (var column in columns)
            {
                result.Add(new CardCandidate(
                    new BoundingBox(column.Left, rowBands[row].Top, column.Right, rowBands[row].Bottom),
                    1f,
                    row));
            }
        }

        return result;
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

    public BoundingBox? FindCardAtPoint(ImageFrame image, int x, int y)
    {
        if (x < 0 || x >= image.Width || y < 0 || y >= image.Height) return null;

        var allowedRows = new OpenCvCandidateGenerator().Generate(image)
            .GroupBy(candidate => candidate.Row)
            .OrderBy(row => row.Key)
            .Take(ReIdDefaults.FastMaxRows)
            .Select(row => (Top: row.Min(candidate => candidate.BoundingBox.Y1),
                            Bottom: row.Max(candidate => candidate.BoundingBox.Y2)))
            .ToArray();
        if (allowedRows.Length > 0 && !allowedRows.Any(row => y >= row.Top && y <= row.Bottom)) return null;

        using var mat = MatConversion.ToMat(image);
        using var gray = new Mat();
        Cv2.CvtColor(mat, gray, mat.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        var checkTop = Math.Max(0, y - 3);
        var checkBottom = Math.Min(image.Height, y + 4);
        var (seedLeft, seedRight) = CardInsetX(gray, x, checkTop, checkBottom);
        if (seedRight - seedLeft < ReIdDefaults.ClickBoxMinimumSize) return null;

        var snapped = SnapClickedBoxToCard(gray, new BoundingBox(seedLeft, y, seedRight, Math.Min(image.Height, y + 1)));
        return snapped.Width < ReIdDefaults.ClickBoxMinimumSize || snapped.Height < ReIdDefaults.ClickBoxMinimumSize
            ? null
            : snapped;
    }

    private static BoundingBox SnapClickedBoxToCard(Mat gray, BoundingBox approximate)
    {
        var width = gray.Cols;
        var height = gray.Rows;
        var x1 = Math.Clamp(approximate.X1, 0, width - 1);
        var x2 = Math.Clamp(approximate.X2, x1 + 1, width);
        var y1 = Math.Clamp(approximate.Y1, 0, height - 1);
        var y2 = Math.Clamp(approximate.Y2, y1 + 1, height - 1);
        while (y1 > 0 && RowMean(gray, y1, x1, x2) > 32) y1--;
        while (y2 < height - 1 && RowMean(gray, y2, x1, x2) > 32) y2++;

        var checkTop = y1 + (y2 - y1) / 4;
        var checkBottom = y2 - (y2 - y1) / 4;
        if (checkBottom <= checkTop)
        {
            checkTop = y1;
            checkBottom = Math.Max(y1 + 1, y2);
        }

        var (left, right) = CardInsetX(gray, (x1 + x2) / 2, checkTop, checkBottom);
        return new BoundingBox(left, y1, right, y2).Clamp(width, height);
    }

    private static (int Left, int Right) CardInsetX(Mat gray, int middleX, int checkTop, int checkBottom)
    {
        var width = gray.Cols;
        var gapLeft = middleX;
        while (gapLeft > 0 && !IsDarkUniformColumn(gray, gapLeft, checkTop, checkBottom)) gapLeft--;
        var cardStart = gapLeft > 0 ? gapLeft + 1 : 0;
        var imageEdgeLeft = cardStart;
        for (var x = cardStart; x < middleX; x++)
        {
            if (IsDarkUniformColumn(gray, x, checkTop, checkBottom)) continue;
            imageEdgeLeft = x;
            break;
        }

        var gapRight = middleX;
        while (gapRight < width - 1 && !IsDarkUniformColumn(gray, gapRight, checkTop, checkBottom)) gapRight++;
        var cardEnd = gapRight < width - 1 ? gapRight - 1 : width - 1;
        var imageEdgeRight = cardEnd;
        for (var x = cardEnd; x > middleX; x--)
        {
            if (IsDarkUniformColumn(gray, x, checkTop, checkBottom)) continue;
            imageEdgeRight = x;
            break;
        }

        return (Math.Min(cardStart + 6, imageEdgeLeft), Math.Max(cardEnd - 6, imageEdgeRight));
    }

    private static bool IsDarkUniformColumn(Mat gray, int x, int top, int bottom)
    {
        using var column = new Mat(gray, new Rect(x, top, 1, Math.Max(1, bottom - top)));
        Cv2.MeanStdDev(column, out var mean, out var deviation);
        return mean.Val0 < 26 && deviation.Val0 * deviation.Val0 < 5;
    }

    private static double RowMean(Mat gray, int y, int left, int right)
    {
        using var row = new Mat(gray, new Rect(left, y, Math.Max(1, right - left), 1));
        return Cv2.Mean(row).Val0;
    }

    private static int FindOuterHorizontalEdge(Mat gray, Mat edges, int expected, int radius,
        int sampleTop, int sampleBottom, bool left)
    {
        var width = gray.Cols;
        var immediateOutside = left ? expected - 1 : expected;
        if (immediateOutside >= 0 && immediateOutside < width &&
            IsDarkUniformColumn(gray, immediateOutside, sampleTop, sampleBottom))
            return Math.Clamp(expected, 0, width);

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
