using AutoMarkerReID.Application;
using AutoMarkerReID.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoMarkerReID.Tests;

public sealed class DirectCaptureCleanupTests
{
    public static TheoryData<ImageJobSource, bool> Sources => new()
    {
        { ImageJobSource.NewCapture, true },
        { ImageJobSource.RepeatCapture, true },
        { ImageJobSource.Clipboard, false },
        { ImageJobSource.File, false },
        { ImageJobSource.CommandLine, false },
    };

    [Theory]
    [MemberData(nameof(Sources))]
    public void DeletesOnlyCollectedDirectCaptureSources(ImageJobSource source, bool expected)
    {
        var job = ImageJob.Create(Image(), source, "capture.png");
        var result = new ProcessingResult.QueryCollected("Query_1", "reference.png");

        Assert.Equal(expected, DirectCaptureCleanupPolicy.ShouldDeleteSavedCopy(job, result));
    }

    [Fact]
    public void KeepsRejectedAndReIdDirectCaptures()
    {
        var job = ImageJob.Create(Image(), ImageJobSource.NewCapture, "capture.png");
        var rejected = new ProcessingResult.Ignored("rejected");
        var reId = new ProcessingResult.ReviewRequired(new ReviewSession(
            Guid.NewGuid(), Image(), [], DateTimeOffset.UtcNow, ImageJobSource.NewCapture));

        Assert.False(DirectCaptureCleanupPolicy.ShouldDeleteSavedCopy(job, rejected));
        Assert.False(DirectCaptureCleanupPolicy.ShouldDeleteSavedCopy(job, reId));
    }

    [Fact]
    public void DeletesExactSavedCopyAndKeepsOtherFiles()
    {
        var root = CreateRoot();
        try
        {
            var sourcePath = Path.Combine(root, "capture.png");
            var otherPath = Path.Combine(root, "other.png");
            File.WriteAllBytes(sourcePath, [1]);
            File.WriteAllBytes(otherPath, [2]);
            var job = ImageJob.Create(Image(), ImageJobSource.RepeatCapture, sourcePath);

            DirectCaptureCleanupPolicy.TryDeleteSavedCopy(job, new ProcessingResult.QueryCollected("Query_1", "reference.png"), NullLogger.Instance);

            Assert.False(File.Exists(sourcePath));
            Assert.True(File.Exists(otherPath));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void CleanupFailureDoesNotEscape()
    {
        var root = CreateRoot();
        try
        {
            var job = ImageJob.Create(Image(), ImageJobSource.NewCapture, root);

            var exception = Record.Exception(() => DirectCaptureCleanupPolicy.TryDeleteSavedCopy(
                job,
                new ProcessingResult.QueryCollected("Query_1", "reference.png"),
                NullLogger.Instance));

            Assert.Null(exception);
            Assert.True(Directory.Exists(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static ImageFrame Image() => new(1, 1, 3, ImagePixelFormat.Bgr24, [0, 0, 0]);

    private static string CreateRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"automarker-cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
