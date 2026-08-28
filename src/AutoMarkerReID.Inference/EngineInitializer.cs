using AutoMarkerReID.Application;
using AutoMarkerReID.Domain;
using AutoMarkerReID.Imaging;
using Microsoft.Extensions.Logging;

namespace AutoMarkerReID.Inference;

public sealed class EngineInitializer(
    IModelRuntime runtime,
    IQueryRepository queryRepository,
    IFeatureCache cache,
    IImageCodec codec,
    IOcrService ocr,
    QueryCatalog catalog,
    ILogger<EngineInitializer> logger) : IEngineInitializer
{
    public bool IsReady { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await runtime.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await ocr.WarmupAsync(cancellationToken).ConfigureAwait(false);
        await cache.RemoveOrphansAsync(cancellationToken).ConfigureAwait(false);
        await BuildMissingReferencesAsync(progress: null, logProgress: false, cancellationToken).ConfigureAwait(false);
        IsReady = runtime.IsAvailable;
    }

    public async Task RebuildCacheAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        IsReady = false;
        EngineInitializerLog.CacheRebuildStarted(logger);
        try
        {
            await cache.DeleteAllAsync(cancellationToken).ConfigureAwait(false);
            EngineInitializerLog.CacheCleared(logger);
            await BuildMissingReferencesAsync(progress, logProgress: true, cancellationToken).ConfigureAwait(false);
            IsReady = runtime.IsAvailable;
            EngineInitializerLog.CacheRebuildCompleted(logger);
        }
        catch (Exception exception)
        {
            EngineInitializerLog.CacheRebuildFailed(logger, exception);
            throw;
        }
    }

    private async Task BuildMissingReferencesAsync(IProgress<double>? progress, bool logProgress, CancellationToken cancellationToken)
    {
        var queries = await queryRepository.LoadAsync(cancellationToken).ConfigureAwait(false);
        var total = Math.Max(1, queries.Sum(query => query.References.Count));
        var completed = 0;
        if (logProgress)
        {
            EngineInitializerLog.ReferenceRebuildStarted(logger, total);
        }
        var rebuilt = new List<QueryIdentity>(queries.Count);
        foreach (var query in queries)
        {
            var references = new List<ReferenceImage>(query.References.Count);
            foreach (var reference in query.References)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resolved = reference;
                if (reference.Embeddings.Count == 0)
                {
                    try
                    {
                        var image = codec.Decode(await File.ReadAllBytesAsync(reference.ImagePath, cancellationToken).ConfigureAwait(false));
                        var embeddings = new Dictionary<string, float[]>(
                            await runtime.ExtractBodyEmbeddingsAsync(image, cancellationToken).ConfigureAwait(false),
                            StringComparer.OrdinalIgnoreCase);

                        resolved = reference with
                        {
                            Embeddings = embeddings,
                            Timestamp = await ocr.ReadTimestampAsync(image, cancellationToken).ConfigureAwait(false),
                            AppearanceDescriptor = LbpDescriptor.Create(image),
                            LastModified = new DateTimeOffset(File.GetLastWriteTimeUtc(reference.ImagePath), TimeSpan.Zero),
                        };
                        await cache.WriteAsync(resolved, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        EngineInitializerLog.ReferenceBuildFailed(logger, reference.ImagePath, exception);
                    }
                }

                if (resolved.Embeddings.Count > 0)
                {
                    references.Add(resolved);
                }

                completed++;
                progress?.Report((double)completed / total);
                if (logProgress)
                {
                    EngineInitializerLog.ReferenceRebuilt(logger, query.Id, completed, total);
                }
            }

            rebuilt.Add(query with { References = references, CalibratedThreshold = ThresholdCalibrator.Calibrate(references) });
        }

        catalog.Replace(rebuilt);
    }
}

internal static partial class EngineInitializerLog
{
    [LoggerMessage(EventId = 3100, Level = LogLevel.Error, Message = "Không thể tạo dữ liệu AI cho ảnh tham chiếu {path}.")]
    public static partial void ReferenceBuildFailed(ILogger logger, string path, Exception exception);

    [LoggerMessage(EventId = 3101, Level = LogLevel.Information, Message = "Bắt đầu tạo lại dữ liệu AI và OCR.")]
    public static partial void CacheRebuildStarted(ILogger logger);

    [LoggerMessage(EventId = 3102, Level = LogLevel.Information, Message = "Đã xóa cache cũ; đang tạo lại dữ liệu AI và OCR.")]
    public static partial void CacheCleared(ILogger logger);

    [LoggerMessage(EventId = 3103, Level = LogLevel.Information, Message = "Cần tạo lại AI/OCR cho {total} ảnh tham chiếu.")]
    public static partial void ReferenceRebuildStarted(ILogger logger, int total);

    [LoggerMessage(EventId = 3104, Level = LogLevel.Information, Message = "Đã tạo AI/OCR cho {queryId}: {completed}/{total} ảnh.")]
    public static partial void ReferenceRebuilt(ILogger logger, string queryId, int completed, int total);

    [LoggerMessage(EventId = 3105, Level = LogLevel.Information, Message = "Đã hoàn tất tạo lại dữ liệu AI và OCR.")]
    public static partial void CacheRebuildCompleted(ILogger logger);

    [LoggerMessage(EventId = 3106, Level = LogLevel.Error, Message = "Tạo lại dữ liệu AI và OCR không thành công.")]
    public static partial void CacheRebuildFailed(ILogger logger, Exception exception);
}
