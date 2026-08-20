using System.Runtime.InteropServices;
using AutoMarkerReID.Application;
using AutoMarkerReID.Domain;

namespace AutoMarkerReID.Windows;

public sealed class WindowsClipboardWriter : IClipboardWriter
{
    public Task WriteImageAsync(ImageFrame image, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        image.Validate();
        var sourceBytesPerPixel = image.BytesPerPixel;
        var targetStride = ((image.Width * 3) + 3) & ~3;
        var headerSize = 40;
        var allocationSize = checked(headerSize + (targetStride * image.Height));
        var memory = NativeMethods.GlobalAlloc(NativeMethods.GmemMoveable | NativeMethods.GmemZeroInit, (nuint)allocationSize);
        if (memory == 0)
        {
            throw new InvalidOperationException("Không thể cấp phát DIB cho Clipboard.");
        }

        var handedToClipboard = false;
        try
        {
            var pointer = NativeMethods.GlobalLock(memory);
            if (pointer == 0)
            {
                throw new InvalidOperationException("Không thể khóa DIB Clipboard.");
            }

            try
            {
                Marshal.WriteInt32(pointer, 0, headerSize);
                Marshal.WriteInt32(pointer, 4, image.Width);
                Marshal.WriteInt32(pointer, 8, image.Height);
                Marshal.WriteInt16(pointer, 12, 1);
                Marshal.WriteInt16(pointer, 14, 24);
                Marshal.WriteInt32(pointer, 16, 0);
                Marshal.WriteInt32(pointer, 20, targetStride * image.Height);
                var row = new byte[targetStride];
                for (var targetRow = 0; targetRow < image.Height; targetRow++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Array.Clear(row);
                    var sourceRow = image.Height - targetRow - 1;
                    for (var x = 0; x < image.Width; x++)
                    {
                        var sourceOffset = (sourceRow * image.Stride) + (x * sourceBytesPerPixel);
                        var targetOffset = x * 3;
                        if (image.PixelFormat == ImagePixelFormat.Gray8)
                        {
                            row[targetOffset] = image.Pixels[sourceOffset];
                            row[targetOffset + 1] = image.Pixels[sourceOffset];
                            row[targetOffset + 2] = image.Pixels[sourceOffset];
                        }
                        else
                        {
                            row[targetOffset] = image.Pixels[sourceOffset];
                            row[targetOffset + 1] = image.Pixels[sourceOffset + 1];
                            row[targetOffset + 2] = image.Pixels[sourceOffset + 2];
                        }
                    }

                    Marshal.Copy(row, 0, pointer + headerSize + (targetRow * targetStride), row.Length);
                }
            }
            finally
            {
                NativeMethods.GlobalUnlock(memory);
            }

            if (!NativeMethods.OpenClipboard(0))
            {
                throw new InvalidOperationException("Clipboard đang bị ứng dụng khác khóa.");
            }

            try
            {
                if (!NativeMethods.EmptyClipboard() || NativeMethods.SetClipboardData(NativeMethods.CfDib, memory) == 0)
                {
                    throw new InvalidOperationException("Không thể ghi ảnh vào Clipboard.");
                }

                handedToClipboard = true;
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }

            return Task.CompletedTask;
        }
        finally
        {
            if (!handedToClipboard)
            {
                NativeMethods.GlobalFree(memory);
            }
        }
    }
}
