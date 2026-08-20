using AutoMarkerReID.Application;
using AutoMarkerReID.Domain;
using OpenCvSharp;

namespace AutoMarkerReID.Imaging;

public sealed class OpenCvInterfaceDetector : IInterfaceDetector, IDisposable
{
    private static readonly double[] Scales = [0.8, 0.9, 1.0, 1.1, 1.2];
    private readonly Mat? _template;

    public OpenCvInterfaceDetector(string templatePath)
    {
        if (File.Exists(templatePath))
        {
            var loaded = Cv2.ImRead(templatePath, ImreadModes.Grayscale);
            if (!loaded.Empty())
            {
                _template = loaded;
            }
            else
            {
                loaded.Dispose();
            }
        }
    }

    public bool IsReIdInterface(ImageFrame image, out float score)
    {
        score = 0;
        if (_template is null || image.Width < 600 || image.Width <= image.Height)
        {
            return false;
        }

        using var color = MatConversion.ToMat(image);
        using var gray = new Mat();
        if (color.Channels() == 1)
        {
            color.CopyTo(gray);
        }
        else
        {
            Cv2.CvtColor(color, gray, color.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        }

        foreach (var scale in Scales)
        {
            var width = Math.Max(1, (int)Math.Round(_template.Width * scale));
            var height = Math.Max(1, (int)Math.Round(_template.Height * scale));
            if (width > gray.Width || height > gray.Height)
            {
                continue;
            }

            using var resized = new Mat();
            Cv2.Resize(_template, resized, new Size(width, height), interpolation: InterpolationFlags.Area);
            using var result = new Mat();
            Cv2.MatchTemplate(gray, resized, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out var maxValue, out _, out _);
            score = Math.Max(score, (float)maxValue);
        }

        return score >= ReIdDefaults.InterfaceMatchThreshold;
    }

    public void Dispose() => _template?.Dispose();
}
