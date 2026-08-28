using AutoMarkerReID.Application;
using AutoMarkerReID.Domain;
using AutoMarkerReID.Imaging;

namespace AutoMarkerReID.Inference;

public sealed class QueryCollector(
    IQueryRepository repository,
    IFeatureCache cache,
    IImageCodec codec,
    IModelRuntime runtime,
    IOcrService ocr,
    QueryCatalog catalog) : IQueryCollector
{
    public async Task<QueryCollectionResult> TryCollectAsync(ImageFrame image, string targetQueryId, CancellationToken cancellationToken)
    {
        var validation = CropValidator.Validate(image);
        if (!validation.IsValid)
        {
            return new(false, validation.Reason);
        }

        var fingerprint = DuplicateHasher.Compute(image);
        var queryPath = await repository.EnsureQueryAsync(targetQueryId, cancellationToken).ConfigureAwait(false);
        foreach (var existingPath in Directory.EnumerateFiles(queryPath).Where(IsSupportedImage))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var existing = codec.Decode(await File.ReadAllBytesAsync(existingPath, cancellationToken).ConfigureAwait(false));
                var other = DuplicateHasher.Compute(existing);
                if (fingerprint.ExactSha256 == other.ExactSha256 ||
                    DuplicateHasher.HammingDistance(fingerprint.PerceptualHash, other.PerceptualHash) <= 5 ||
                    DuplicateHasher.HammingDistance(fingerprint.DifferenceHash, other.DifferenceHash) <= 5)
                {
                    return new(false, $"ảnh bị trùng hoặc quá giống {Path.GetFileName(existingPath)}");
                }
            }
            catch (InvalidDataException)
            {
            }
        }

        var imagePath = await repository.AddReferenceAsync(targetQueryId, image, cancellationToken).ConfigureAwait(false);
        try
        {
            var embeddings = new Dictionary<string, float[]>(
                await runtime.ExtractBodyEmbeddingsAsync(image, cancellationToken).ConfigureAwait(false),
                StringComparer.OrdinalIgnoreCase);

            var reference = new ReferenceImage(
                Path.GetFileNameWithoutExtension(imagePath),
                targetQueryId,
                imagePath,
                embeddings,
                await ocr.ReadTimestampAsync(image, cancellationToken).ConfigureAwait(false),
                LbpDescriptor.Create(image),
                new DateTimeOffset(File.GetLastWriteTimeUtc(imagePath), TimeSpan.Zero));
            await cache.WriteAsync(reference, cancellationToken).ConfigureAwait(false);
            var queries = catalog.Snapshot.Values.ToList();
            var queryIndex = queries.FindIndex(query => query.Id.Equals(targetQueryId, StringComparison.OrdinalIgnoreCase));
            if (queryIndex >= 0)
            {
                // Adding a reference changes the intra-Query similarity distribution,
                // so the threshold has to be refitted or it stays pinned to whatever
                // the Query looked like at its previous size.
                var updatedReferences = queries[queryIndex].References.Append(reference).ToArray();
                queries[queryIndex] = queries[queryIndex] with
                {
                    References = updatedReferences,
                    CalibratedThreshold = ThresholdCalibrator.Calibrate(updatedReferences),
                };
            }
            else
            {
                queries.Add(new QueryIdentity(targetQueryId, [reference], ThresholdCalibrator.Calibrate([reference])));
            }

            catalog.Replace(queries);
            return new(true, "đã thêm", imagePath);
        }
        catch
        {
            File.Delete(imagePath);
            throw;
        }
    }

    private static bool IsSupportedImage(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp";
}
