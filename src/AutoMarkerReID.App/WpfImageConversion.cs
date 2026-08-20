using System.Windows.Media;
using System.Windows.Media.Imaging;
using AutoMarkerReID.Domain;

namespace AutoMarkerReID.App;

internal static class WpfImageConversion
{
    public static BitmapSource ToBitmapSource(ImageFrame image)
    {
        image.Validate();
        var format = image.PixelFormat switch
        {
            ImagePixelFormat.Bgr24 => PixelFormats.Bgr24,
            ImagePixelFormat.Bgra32 => PixelFormats.Bgra32,
            ImagePixelFormat.Gray8 => PixelFormats.Gray8,
            _ => throw new ArgumentOutOfRangeException(nameof(image)),
        };
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, format, null, image.Pixels, image.Stride);
        bitmap.Freeze();
        return bitmap;
    }
}
