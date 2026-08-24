using System.Buffers.Binary;
using System.Security.Cryptography;
using AutoMarkerReID.Domain;
using OpenCvSharp;

namespace AutoMarkerReID.Imaging;

public sealed record CropValidationResult(bool IsValid, string Reason);

public static class CropValidator
{
    public static CropValidationResult Validate(ImageFrame image)
    {
        if (image.Height < 100)
        {
            return new(false, "ảnh người thấp hơn kích thước tối thiểu 100 px");
        }

        if (image.Width < 35)
        {
            return new(false, "ảnh người hẹp hơn kích thước tối thiểu 35 px");
        }

        var ratio = (double)image.Height / image.Width;
        if (ratio is < 1.2 or > 5.5)
        {
            return new(false, "tỷ lệ khung hình không phù hợp với ảnh người");
        }

        using var mat = MatConversion.ToMat(image);
        Cv2.MeanStdDev(mat, out _, out var deviation);
        var maxDeviation = Enumerable.Range(0, mat.Channels()).Max(channel => deviation[channel]);
        if (maxDeviation < 12)
        {
            return new(false, "ảnh có quá ít chi tiết để sử dụng");
        }

        return new(true, "hợp lệ");
    }
}

public sealed record ImageFingerprints(string ExactSha256, ulong PerceptualHash, ulong DifferenceHash);

public static class DuplicateHasher
{
    public static ImageFingerprints Compute(ImageFrame image)
    {
        using var mat = MatConversion.ToMat(image);
        using var gray = new Mat();
        if (mat.Channels() == 1)
        {
            mat.CopyTo(gray);
        }
        else
        {
            Cv2.CvtColor(mat, gray, mat.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        }

        Span<byte> dimensions = stackalloc byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(dimensions, image.Width);
        BinaryPrimitives.WriteInt32LittleEndian(dimensions[4..], image.Height);
        BinaryPrimitives.WriteInt32LittleEndian(dimensions[8..], (int)image.PixelFormat);
        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        incremental.AppendData(dimensions);
        incremental.AppendData(image.Pixels);
        var exact = Convert.ToHexString(incremental.GetHashAndReset());

        return new ImageFingerprints(exact, ComputePerceptualHash(gray), ComputeDifferenceHash(gray));
    }

    public static int HammingDistance(ulong left, ulong right) => System.Numerics.BitOperations.PopCount(left ^ right);

    private static ulong ComputeDifferenceHash(Mat gray)
    {
        using var resized = new Mat();
        Cv2.Resize(gray, resized, new Size(9, 8), interpolation: InterpolationFlags.Area);
        ulong hash = 0;
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                if (resized.At<byte>(y, x) > resized.At<byte>(y, x + 1))
                {
                    hash |= 1UL << ((y * 8) + x);
                }
            }
        }

        return hash;
    }

    private static ulong ComputePerceptualHash(Mat gray)
    {
        using var resized = new Mat();
        Cv2.Resize(gray, resized, new Size(32, 32), interpolation: InterpolationFlags.Area);
        using var floating = new Mat();
        resized.ConvertTo(floating, MatType.CV_32F);
        using var dct = new Mat();
        Cv2.Dct(floating, dct);
        var values = new List<float>(64);
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                if (x != 0 || y != 0)
                {
                    values.Add(dct.At<float>(y, x));
                }
            }
        }

        var ordered = values.Order().ToArray();
        var median = ordered[ordered.Length / 2];
        ulong hash = 0;
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index] > median)
            {
                hash |= 1UL << index;
            }
        }

        return hash;
    }
}
