using System.Text;
using System.Text.RegularExpressions;
using AutoMarkerReID.Application;
using AutoMarkerReID.Domain;
using AutoMarkerReID.Imaging;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using OpenVinoSharp;

namespace AutoMarkerReID.Inference;

public sealed partial class OpenVinoOcrService : IOcrService, IAsyncDisposable
{
    private static readonly double[] BottomRatios = [0.18, 0.20, 0.22, 0.25, 0.28, 0.30];
    private readonly string _modelPath;
    private readonly string[] _characters;
    private readonly ILogger<OpenVinoOcrService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Core? _core;
    private CompiledModel? _model;
    private bool _disabled;

    public OpenVinoOcrService(string modelPath, string dictionaryPath, ILogger<OpenVinoOcrService> logger)
    {
        _modelPath = modelPath;
        _logger = logger;
        var dictionary = File.Exists(dictionaryPath)
            ? File.ReadAllLines(dictionaryPath, Encoding.UTF8)
            : [];
        _characters = ["blank", .. dictionary, " "];
    }

    public async Task<string?> ReadTimestampAsync(ImageFrame card, CancellationToken cancellationToken)
    {
        if (_disabled || !File.Exists(_modelPath) || _characters.Length <= 2)
        {
            return null;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            var votes = new Dictionary<string, List<float>>(StringComparer.OrdinalIgnoreCase);
            foreach (var ratio in BottomRatios)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var source = MatConversion.ToMat(card);
                var cropHeight = Math.Max(2, (int)Math.Round(source.Height * ratio));
                using var bottom = new Mat(source, new Rect(0, source.Height - cropHeight, source.Width, cropHeight));
                using var enlarged = new Mat();
                Cv2.Resize(bottom, enlarged, new Size(bottom.Width * 8, bottom.Height * 8), interpolation: InterpolationFlags.Cubic);
                var (text, confidence) = Recognize(enlarged);
                var timestamp = NormalizeTimestamp(text);
                if (timestamp is null)
                {
                    continue;
                }

                if (!votes.TryGetValue(timestamp, out var scores))
                {
                    scores = [];
                    votes[timestamp] = scores;
                }

                scores.Add(confidence);
                if (scores.Count >= 2)
                {
                    return timestamp;
                }
            }

            return votes
                .OrderByDescending(item => item.Value.Count)
                .ThenByDescending(item => item.Value.Max())
                .ThenByDescending(item => item.Value.Average())
                .Select(item => item.Key)
                .FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _disabled = true;
            OpenVinoOcrLog.Disabled(_logger, exception);
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task WarmupAsync(CancellationToken cancellationToken)
    {
        if (_disabled || !File.Exists(_modelPath) || _characters.Length <= 2) return;
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
        }
        catch (Exception exception)
        {
            _disabled = true;
            OpenVinoOcrLog.Disabled(_logger, exception);
        }
        finally
        {
            _lock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _model?.Dispose();
        _core?.Dispose();
        _lock.Dispose();
        return ValueTask.CompletedTask;
    }

    public static string? NormalizeTimestamp(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = text.ToUpperInvariant()
            .Replace('Ｏ', '0')
            .Replace('O', '0')
            .Replace('：', ':')
            .Replace("A.M", "AM", StringComparison.Ordinal)
            .Replace("P.M", "PM", StringComparison.Ordinal);
        var match = TimestampRegex().Match(normalized);
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, out var hour) ||
            !int.TryParse(match.Groups[2].Value, out var minute) ||
            hour is < 1 or > 12 || minute is < 0 or > 59)
        {
            return null;
        }

        return $"{hour}:{minute:00} {match.Groups[3].Value}M";
    }

    private (string Text, float Confidence) Recognize(Mat image)
    {
        const int targetHeight = 48;
        const int targetWidth = 320;
        using var bgr = new Mat();
        if (image.Channels() == 4)
        {
            Cv2.CvtColor(image, bgr, ColorConversionCodes.BGRA2BGR);
        }
        else if (image.Channels() == 1)
        {
            Cv2.CvtColor(image, bgr, ColorConversionCodes.GRAY2BGR);
        }
        else
        {
            image.CopyTo(bgr);
        }

        var resizedWidth = Math.Min(targetWidth, Math.Max(1, (int)Math.Ceiling(targetHeight * (bgr.Width / (double)bgr.Height))));
        using var resized = new Mat();
        Cv2.Resize(bgr, resized, new Size(resizedWidth, targetHeight), interpolation: InterpolationFlags.Linear);
        var planeSize = targetHeight * targetWidth;
        var input = new float[planeSize * 3];
        for (var y = 0; y < targetHeight; y++)
        {
            for (var x = 0; x < resizedWidth; x++)
            {
                var pixel = resized.At<Vec3b>(y, x);
                var index = (y * targetWidth) + x;
                input[index] = (pixel.Item0 / 127.5f) - 1f;
                input[planeSize + index] = (pixel.Item1 / 127.5f) - 1f;
                input[(planeSize * 2) + index] = (pixel.Item2 / 127.5f) - 1f;
            }
        }

        using var shape = Shape.nchw(1, 3, targetHeight, targetWidth);
        using var tensor = new Tensor(shape, input);
        using var request = _model!.create_infer_request();
        request.set_input_tensor(tensor);
        request.infer();
        using var output = request.get_output_tensor();
        var values = output.get_float_data();
        using var outputShape = output.shape;
        var dimensions = outputShape.get_dims();
        if (dimensions.Length < 3)
        {
            throw new InvalidDataException("Output OCR không có shape [batch,time,classes].");
        }

        var timeSteps = checked((int)dimensions[^2]);
        var classCount = checked((int)dimensions[^1]);
        if (timeSteps <= 0 || classCount <= 1 || values.Length < timeSteps * classCount)
        {
            throw new InvalidDataException("Output OCR có kích thước không hợp lệ.");
        }

        var builder = new StringBuilder();
        var confidences = new List<float>();
        var previous = -1;
        for (var step = 0; step < timeSteps; step++)
        {
            var offset = step * classCount;
            var bestIndex = 0;
            var bestScore = values[offset];
            for (var classIndex = 1; classIndex < classCount; classIndex++)
            {
                var score = values[offset + classIndex];
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = classIndex;
                }
            }

            if (bestIndex != 0 && bestIndex != previous && bestIndex < _characters.Length)
            {
                builder.Append(_characters[bestIndex]);
                confidences.Add(bestScore);
            }

            previous = bestIndex;
        }

        return (builder.ToString(), confidences.Count == 0 ? 0 : confidences.Average());
    }

    private void EnsureLoaded()
    {
        if (_model is not null)
        {
            return;
        }

        _core = new Core();
        _model = _core.compile_model_unicode(_modelPath, "CPU");
        OpenVinoOcrLog.Loaded(_logger, _modelPath);
    }

    [GeneratedRegex(@"(?<!\d)(\d{1,2})\s*[:.]\s*(\d{2})\s*([AP])\s*\.?\s*M\.?")]
    private static partial Regex TimestampRegex();
}

internal static partial class OpenVinoOcrLog
{
    [LoggerMessage(EventId = 3300, Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "Đã tải OCR model {path}.")]
    public static partial void Loaded(ILogger logger, string path);

    [LoggerMessage(EventId = 3301, Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "OCR OpenVINO bị vô hiệu hóa sau lỗi runtime.")]
    public static partial void Disabled(ILogger logger, Exception exception);
}
