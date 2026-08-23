using AutoMarkerReID.Domain;
using AutoMarkerReID.Imaging;

namespace AutoMarkerReID.Tests;

public sealed class ImagingTests
{
    [Fact]
    public void CropValidationHashesAndLbpAreStableForValidPortrait()
    {
        var image = Gradient(60, 140);
        Assert.True(CropValidator.Validate(image).IsValid);
        var first = DuplicateHasher.Compute(image);
        var second = DuplicateHasher.Compute(image with { Pixels = [.. image.Pixels] });
        Assert.Equal(first, second);
        Assert.Equal(0, DuplicateHasher.HammingDistance(first.PerceptualHash, second.PerceptualHash));
        var descriptor = LbpDescriptor.Create(image);
        Assert.NotNull(descriptor);
        Assert.Equal(512, descriptor.Length);
        Assert.InRange(LbpDescriptor.Similarity(descriptor, descriptor), 0.999f, 1.0f);
    }

    [Fact]
    public void CropValidatorRejectsFlatAndLandscapeImages()
    {
        Assert.False(CropValidator.Validate(Solid(60, 140, 128)).IsValid);
        Assert.False(CropValidator.Validate(Gradient(200, 100)).IsValid);
    }

    [Fact]
    public void EditorOperationsCutStripsAndMergeWithoutResampling()
    {
        var image = Gradient(60, 140);
        var cutVertical = ImageEditorOperations.CutOut(image, new BoundingBox(10, 20, 20, 25));
        var cutHorizontal = ImageEditorOperations.CutOut(image, new BoundingBox(10, 20, 15, 40));
        var merged = ImageEditorOperations.Merge(image, image, false);
        Assert.Equal(50, cutVertical.Width);
        Assert.Equal(120, cutHorizontal.Height);
        Assert.Equal(120, merged.Width);
        Assert.Equal(140, merged.Height);
    }

    [Fact]
    public void CutOutUsesDragDirectionInsteadOfSelectionAspectRatio()
    {
        var image = Gradient(60, 140);

        // A wide selection made with a vertical drag still removes a row strip.
        var horizontalDrag = ImageEditorOperations.CutOut(
            image, new BoundingBox(10, 20, 40, 25), removeVerticalStrip: false);
        // A tall selection made with a horizontal drag still removes a column strip.
        var verticalDrag = ImageEditorOperations.CutOut(
            image, new BoundingBox(10, 20, 15, 60), removeVerticalStrip: true);

        Assert.Equal(60, horizontalDrag.Width);
        Assert.Equal(135, horizontalDrag.Height);
        Assert.Equal(55, verticalDrag.Width);
        Assert.Equal(140, verticalDrag.Height);
    }

    [Fact]
    public void SnapToCardPreservesOuterCardFrameAndClampsToScreenshot()
    {
        var image = CardGrid();
        var renderer = new OpenCvBoxRenderer();

        var cardFrame = new BoundingBox(30, 20, 90, 190);
        Assert.Equal(cardFrame, renderer.SnapToCard(image, cardFrame));
        Assert.Equal(new BoundingBox(0, 0, 220, 215),
            renderer.SnapToCard(image, new BoundingBox(-5, -10, 225, 230)));
    }

    [Fact]
    public void SnapToCardExpandsInnerCameraEdgesToGrayCardFrame()
    {
        var image = FramedCardGrid();
        var renderer = new OpenCvBoxRenderer();

        var snapped = renderer.SnapToCard(image, new BoundingBox(115, 45, 165, 195));
        Assert.InRange(snapped.X1, 108, 111);
        Assert.InRange(snapped.X2, 168, 171);
        Assert.Equal(45, snapped.Y1);
        Assert.Equal(195, snapped.Y2);
        Assert.Equal(snapped, renderer.SnapToCard(image, snapped));
    }

    [Fact]
    public void CandidateGeneratorUsesGrayOuterFrameInsteadOfInnerCameraImage()
    {
        var candidates = new OpenCvCandidateGenerator().Generate(FramedCardGrid());

        Assert.True(candidates.Count >= 3);
        foreach (var expectedLeft in new[] { 110, 200, 290 })
        {
            var candidate = candidates.MinBy(item => Math.Abs(item.BoundingBox.X1 - expectedLeft));
            Assert.InRange(candidate!.BoundingBox.X1, expectedLeft - 2, expectedLeft + 2);
            Assert.InRange(candidate.BoundingBox.X2, expectedLeft + 58, expectedLeft + 62);
            Assert.InRange(candidate.BoundingBox.Y1, 38, 42);
            Assert.InRange(candidate.BoundingBox.Y2, 198, 202);
        }
    }

    [Fact]
    public void CandidateGeneratorKeepsAllThreeVisibleRows()
    {
        var candidates = new OpenCvCandidateGenerator().Generate(ThreeRowCardGrid());

        Assert.True(candidates.Count == 12, string.Join(" | ", candidates.Select(candidate =>
            $"r{candidate.Row}:{candidate.BoundingBox.X1}-{candidate.BoundingBox.X2}")));
        Assert.Collection(candidates.Select(candidate => candidate.Row).Distinct().Order(),
            row => Assert.Equal(0, row), row => Assert.Equal(1, row), row => Assert.Equal(2, row));
        Assert.All(candidates.GroupBy(candidate => candidate.Row), row => Assert.Equal(4, row.Count()));
    }

    internal static ImageFrame Gradient(int width, int height)
    {
        var pixels = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = (y * width + x) * 3;
            pixels[offset] = (byte)((x * 7 + y * 3) % 256);
            pixels[offset + 1] = (byte)((x * 11 + y * 5) % 256);
            pixels[offset + 2] = (byte)((x * 13 + y * 17) % 256);
        }
        return new ImageFrame(width, height, width * 3, ImagePixelFormat.Bgr24, pixels);
    }

    private static ImageFrame Solid(int width, int height, byte value) =>
        new(width, height, width * 3, ImagePixelFormat.Bgr24, Enumerable.Repeat(value, width * height * 3).ToArray());

    private static ImageFrame CardGrid()
    {
        const int width = 220;
        const int height = 215;
        var pixels = Enumerable.Repeat((byte)18, width * height * 3).ToArray();
        Fill(30, 20, 90, 190, 165);
        Fill(105, 20, 165, 190, 145);
        Fill(52, 48, 70, 166, 65);
        Fill(118, 55, 151, 168, 80);
        return new ImageFrame(width, height, width * 3, ImagePixelFormat.Bgr24, pixels);

        void Fill(int x1, int y1, int x2, int y2, byte value)
        {
            for (var y = y1; y < y2; y++)
            for (var x = x1; x < x2; x++)
            {
                var offset = (y * width + x) * 3;
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
            }
        }
    }

    private static ImageFrame FramedCardGrid()
    {
        const int width = 400;
        const int height = 320;
        var pixels = Enumerable.Repeat((byte)18, width * height * 3).ToArray();
        foreach (var left in new[] { 110, 200, 290 })
        {
            Fill(left, 40, left + 60, 200, 72);      // gray outer card/frame
            Fill(left + 5, 45, left + 55, 195, 165); // camera image
            Fill(left + 20, 65, left + 40, 178, 55); // strong inner subject edges
        }
        return new ImageFrame(width, height, width * 3, ImagePixelFormat.Bgr24, pixels);

        void Fill(int x1, int y1, int x2, int y2, byte value)
        {
            for (var y = y1; y < y2; y++)
            for (var x = x1; x < x2; x++)
            {
                var offset = (y * width + x) * 3;
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
            }
        }
    }

    private static ImageFrame ThreeRowCardGrid()
    {
        const int width = 700;
        const int height = 600;
        var pixels = Enumerable.Repeat((byte)18, width * height * 3).ToArray();
        foreach (var top in new[] { 50, 230, 410 })
        foreach (var left in new[] { 220, 310, 400, 490 })
        {
            Fill(left, top, left + 60, top + 140, 72);
            Fill(left + 5, top + 3, left + 55, top + 137, 165);
            Fill(left + 20, top + 25, left + 40, top + 120, 55);
        }
        Fill(180, 230, 230, 370, 72); // heading joined to the first card; recover that grid slot
        Fill(570, 230, 690, 370, 72); // unrelated wide row noise must not become a card
        return new ImageFrame(width, height, width * 3, ImagePixelFormat.Bgr24, pixels);

        void Fill(int x1, int y1, int x2, int y2, byte value)
        {
            for (var y = y1; y < y2; y++)
            for (var x = x1; x < x2; x++)
            {
                var offset = (y * width + x) * 3;
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
            }
        }
    }
}
