using System.Text;
using AutoMarkerReID.Application;
using AutoMarkerReID.Domain;

namespace AutoMarkerReID.Persistence;

public sealed class FileFeatureCache(StoragePaths paths) : IFeatureCache
{
    private const string Magic = "AMREID01";

    public Task<ReferenceImage?> TryReadAsync(string queryId, string imagePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cachePath = GetCachePath(queryId, imagePath);
        if (!File.Exists(cachePath) || File.GetLastWriteTimeUtc(cachePath) < File.GetLastWriteTimeUtc(imagePath))
        {
            return Task.FromResult<ReferenceImage?>(null);
        }

        try
        {
            using var stream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            if (Encoding.ASCII.GetString(reader.ReadBytes(Magic.Length)) != Magic)
            {
                return Task.FromResult<ReferenceImage?>(null);
            }

            var storedPath = reader.ReadString();
            var ticks = reader.ReadInt64();
            var modelCount = reader.ReadInt32();
            var embeddings = new Dictionary<string, float[]>(modelCount, StringComparer.OrdinalIgnoreCase);
            for (var modelIndex = 0; modelIndex < modelCount; modelIndex++)
            {
                var modelName = reader.ReadString();
                var length = reader.ReadInt32();
                if (length is <= 0 or > 100_000)
                {
                    return Task.FromResult<ReferenceImage?>(null);
                }

                var values = new float[length];
                for (var index = 0; index < length; index++)
                {
                    values[index] = reader.ReadSingle();
                }

                embeddings[modelName] = values;
            }

            var timestamp = reader.ReadBoolean() ? reader.ReadString() : null;
            var appearanceLength = reader.ReadInt32();
            float[]? appearance = null;
            if (appearanceLength > 0)
            {
                appearance = new float[appearanceLength];
                for (var index = 0; index < appearance.Length; index++)
                {
                    appearance[index] = reader.ReadSingle();
                }
            }

            var id = Path.GetFileNameWithoutExtension(imagePath);
            return Task.FromResult<ReferenceImage?>(new ReferenceImage(
                id,
                queryId,
                string.IsNullOrWhiteSpace(storedPath) ? imagePath : storedPath,
                embeddings,
                timestamp,
                appearance,
                new DateTimeOffset(ticks, TimeSpan.Zero)));
        }
        catch (Exception exception) when (exception is IOException or EndOfStreamException or InvalidDataException)
        {
            return Task.FromResult<ReferenceImage?>(null);
        }
    }

    public Task WriteAsync(ReferenceImage reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cachePath = GetCachePath(reference.QueryId, reference.ImagePath);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var temporary = cachePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Encoding.ASCII.GetBytes(Magic));
                writer.Write(reference.ImagePath);
                writer.Write(reference.LastModified.UtcTicks);
                writer.Write(reference.Embeddings.Count);
                foreach (var embedding in reference.Embeddings.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
                {
                    writer.Write(embedding.Key);
                    writer.Write(embedding.Value.Length);
                    foreach (var value in embedding.Value)
                    {
                        writer.Write(value);
                    }
                }

                writer.Write(reference.Timestamp is not null);
                if (reference.Timestamp is not null)
                {
                    writer.Write(reference.Timestamp);
                }

                writer.Write(reference.AppearanceDescriptor?.Length ?? 0);
                if (reference.AppearanceDescriptor is not null)
                {
                    foreach (var value in reference.AppearanceDescriptor)
                    {
                        writer.Write(value);
                    }
                }

                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, cachePath, overwrite: true);
            File.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow);
            return Task.CompletedTask;
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    public Task DeleteAllAsync(CancellationToken cancellationToken)
    {
        foreach (var cacheDirectory in Directory.EnumerateDirectories(paths.Queries, ".cache", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Delete(cacheDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    public Task RemoveOrphansAsync(CancellationToken cancellationToken)
    {
        foreach (var cachePath in Directory.EnumerateFiles(paths.Queries, "*.emb", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cacheDirectory = Path.GetDirectoryName(cachePath)!;
            var queryDirectory = Directory.GetParent(cacheDirectory)?.FullName;
            if (queryDirectory is null)
            {
                continue;
            }

            var sourceName = Path.GetFileNameWithoutExtension(cachePath);
            var exists = Directory.EnumerateFiles(queryDirectory)
                .Where(IsSupportedImage)
                .Any(path => string.Equals(Path.GetFileNameWithoutExtension(path), sourceName, StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                File.Delete(cachePath);
            }
        }

        return Task.CompletedTask;
    }

    private string GetCachePath(string queryId, string imagePath) =>
        Path.Combine(paths.Queries, queryId, ".cache", Path.GetFileNameWithoutExtension(imagePath) + ".emb");

    private static bool IsSupportedImage(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
