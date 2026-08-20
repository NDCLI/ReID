using System.Runtime.InteropServices;
using AutoMarkerReID.Application;
using AutoMarkerReID.Domain;
using OpenCvSharp;

namespace AutoMarkerReID.Imaging;

public sealed class OpenCvImageCodec : IImageCodec
{
    public ImageFrame Decode(ReadOnlySpan<byte> encoded)
    {
        using var decoded = Cv2.ImDecode(encoded.ToArray(), ImreadModes.Color);
        if (decoded.Empty())
        {
            throw new InvalidDataException("Không thể giải mã ảnh.");
        }

        return MatConversion.ToImageFrame(decoded);
    }

    public byte[] EncodePng(ImageFrame image)
    {
        using var mat = MatConversion.ToMat(image);
        if (!Cv2.ImEncode(".png", mat, out var encoded))
        {
            throw new InvalidDataException("Không thể mã hóa ảnh PNG.");
        }

        return encoded;
    }

    public ImageFrame Crop(ImageFrame image, BoundingBox bounds)
    {
        var clamped = bounds.Clamp(image.Width, image.Height);
        if (clamped.Area == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds), "Vùng crop rỗng.");
        }

        using var source = MatConversion.ToMat(image);
        using var crop = new Mat(source, new Rect(clamped.X1, clamped.Y1, clamped.Width, clamped.Height));
        return MatConversion.ToImageFrame(crop);
    }
}

public static class MatConversion
{
    public static Mat ToMat(ImageFrame image)
    {
        image.Validate();
        var type = image.PixelFormat switch
        {
            ImagePixelFormat.Gray8 => MatType.CV_8UC1,
            ImagePixelFormat.Bgr24 => MatType.CV_8UC3,
            ImagePixelFormat.Bgra32 => MatType.CV_8UC4,
            _ => throw new ArgumentOutOfRangeException(nameof(image)),
        };

        var mat = new Mat(image.Height, image.Width, type);
        var rowBytes = image.Width * image.BytesPerPixel;
        for (var row = 0; row < image.Height; row++)
        {
            Marshal.Copy(image.Pixels, row * image.Stride, (nint)(mat.Data + (row * mat.Step())), rowBytes);
        }

        return mat;
    }

    public static ImageFrame ToImageFrame(Mat source)
    {
        if (source.Empty() || source.Depth() != MatType.CV_8U)
        {
            throw new InvalidDataException("Mat phải là ảnh 8-bit không rỗng.");
        }

        Mat? converted = null;
        var mat = source;
        try
        {
            if (source.Channels() == 2)
            {
                converted = new Mat();
                Cv2.CvtColor(source, converted, ColorConversionCodes.GRAY2BGR);
                mat = converted;
            }

            var format = mat.Channels() switch
            {
                1 => ImagePixelFormat.Gray8,
                3 => ImagePixelFormat.Bgr24,
                4 => ImagePixelFormat.Bgra32,
                _ => throw new InvalidDataException($"Số channel không hỗ trợ: {mat.Channels()}"),
            };
            var bytesPerPixel = mat.Channels();
            var width = mat.Width;
            var height = mat.Height;
            var stride = checked(width * bytesPerPixel);
            var pixels = new byte[checked(stride * height)];
            for (var row = 0; row < height; row++)
            {
                Marshal.Copy((nint)(mat.Data + (row * mat.Step())), pixels, row * stride, stride);
            }

            return new ImageFrame(width, height, stride, format, pixels);
        }
        finally
        {
            converted?.Dispose();
        }
    }
}
