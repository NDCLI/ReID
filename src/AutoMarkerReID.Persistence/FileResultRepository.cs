using System.Text.Json;
using System.Text.Json.Serialization;
using AutoMarkerReID.Application;
using AutoMarkerReID.Domain;

namespace AutoMarkerReID.Persistence;

public sealed class FileResultRepository(
    StoragePaths paths,
    IImageCodec codec,
    IBoxRenderer renderer,
    IFileTrashService trashService) : IResultRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string RootPath => paths.Output;

    public async Task<SavedResult> SaveAsync(ReviewSession session, CancellationToken cancellationToken)
    {
        var dominantQuery = session.Matches
            .GroupBy(match => match.QueryId, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenByDescending(group => group.Sum(match => match.Score))
            .Select(group => group.Key)
            .FirstOrDefault();
        var outputDirectory = Path.Combine(paths.Output, dominantQuery ?? "Unmatched");
        Directory.CreateDirectory(outputDirectory);
        var id = DateTime.Now.ToString("yyyyMMdd_HHmmss_ffffff", System.Globalization.CultureInfo.InvariantCulture);
        var originalPath = Path.Combine(outputDirectory, $"original_{id}.png");
        var markedPath = Path.Combine(outputDirectory, $"marked_{id}.png");
        var metadataPath = Path.Combine(outputDirectory, $"marked_{id}.json");
        var temporaryFiles = new[] { originalPath + ".tmp", markedPath + ".tmp", metadataPath + ".tmp" };
        var finalFiles = new[] { originalPath, markedPath, metadataPath };
        try
        {
            await WriteDurableAsync(temporaryFiles[0], codec.EncodePng(session.Original), cancellationToken).ConfigureAwait(false);
            var marked = renderer.Draw(session.Original, session.Matches);
            await WriteDurableAsync(temporaryFiles[1], codec.EncodePng(marked), cancellationToken).ConfigureAwait(false);
            var saved = new SavedResult(id, DateTimeOffset.Now, dominantQuery, originalPath, markedPath, session.Matches);
            await WriteDurableAsync(temporaryFiles[2], JsonSerializer.SerializeToUtf8Bytes(saved, JsonOptions), cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < temporaryFiles.Length; index++)
            {
                File.Move(temporaryFiles[index], finalFiles[index]);
            }

            return saved;
        }
        catch
        {
            foreach (var file in temporaryFiles.Concat(finalFiles))
            {
                TryDelete(file);
            }

            throw;
        }
    }

    public async Task<IReadOnlyList<SavedResult>> ListAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(paths.Output))
        {
            return [];
        }

        var results = new List<SavedResult>();
        var knownMarkedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var metadataPath in Directory.EnumerateFiles(paths.Output, "marked_*.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(metadataPath);
                var result = await JsonSerializer.DeserializeAsync<SavedResult>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
                if (result is not null && IsSafeOutputPath(result.OriginalImagePath) && IsSafeOutputPath(result.MarkedImagePath) &&
                    File.Exists(result.OriginalImagePath) && File.Exists(result.MarkedImagePath))
                {
                    results.Add(result);
                    knownMarkedFiles.Add(Path.GetFullPath(result.MarkedImagePath));
                }
            }
            catch (JsonException)
            {
            }
        }

        var imageExtensions = new HashSet<string>([".png", ".jpg", ".jpeg", ".bmp", ".webp"], StringComparer.OrdinalIgnoreCase);
        foreach (var imagePath in Directory.EnumerateFiles(paths.Output, "*", SearchOption.AllDirectories)
                     .Where(path => imageExtensions.Contains(Path.GetExtension(path)))
                     .Where(path => !Path.GetFileName(path).StartsWith("original_", StringComparison.OrdinalIgnoreCase)))
        {
            var fullPath = Path.GetFullPath(imagePath);
            if (knownMarkedFiles.Contains(fullPath)) continue;
            var info = new FileInfo(fullPath);
            results.Add(new SavedResult(
                Path.GetFileNameWithoutExtension(fullPath),
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                Directory.GetParent(fullPath)?.Name,
                string.Empty,
                fullPath,
                []));
        }

        return results.OrderByDescending(result => result.CreatedAt).ToArray();
    }

    public async Task UpdateMatchesAsync(SavedResult result, IReadOnlyList<MatchResult> matches, CancellationToken cancellationToken)
    {
        EnsureSafeResult(result);
        if (string.IsNullOrWhiteSpace(result.OriginalImagePath) || !File.Exists(result.OriginalImagePath))
            throw new InvalidOperationException("Kết quả legacy không có ảnh original/JSON nên không thể sửa khung.");
        var original = codec.Decode(await File.ReadAllBytesAsync(result.OriginalImagePath, cancellationToken).ConfigureAwait(false));
        var marked = renderer.Draw(original, matches);
        var metadataPath = Path.ChangeExtension(result.MarkedImagePath, ".json");
        var updated = result with { Matches = matches };
        await ReplaceDurableAsync(result.MarkedImagePath, codec.EncodePng(marked), cancellationToken).ConfigureAwait(false);
        await ReplaceDurableAsync(metadataPath, JsonSerializer.SerializeToUtf8Bytes(updated, JsonOptions), cancellationToken).ConfigureAwait(false);
    }

    public Task MoveToRecycleBinAsync(SavedResult result, CancellationToken cancellationToken)
    {
        if (!IsSafeOutputPath(result.MarkedImagePath)) throw new InvalidDataException("Ảnh nằm ngoài thư mục output.");
        if (string.IsNullOrWhiteSpace(result.OriginalImagePath))
            return trashService.MoveToRecycleBinAsync([result.MarkedImagePath], cancellationToken);
        EnsureSafeResult(result);
        var metadataPath = Path.ChangeExtension(result.MarkedImagePath, ".json");
        IReadOnlyCollection<string> files = [result.OriginalImagePath, result.MarkedImagePath, metadataPath];
        return trashService.MoveToRecycleBinAsync(files, cancellationToken);
    }

    public Task DeleteAllAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.Output);
        foreach (var file in Directory.EnumerateFiles(paths.Output))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(file);
        }

        foreach (var directory in Directory.EnumerateDirectories(paths.Output))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static async Task WriteDurableAsync(string path, byte[] contents, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task ReplaceDurableAsync(string path, byte[] contents, CancellationToken cancellationToken)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await WriteDurableAsync(temporary, contents, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private void EnsureSafeResult(SavedResult result)
    {
        if (!IsSafeOutputPath(result.OriginalImagePath) || !IsSafeOutputPath(result.MarkedImagePath))
        {
            throw new InvalidDataException("Metadata trỏ ra ngoài thư mục output.");
        }
    }

    private bool IsSafeOutputPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var root = Path.GetFullPath(paths.Output).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(path);
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

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
