using AutoMarkerReID.Application;
using AutoMarkerReID.Domain;
using AutoMarkerReID.Imaging;
using AutoMarkerReID.Inference;
using AutoMarkerReID.Persistence;

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

    private static string CreateRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"automarker-queries-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeRuntime : IModelRuntime
    {
        public bool IsAvailable => true;
        public IReadOnlyList<string> ActiveBodyModels => ["model"];
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyDictionary<string, float[]>> ExtractBodyEmbeddingsAsync(ImageFrame image, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, float[]>>(new Dictionary<string, float[]> { ["model"] = [1, 0, 0] });
        public Task<float[]?> ExtractFaceEmbeddingAsync(ImageFrame image, CancellationToken cancellationToken) => Task.FromResult<float[]?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeOcr : IOcrService
    {
        public Task<string?> ReadTimestampAsync(ImageFrame card, CancellationToken cancellationToken) => Task.FromResult<string?>("7:42 AM");
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
