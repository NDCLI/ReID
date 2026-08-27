using AutoMarkerReID.Application;
using AutoMarkerReID.Domain;
using AutoMarkerReID.Imaging;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using OpenVinoSharp;

namespace AutoMarkerReID.Inference;

public sealed class OpenVinoModelRuntime(ModelLocations locations, ILogger<OpenVinoModelRuntime> logger) : IModelRuntime
{
    private readonly Dictionary<string, LoadedModel> _bodyModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private Core? _core;
    private LoadedModel? _faceDetector;

    public bool IsAvailable => _bodyModels.Count > 0;
    public IReadOnlyList<string> ActiveBodyModels => _bodyModels.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _core ??= new Core();
        foreach (var definition in locations.BodyModels)
        {
            if (!File.Exists(definition.Path))
            {
                OpenVinoRuntimeLog.ModelMissing(logger, definition.Name);
                continue;
            }

            try
            {
                var compiled = _core.compile_model_unicode(definition.Path, "CPU");
                _bodyModels[definition.Name] = new LoadedModel(definition.Name, compiled, definition.InputHeight, definition.InputWidth);
                OpenVinoRuntimeLog.ModelLoaded(logger, definition.Name, definition.Path);
            }
            catch (Exception exception)
            {
                OpenVinoRuntimeLog.ModelLoadFailed(logger, definition.Name, exception);
            }
        }

        _faceDetector = TryLoad("face_detector", locations.FaceDetection, 300, 300);
        if (!IsAvailable)
        {
            throw new InvalidOperationException("Không có OSNet body model nào tải thành công.");
        }

        return Task.CompletedTask;
    }

    public async Task<IReadOnlyDictionary<string, float[]>> ExtractBodyEmbeddingsAsync(ImageFrame image, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await _inferenceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = new Dictionary<string, float[]>(_bodyModels.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var model in _bodyModels.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    result[model.Name] = InferEmbedding(model, image);
                }
                catch (Exception exception)
                {
                    OpenVinoRuntimeLog.InferenceFailed(logger, model.Name, exception);
                }
            }

            return result;
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    public async Task<bool> HasVisibleFaceAsync(ImageFrame image, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        if (_faceDetector is null)
        {
            return false;
        }

        await _inferenceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return DetectBestFace(image) is not null;
        }
        catch (Exception exception)
        {
            OpenVinoRuntimeLog.InferenceFailed(logger, "face_detector", exception);
            return false;
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        foreach (var model in _bodyModels.Values)
        {
            model.Dispose();
        }

        _bodyModels.Clear();
        _faceDetector?.Dispose();
        _core?.Dispose();
        _inferenceLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private LoadedModel? TryLoad(string name, string path, int height, int width)
    {
        if (!File.Exists(path))
        {
            OpenVinoRuntimeLog.ModelMissing(logger, name);
            return null;
        }

        try
        {
            var compiled = _core!.compile_model_unicode(path, "CPU");
            OpenVinoRuntimeLog.ModelLoaded(logger, name, path);
            return new LoadedModel(name, compiled, height, width);
        }
        catch (Exception exception)
        {
            OpenVinoRuntimeLog.ModelLoadFailed(logger, name, exception);
            return null;
        }
    }

    private static float[] InferEmbedding(LoadedModel model, ImageFrame image)
    {
        var output = InferRaw(model, image);
        double sumSquares = 0;
        for (var index = 0; index < output.Length; index++)
        {
            sumSquares += output[index] * output[index];
        }

        var norm = Math.Sqrt(sumSquares);
        if (norm <= 1e-12)
        {
            return output;
        }

        for (var index = 0; index < output.Length; index++)
        {
            output[index] = (float)(output[index] / norm);
        }

        return output;
    }

    private BoundingBox? DetectBestFace(ImageFrame image)
    {
        var detections = InferRaw(_faceDetector!, image);
        BoundingBox? bestFace = null;
        var bestConfidence = 0f;
        for (var offset = 0; offset + 6 < detections.Length; offset += 7)
        {
            var confidence = detections[offset + 2];
            if (confidence < ReIdDefaults.FaceDetectionThreshold || confidence <= bestConfidence)
            {
                continue;
            }

            var box = new BoundingBox(
                (int)Math.Round(detections[offset + 3] * image.Width),
                (int)Math.Round(detections[offset + 4] * image.Height),
                (int)Math.Round(detections[offset + 5] * image.Width),
                (int)Math.Round(detections[offset + 6] * image.Height)).Clamp(image.Width, image.Height);
            if (box.Area > 0)
            {
                bestFace = box;
                bestConfidence = confidence;
            }
        }

        return bestFace;
    }

    private static float[] InferRaw(LoadedModel model, ImageFrame image)
    {
        var input = PrepareNchwBgr(image, model.InputHeight, model.InputWidth);
        using var shape = Shape.nchw(1, 3, model.InputHeight, model.InputWidth);
        using var tensor = new Tensor(shape, input);
        using var request = model.Compiled.create_infer_request();
        request.set_input_tensor(tensor);
        request.infer();
        using var output = request.get_output_tensor();
        return output.get_float_data();
    }

    private static float[] PrepareNchwBgr(ImageFrame image, int height, int width)
    {
        using var source = MatConversion.ToMat(image);
        using var bgr = new Mat();
        if (source.Channels() == 4)
        {
            Cv2.CvtColor(source, bgr, ColorConversionCodes.BGRA2BGR);
        }
        else if (source.Channels() == 1)
        {
            Cv2.CvtColor(source, bgr, ColorConversionCodes.GRAY2BGR);
        }
        else
        {
            source.CopyTo(bgr);
        }

        using var resized = new Mat();
        Cv2.Resize(bgr, resized, new Size(width, height), interpolation: InterpolationFlags.Linear);
        var planeSize = checked(width * height);
        var input = new float[checked(planeSize * 3)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pixel = resized.At<Vec3b>(y, x);
                var offset = (y * width) + x;
                input[offset] = pixel.Item0;
                input[planeSize + offset] = pixel.Item1;
                input[(planeSize * 2) + offset] = pixel.Item2;
            }
        }

        return input;
    }

    private void EnsureInitialized()
    {
        if (_core is null || !IsAvailable)
        {
            throw new InvalidOperationException("OpenVINO runtime chưa sẵn sàng.");
        }
    }

    private sealed record LoadedModel(string Name, CompiledModel Compiled, int InputHeight, int InputWidth) : IDisposable
    {
        public void Dispose() => Compiled.Dispose();
    }
}

internal static partial class OpenVinoRuntimeLog
{
    [LoggerMessage(EventId = 3000, Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Không tìm thấy mô hình {name}.")]
    public static partial void ModelMissing(ILogger logger, string name);

    [LoggerMessage(EventId = 3001, Level = Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Đã tải mô hình {name} từ {path}.")]
    public static partial void ModelLoaded(ILogger logger, string name, string path);

    [LoggerMessage(EventId = 3002, Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "Không thể tải mô hình {name}.")]
    public static partial void ModelLoadFailed(ILogger logger, string name, Exception exception);

    [LoggerMessage(EventId = 3003, Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "Mô hình {name} không thể hoàn tất nhận diện.")]
    public static partial void InferenceFailed(ILogger logger, string name, Exception exception);
}
