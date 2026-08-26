using AutoMarkerReID.Application;
using AutoMarkerReID.Domain;
using AutoMarkerReID.Imaging;
using AutoMarkerReID.Inference;
using AutoMarkerReID.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoMarkerReID.Tests;

public sealed class QueryTests
{
    [Fact]
    public async Task RepositorySortsQueryNumbersNaturally()
    {
        var root = CreateRoot();
        try
        {
            var paths = new StoragePaths(root, Path.Combine(root, "models"));
            paths.EnsureCreated();
            var cache = new FileFeatureCache(paths);
            var repository = new FileQueryRepository(paths, new OpenCvImageCodec(), cache);
            var queries = await repository.LoadAsync(CancellationToken.None);
            Assert.True(queries.FindIndex(item => item.Id == "Query_2") < queries.FindIndex(item => item.Id == "Query_10"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task CollectorRejectsDuplicateAndUpdatesLiveCatalogImmediately()
    {
        var root = CreateRoot();
        try
        {
            var paths = new StoragePaths(root, Path.Combine(root, "models"));
            paths.EnsureCreated();
            var codec = new OpenCvImageCodec();
            var cache = new FileFeatureCache(paths);
            var repository = new FileQueryRepository(paths, codec, cache);
            var catalog = new QueryCatalog();
            catalog.Replace(await repository.LoadAsync(CancellationToken.None));
            var collector = new QueryCollector(repository, cache, codec, new FakeRuntime(), new FakeOcr(), catalog);
            var image = ImagingTests.Gradient(60, 140);

            var first = await collector.TryCollectAsync(image, "Query_2", CancellationToken.None);
            var duplicate = await collector.TryCollectAsync(image, "Query_2", CancellationToken.None);
            Assert.True(first.Accepted);
            Assert.False(duplicate.Accepted);
            Assert.Contains("trùng", duplicate.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Single(catalog.Snapshot["Query_2"].References);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DeleteQuerySendsReferenceFilesToTrashService()
    {
        var root = CreateRoot();
        try
        {
            var paths = new StoragePaths(root, Path.Combine(root, "models"));
            paths.EnsureCreated();
            var reference = Path.Combine(paths.Queries, "Query_4", "sample.png");
            await File.WriteAllBytesAsync(reference, [1, 2, 3]);
            var trash = new RecordingTrashService();
            var repository = new FileQueryRepository(paths, new OpenCvImageCodec(), new FileFeatureCache(paths), trash);

            await repository.DeleteScopeAsync("Query_4", CancellationToken.None);

            Assert.Contains(reference, trash.Paths, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DeleteAllPermanentlyClearsImagesAndCacheButKeepsQuerySlots()
    {
        var root = CreateRoot();
        try
        {
            var paths = new StoragePaths(root, Path.Combine(root, "models"));
            paths.EnsureCreated();
            var reference = Path.Combine(paths.Queries, "Query_4", "sample.png");
            var cache = Path.Combine(paths.Queries, "Query_4", ".cache", "sample.emb");
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            await File.WriteAllBytesAsync(reference, [1, 2, 3]);
            await File.WriteAllBytesAsync(cache, [4, 5, 6]);
            var repository = new FileQueryRepository(paths, new OpenCvImageCodec(), new FileFeatureCache(paths));

            await repository.DeleteAllAsync(CancellationToken.None);

            Assert.Empty(Directory.EnumerateFiles(paths.Queries, "*", SearchOption.AllDirectories));
            Assert.All(Enumerable.Range(1, 14), index => Assert.True(Directory.Exists(Path.Combine(paths.Queries, $"Query_{index}"))));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MatchEngineRejectsBodyMatchWhenNoFaceIsDetected()
    {
        var runtime = new FakeRuntime { HasVisibleFace = false };
        var catalog = new QueryCatalog();
        catalog.Replace([new QueryIdentity("Query_12", [new ReferenceImage(
            "reference", "Query_12", "reference.png",
            new Dictionary<string, float[]> { ["model"] = [1, 0, 0] },
            "7:42 AM", null, DateTimeOffset.UtcNow)], 0.65f)]);
        var engine = new OpenVinoMatchEngine(runtime, new OpenCvImageCodec(), new SingleCandidateGenerator(),
            new OpenCvBoxRenderer(), new FakeOcr(), catalog, new UserSelectionState(),
            NullLogger<OpenVinoMatchEngine>.Instance);

        var matches = await engine.MatchAsync(ImagingTests.Gradient(60, 140), "Query_12", CancellationToken.None);

        Assert.Empty(matches);
        Assert.Equal(1, runtime.FaceDetectionCalls);
        Assert.Equal(0, runtime.FaceEmbeddingCalls);
        Assert.Contains("không phát hiện khuôn mặt", Assert.Single(engine.LastExplanations).Reason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MatchEngineRejectsCardWhenTimestampCannotBeRead()
    {
        var runtime = new FakeRuntime();
        var catalog = new QueryCatalog();
        catalog.Replace([new QueryIdentity("Query_4", [new ReferenceImage(
            "reference", "Query_4", "reference.png",
            new Dictionary<string, float[]> { ["model"] = [1, 0, 0] },
            "12:17 PM", null, DateTimeOffset.UtcNow)], 0.65f)]);
        var engine = new OpenVinoMatchEngine(runtime, new OpenCvImageCodec(), new SingleCandidateGenerator(),
            new OpenCvBoxRenderer(), new FakeOcr(null), catalog, new UserSelectionState(),
            NullLogger<OpenVinoMatchEngine>.Instance);

        var matches = await engine.MatchAsync(ImagingTests.Gradient(60, 140), "Query_4", CancellationToken.None);

        Assert.Empty(matches);
        Assert.Equal(0, runtime.BodyEmbeddingCalls);
        Assert.Equal(0, runtime.FaceDetectionCalls);
        Assert.Contains("không đọc được timestamp", Assert.Single(engine.LastExplanations).Reason,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"automarker-queries-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeRuntime : IModelRuntime
    {
        public bool HasVisibleFace { get; init; } = true;
        public int BodyEmbeddingCalls { get; private set; }
        public int FaceDetectionCalls { get; private set; }
        public int FaceEmbeddingCalls { get; private set; }
        public bool IsAvailable => true;
        public IReadOnlyList<string> ActiveBodyModels => ["model"];
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyDictionary<string, float[]>> ExtractBodyEmbeddingsAsync(ImageFrame image, CancellationToken cancellationToken)
        {
            BodyEmbeddingCalls++;
            return Task.FromResult<IReadOnlyDictionary<string, float[]>>(new Dictionary<string, float[]> { ["model"] = [1, 0, 0] });
        }
        public Task<bool> HasVisibleFaceAsync(ImageFrame image, CancellationToken cancellationToken)
        {
            FaceDetectionCalls++;
            return Task.FromResult(HasVisibleFace);
        }
        public Task<float[]?> ExtractFaceEmbeddingAsync(ImageFrame image, CancellationToken cancellationToken)
        {
            FaceEmbeddingCalls++;
            return Task.FromResult<float[]?>(null);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SingleCandidateGenerator : ICandidateGenerator
    {
        public IReadOnlyList<CardCandidate> Generate(ImageFrame screenshot) =>
            [new(new BoundingBox(0, 0, screenshot.Width, screenshot.Height), 1, 0)];
    }

    private sealed class FakeOcr(string? timestamp = "7:42 AM") : IOcrService
    {
        public Task<string?> ReadTimestampAsync(ImageFrame card, CancellationToken cancellationToken) => Task.FromResult(timestamp);
    }

    private sealed class RecordingTrashService : IFileTrashService
    {
        public List<string> Paths { get; } = [];

        public Task MoveToRecycleBinAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
        {
            Paths.AddRange(paths);
            foreach (var path in paths) File.Delete(path);
            return Task.CompletedTask;
        }
    }
}

internal static class TestCollectionExtensions
{
    public static int FindIndex<T>(this IReadOnlyList<T> items, Predicate<T> predicate)
    {
        for (var index = 0; index < items.Count; index++) if (predicate(items[index])) return index;
        return -1;
    }
}
