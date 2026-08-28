using System.Text.RegularExpressions;
using AutoMarkerReID.Application;
using AutoMarkerReID.Domain;

namespace AutoMarkerReID.Persistence;

public sealed partial class FileQueryRepository(
    StoragePaths paths,
    IImageCodec codec,
    IFeatureCache cache,
    IFileTrashService? trashService = null) : IQueryRepository
{
    public string RootPath => paths.Queries;

    public async Task<IReadOnlyList<QueryIdentity>> LoadAsync(CancellationToken cancellationToken)
    {
        paths.EnsureCreated();
        var queries = new List<QueryIdentity>();
        foreach (var directory in Directory.EnumerateDirectories(paths.Queries, "Query_*")
                     .Where(path => QueryNameRegex().IsMatch(Path.GetFileName(path)))
                     .OrderBy(NaturalQueryNumber))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var queryId = Path.GetFileName(directory);
            var references = new List<ReferenceImage>();
            foreach (var imagePath in Directory.EnumerateFiles(directory).Where(IsSupportedImage).Order(StringComparer.OrdinalIgnoreCase))
            {
                var cached = await cache.TryReadAsync(queryId, imagePath, cancellationToken).ConfigureAwait(false);
                if (cached is not null)
                {
                    references.Add(cached);
                }
                else
                {
                    references.Add(new ReferenceImage(
                        Path.GetFileNameWithoutExtension(imagePath),
                        queryId,
                        imagePath,
                        new Dictionary<string, float[]>(),
                        null,
                        null,
                        new DateTimeOffset(File.GetLastWriteTimeUtc(imagePath), TimeSpan.Zero)));
                }
            }

            queries.Add(new QueryIdentity(queryId, references, ThresholdCalibrator.Calibrate(references)));
        }

        return queries;
    }

    public Task<string> EnsureQueryAsync(string queryId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!QueryNameRegex().IsMatch(queryId))
        {
            throw new ArgumentException("Query phải có dạng Query_1 đến Query_999.", nameof(queryId));
        }

        var number = int.Parse(QueryNameRegex().Match(queryId).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (number is < 1 or > 999)
        {
            throw new ArgumentOutOfRangeException(nameof(queryId));
        }

        var fullPath = Path.Combine(paths.Queries, queryId);
        Directory.CreateDirectory(fullPath);
        return Task.FromResult(fullPath);
    }

    public async Task<string> AddReferenceAsync(string queryId, ImageFrame image, CancellationToken cancellationToken)
    {
        var queryPath = await EnsureQueryAsync(queryId, cancellationToken).ConfigureAwait(false);
        var fileName = $"capture_{DateTime.Now:yyyyMMdd_HHmmss_ffffff}.png";
        var destination = Path.Combine(queryPath, fileName);
        var temporary = destination + ".tmp";
        try
        {
            var encoded = codec.EncodePng(image);
            await File.WriteAllBytesAsync(temporary, encoded, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination);
            return destination;
        }
        catch
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }

            throw;
        }
    }

    public async Task DeleteScopeAsync(string? queryId, CancellationToken cancellationToken)
    {
        var directories = queryId is null
            ? Directory.EnumerateDirectories(paths.Queries, "Query_*")
            : [Path.Combine(paths.Queries, queryId)];
        foreach (var directory in directories.Where(Directory.Exists))
        {
            var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToArray();
            if (files.Length > 0 && trashService is not null)
            {
                await trashService.MoveToRecycleBinAsync(files, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Delete(file);
                }
            }
        }
    }

    public Task DeleteAllAsync(CancellationToken cancellationToken)
    {
        DeleteDirectoryContents(paths.Queries, cancellationToken);
        for (var index = 1; index <= 14; index++)
            Directory.CreateDirectory(Path.Combine(paths.Queries, $"Query_{index}"));
        return Task.CompletedTask;
    }

    private static int NaturalQueryNumber(string path) =>
        int.TryParse(QueryNameRegex().Match(Path.GetFileName(path)).Groups[1].Value, out var number) ? number : int.MaxValue;

    private static bool IsSupportedImage(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp";

    private static void DeleteDirectoryContents(string directory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(file);
        }

        foreach (var childDirectory in Directory.EnumerateDirectories(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Delete(childDirectory, recursive: true);
        }
    }

    [GeneratedRegex("^Query_([1-9][0-9]{0,2})$", RegexOptions.CultureInvariant)]
    private static partial Regex QueryNameRegex();
}
