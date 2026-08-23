using AutoMarkerReID.Domain;
using OpenCvSharp;

namespace AutoMarkerReID.Imaging;

public static class ImageEditorOperations
{
    public static ImageFrame CutOut(ImageFrame image, BoundingBox selection) =>
        CutOut(image, selection, selection.Width >= selection.Height);

    public static ImageFrame CutOut(ImageFrame image, BoundingBox selection, bool removeVerticalStrip)
    {
        var area = selection.Clamp(image.Width, image.Height);
        if (area.Area == 0) return image;

        using var source = MatConversion.ToMat(image);
        using var result = removeVerticalStrip
            ? RemoveVerticalStrip(source, area.X1, area.X2)
            : RemoveHorizontalStrip(source, area.Y1, area.Y2);
        return MatConversion.ToImageFrame(result);
    }

    public static ImageFrame Merge(ImageFrame primary, ImageFrame secondary, bool secondaryOnLeft)
    {
        using var first = AsBgr(primary);
        using var second = AsBgr(secondary);
        var width = checked(first.Width + second.Width);
        var height = Math.Max(first.Height, second.Height);
        using var canvas = new Mat(height, width, MatType.CV_8UC3, new Scalar(96, 96, 96));
        var left = secondaryOnLeft ? second : first;
        var right = secondaryOnLeft ? first : second;
        CopyCentered(left, canvas, 0);
        CopyCentered(right, canvas, left.Width);
        return MatConversion.ToImageFrame(canvas);
    }

    private static Mat RemoveVerticalStrip(Mat source, int x1, int x2)
    {
        if (x1 <= 0 && x2 >= source.Width) throw new InvalidOperationException("Không thể xóa toàn bộ chiều rộng ảnh.");
        var result = new Mat(source.Height, source.Width - (x2 - x1), source.Type());
        if (x1 > 0)
        {
            using var input = new Mat(source, new Rect(0, 0, x1, source.Height));
            using var output = new Mat(result, new Rect(0, 0, x1, source.Height));
            input.CopyTo(output);
        }
        if (x2 < source.Width)
        {
            using var input = new Mat(source, new Rect(x2, 0, source.Width - x2, source.Height));
            using var output = new Mat(result, new Rect(x1, 0, source.Width - x2, source.Height));
            input.CopyTo(output);
        }
        return result;
    }

    private static Mat RemoveHorizontalStrip(Mat source, int y1, int y2)
    {
        if (y1 <= 0 && y2 >= source.Height) throw new InvalidOperationException("Không thể xóa toàn bộ chiều cao ảnh.");
        var result = new Mat(source.Height - (y2 - y1), source.Width, source.Type());
        if (y1 > 0)
        {
            using var input = new Mat(source, new Rect(0, 0, source.Width, y1));
            using var output = new Mat(result, new Rect(0, 0, source.Width, y1));
            input.CopyTo(output);
        }
        if (y2 < source.Height)
        {
            using var input = new Mat(source, new Rect(0, y2, source.Width, source.Height - y2));
            using var output = new Mat(result, new Rect(0, y1, source.Width, source.Height - y2));
            input.CopyTo(output);
        }
        return result;
    }

    private static Mat AsBgr(ImageFrame image)
    {
        var source = MatConversion.ToMat(image);
        if (source.Channels() == 3) return source;
        var converted = new Mat();
        Cv2.CvtColor(source, converted, source.Channels() == 4 ? ColorConversionCodes.BGRA2BGR : ColorConversionCodes.GRAY2BGR);
        source.Dispose();
        return converted;
    }

    private static void CopyCentered(Mat source, Mat target, int x)
    {
        var y = (target.Height - source.Height) / 2;
        using var destination = new Mat(target, new Rect(x, y, source.Width, source.Height));
        source.CopyTo(destination);
    }
}
