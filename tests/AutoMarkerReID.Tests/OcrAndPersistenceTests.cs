using AutoMarkerReID.Application;
using AutoMarkerReID.Domain;
using AutoMarkerReID.Imaging;
using AutoMarkerReID.Inference;
using AutoMarkerReID.Persistence;

namespace AutoMarkerReID.Tests;

public sealed class OcrAndPersistenceTests
{
    [Theory]
    [InlineData("7:42 am", "7:42 AM")]
    [InlineData("Captured 11.05 P.M.", "11:05 PM")]
    [InlineData("Ｏ9：03 A M", "9:03 AM")]
    public void TimestampNormalizationAcceptsCommonOcrVariants(string input, string expected) =>
        Assert.Equal(expected, OpenVinoOcrService.NormalizeTimestamp(input));

    [Theory]
    [InlineData("13:12 PM")]
    [InlineData("9:99 AM")]
    [InlineData("no time")]
    public void TimestampNormalizationRejectsInvalidText(string input) =>
        Assert.Null(OpenVinoOcrService.NormalizeTimestamp(input));

    [Fact]
    public async Task ResultRepositorySavesLoadsUpdatesAndUsesRecycleService()
    {
        var root = Path.Combine(Path.GetTempPath(), $"automarker-results-{Guid.NewGuid():N}");
        try
        {
            var paths = new StoragePaths(root, Path.Combine(root, "models"));
            paths.EnsureCreated();
            var codec = new OpenCvImageCodec();
            var renderer = new OpenCvBoxRenderer();
            var trash = new RecordingTrash();
            var repository = new FileResultRepository(paths, codec, renderer, trash);
            var image = ImagingTests.Gradient(800, 450);
            var match = Match(new BoundingBox(200, 50, 300, 250));
            var session = new ReviewSession(Guid.NewGuid(), image, [match], DateTimeOffset.UtcNow, ImageJobSource.File);

            var saved = await repository.SaveAsync(session, CancellationToken.None);
            Assert.True(File.Exists(saved.OriginalImagePath));
            Assert.True(File.Exists(saved.MarkedImagePath));
            Assert.True(File.Exists(Path.ChangeExtension(saved.MarkedImagePath, ".json")));
            var loaded = Assert.Single(await repository.ListAsync(CancellationToken.None));
            Assert.Equal(saved.Id, loaded.Id);

            var updated = Match(new BoundingBox(400, 70, 500, 270)) with { ManuallyEdited = true };
            await repository.UpdateMatchesAsync(loaded, [updated], CancellationToken.None);
            var reloaded = Assert.Single(await repository.ListAsync(CancellationToken.None));
            Assert.Equal(updated.BoundingBox, Assert.Single(reloaded.Matches).BoundingBox);
            await repository.MoveToRecycleBinAsync(reloaded, CancellationToken.None);
            Assert.Equal(3, trash.Paths.Count);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static MatchResult Match(BoundingBox box) => new(
        "Query_1", "reference", box, 0.9f, 0.1f, 0.92f, 1f,
        new Dictionary<string, float> { ["osnet"] = 0.9f }, "7:42 AM", MatchSource.Body);

    private sealed class RecordingTrash : IFileTrashService
    {
        public List<string> Paths { get; } = [];
        public Task MoveToRecycleBinAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Paths.AddRange(paths);
            return Task.CompletedTask;
        }
    }
}
