using AutoMarkerReID.Domain;
using OpenCvSharp;

namespace AutoMarkerReID.Imaging;

public static class LbpDescriptor
{
    public static float[]? Create(ImageFrame image)
    {
        var validation = CropValidator.Validate(image);
        if (!validation.IsValid)
        {
            return null;
        }

        using var source = MatConversion.ToMat(image);
        using var gray = new Mat();
        Cv2.CvtColor(source, gray, source.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        using var resized = new Mat();
        Cv2.Resize(gray, resized, new Size(64, 128), interpolation: InterpolationFlags.Area);
        var resizedRows = resized.Rows;
        var resizedColumns = resized.Cols;
        using var lbp = new Mat(resizedRows - 2, resizedColumns - 2, MatType.CV_8UC1, Scalar.All(0));

        for (var y = 1; y < resizedRows - 1; y++)
        {
            for (var x = 1; x < resizedColumns - 1; x++)
            {
                var center = resized.At<byte>(y, x);
                byte code = 0;
                code |= (byte)((resized.At<byte>(y - 1, x - 1) >= center ? 1 : 0) << 7);
                code |= (byte)((resized.At<byte>(y - 1, x) >= center ? 1 : 0) << 6);
                code |= (byte)((resized.At<byte>(y - 1, x + 1) >= center ? 1 : 0) << 5);
                code |= (byte)((resized.At<byte>(y, x + 1) >= center ? 1 : 0) << 4);
                code |= (byte)((resized.At<byte>(y + 1, x + 1) >= center ? 1 : 0) << 3);
                code |= (byte)((resized.At<byte>(y + 1, x) >= center ? 1 : 0) << 2);
                code |= (byte)((resized.At<byte>(y + 1, x - 1) >= center ? 1 : 0) << 1);
                code |= (byte)(resized.At<byte>(y, x - 1) >= center ? 1 : 0);
                lbp.Set(y - 1, x - 1, code);
            }
        }

        var descriptor = new float[512];
        var lbpRows = lbp.Rows;
        FillHistogram(lbp, 0, lbpRows / 2, descriptor, 0);
        FillHistogram(lbp, lbpRows / 2, lbpRows, descriptor, 256);
        return descriptor;
    }

    public static float Similarity(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length != 512 || right.Length != 512)
        {
            return 0;
        }

        double coefficient = 0;
        for (var index = 0; index < left.Length; index++)
        {
            coefficient += Math.Sqrt(Math.Max(0, left[index] * right[index]));
        }

        var normalizedCoefficient = Math.Clamp(coefficient, 0, 1);
        var bhattacharyyaDistance = Math.Sqrt(Math.Max(0, 1 - normalizedCoefficient));
        return (float)(1 - bhattacharyyaDistance);
    }

    private static void FillHistogram(Mat lbp, int startRow, int endRow, float[] destination, int offset)
    {
        var columns = lbp.Cols;
        var total = Math.Max(1, (endRow - startRow) * columns);
        for (var y = startRow; y < endRow; y++)
        {
            for (var x = 0; x < columns; x++)
            {
                destination[offset + lbp.At<byte>(y, x)]++;
            }
        }

        for (var index = 0; index < 256; index++)
        {
            destination[offset + index] /= total;
        }
    }
}
