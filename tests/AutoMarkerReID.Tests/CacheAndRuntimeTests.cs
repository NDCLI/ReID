using AutoMarkerReID.Domain;
using AutoMarkerReID.Inference;
using AutoMarkerReID.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoMarkerReID.Tests;

public sealed class CacheAndRuntimeTests
{
    [Fact]
    public async Task FeatureCacheRoundTripsAndRejectsStaleEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), $"automarker-tests-{Guid.NewGuid():N}");
        try
        {
            var paths = new StoragePaths(root, Path.Combine(root, "models"));
            paths.EnsureCreated();
            var imagePath = Path.Combine(paths.Queries, "Query_1", "reference.png");
            await File.WriteAllBytesAsync(imagePath, [1, 2, 3]);
            var cache = new FileFeatureCache(paths);
            var reference = new ReferenceImage("reference", "Query_1", imagePath,
                new Dictionary<string, float[]> { ["model"] = [0.1f, 0.2f, 0.3f] }, "12:34",
                Enumerable.Repeat(1f / 512, 512).ToArray(), new DateTimeOffset(File.GetLastWriteTimeUtc(imagePath), TimeSpan.Zero));
            await cache.WriteAsync(reference, CancellationToken.None);
            var loaded = await cache.TryReadAsync("Query_1", imagePath, CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.Equal(reference.Timestamp, loaded.Timestamp);
            Assert.Equal(reference.Embeddings["model"], loaded.Embeddings["model"]);

            File.SetLastWriteTimeUtc(imagePath, DateTime.UtcNow.AddMinutes(1));
            Assert.Null(await cache.TryReadAsync("Query_1", imagePath, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task OpenVinoRuntimeLoadsThreeBodyModelsAndReturnsNormalizedEmbeddings()
    {
        var root = FindRepositoryRoot();
        await using var runtime = new OpenVinoModelRuntime(
            new ModelLocations(Path.Combine(root, "assets", "models")),
            NullLogger<OpenVinoModelRuntime>.Instance);
        await runtime.InitializeAsync(CancellationToken.None);
        Assert.Equal(3, runtime.ActiveBodyModels.Count);
        var embeddings = await runtime.ExtractBodyEmbeddingsAsync(ImagingTests.Gradient(64, 140), CancellationToken.None);
        Assert.Equal(3, embeddings.Count);
        foreach (var embedding in embeddings.Values)
        {
            Assert.NotEmpty(embedding);
            var norm = Math.Sqrt(embedding.Sum(value => value * value));
            Assert.InRange(norm, 0.999, 1.001);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "assets", "models", "reid.xml"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("Không tìm thấy assets/models/reid.xml từ test output.");
    }
}
