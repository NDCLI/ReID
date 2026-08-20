using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using AutoMarkerReID.Application;
using AutoMarkerReID.Domain;
using Forms = System.Windows.Forms;

namespace AutoMarkerReID.Windows;

public sealed class WindowsScreenCaptureService : IScreenCaptureService
{
    public BoundingBox VirtualScreenBounds
    {
        get
        {
            var screen = Forms.SystemInformation.VirtualScreen;
            return new BoundingBox(screen.Left, screen.Top, screen.Right, screen.Bottom);
        }
    }

    public Task<ImageFrame> CaptureAsync(BoundingBox region, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = region.Normalize();
        if (normalized.Width < 5 || normalized.Height < 5)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "Vùng chụp phải có mỗi chiều ít nhất 5 px.");
        }

        using var bitmap = new Bitmap(normalized.Width, normalized.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(normalized.X1, normalized.Y1, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
        }

        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = bitmap.Width * 4;
            var pixels = new byte[stride * bitmap.Height];
            for (var row = 0; row < bitmap.Height; row++)
            {
                Marshal.Copy(data.Scan0 + (row * data.Stride), pixels, row * stride, stride);
            }

            return Task.FromResult(new ImageFrame(bitmap.Width, bitmap.Height, stride, ImagePixelFormat.Bgra32, pixels));
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
