using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AutoMarkerReID.Application;
using AutoMarkerReID.Domain;
using Microsoft.Extensions.Logging;

namespace AutoMarkerReID.Windows;

public sealed class WindowsClipboardMonitor(IImageCodec codec, ILogger<WindowsClipboardMonitor> logger) : IClipboardMonitor
{
    private uint _lastSequence;
    private string _lastFallbackHash = string.Empty;
    private int _suspended;
    private int _ignoreNextWrite;

    public bool IsSuspended => Volatile.Read(ref _suspended) != 0;

    public async Task RunAsync(Func<ImageJob, CancellationToken, ValueTask> onImage, CancellationToken cancellationToken)
    {
        SynchronizeGeneration();
        using var timer = new PeriodicTimer(ReIdDefaults.ClipboardPollInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (IsSuspended)
            {
                continue;
            }

            var sequence = NativeMethods.GetClipboardSequenceNumber();
            if (sequence == 0)
            {
                await TryQueueFallbackGenerationAsync(onImage, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (sequence == _lastSequence)
            {
                continue;
            }

            if (Interlocked.Exchange(ref _ignoreNextWrite, 0) != 0)
            {
                _lastSequence = sequence;
                ClipboardMonitorLog.OwnWriteIgnored(logger, sequence);
                continue;
            }

            if (ShouldIgnoreProducer())
            {
                _lastSequence = sequence;
                ClipboardMonitorLog.ExcelClipboardIgnored(logger, sequence);
                continue;
            }

            var deadline = DateTimeOffset.UtcNow + ReIdDefaults.ClipboardReadyTimeout;
            ImageFrame? image = null;
            do
            {
                image = TryReadImage();
                if (image is not null)
                {
                    break;
                }

                await Task.Delay(ReIdDefaults.ClipboardPollInterval, cancellationToken).ConfigureAwait(false);
            }
            while (DateTimeOffset.UtcNow < deadline && NativeMethods.GetClipboardSequenceNumber() == sequence);

            _lastSequence = sequence;
            if (image is null)
            {
                ClipboardMonitorLog.PayloadUnavailable(logger, sequence);
                continue;
            }

            var hash = ComputeThumbnailHash(image);
            await onImage(ImageJob.Create(image, ImageJobSource.Clipboard), cancellationToken).ConfigureAwait(false);
            ClipboardMonitorLog.ImageQueued(logger, sequence, hash);
        }
    }

    public void SetSuspended(bool suspended) => Interlocked.Exchange(ref _suspended, suspended ? 1 : 0);

    public void IgnoreNextWrite() => Interlocked.Exchange(ref _ignoreNextWrite, 1);

    public void SynchronizeGeneration()
    {
        _lastSequence = NativeMethods.GetClipboardSequenceNumber();
        if (_lastSequence == 0 && TryReadImage() is { } image)
        {
            _lastFallbackHash = ComputeThumbnailHash(image);
        }
    }

    private async Task TryQueueFallbackGenerationAsync(Func<ImageJob, CancellationToken, ValueTask> onImage, CancellationToken cancellationToken)
    {
        if (ShouldIgnoreProducer()) return;
        var image = TryReadImage();
        if (image is null) return;
        var hash = ComputeThumbnailHash(image);
        if (string.Equals(hash, _lastFallbackHash, StringComparison.Ordinal)) return;
        _lastFallbackHash = hash;
        if (Interlocked.Exchange(ref _ignoreNextWrite, 0) != 0)
        {
            ClipboardMonitorLog.OwnWriteIgnored(logger, 0);
            return;
        }
        await onImage(ImageJob.Create(image, ImageJobSource.Clipboard), cancellationToken).ConfigureAwait(false);
        ClipboardMonitorLog.ImageQueued(logger, 0, hash);
    }

    private ImageFrame? TryReadImage()
    {
        if (!NativeMethods.OpenClipboard(0))
        {
            return null;
        }

        try
        {
            var dib = NativeMethods.GetClipboardData(NativeMethods.CfDib);
            if (dib != 0)
            {
                var decoded = TryDecodeDib(dib);
                if (decoded is not null)
                {
                    return decoded;
                }
            }

            var drop = NativeMethods.GetClipboardData(NativeMethods.CfHDrop);
            return drop == 0 ? null : TryReadRecentImageFile(drop);
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    private static ImageFrame? TryDecodeDib(nint handle)
    {
        var pointer = NativeMethods.GlobalLock(handle);
        if (pointer == 0)
        {
            return null;
        }

        try
        {
            var headerSize = Marshal.ReadInt32(pointer, 0);
            var width = Marshal.ReadInt32(pointer, 4);
            var signedHeight = Marshal.ReadInt32(pointer, 8);
            var bitsPerPixel = Marshal.ReadInt16(pointer, 14);
            var compression = Marshal.ReadInt32(pointer, 16);
            if (headerSize < 40 || width <= 0 || signedHeight == 0 || compression != 0 || bitsPerPixel is not (24 or 32))
            {
                return null;
            }

            var height = Math.Abs(signedHeight);
            var bytesPerPixel = bitsPerPixel / 8;
            var sourceStride = ((width * bytesPerPixel) + 3) & ~3;
            var targetStride = checked(width * bytesPerPixel);
            var pixels = new byte[checked(targetStride * height)];
            var pixelStart = pointer + headerSize;
            for (var targetRow = 0; targetRow < height; targetRow++)
            {
                var sourceRow = signedHeight > 0 ? height - targetRow - 1 : targetRow;
                Marshal.Copy(pixelStart + (sourceRow * sourceStride), pixels, targetRow * targetStride, targetStride);
            }

            return new ImageFrame(
                width,
                height,
                targetStride,
                bitsPerPixel == 24 ? ImagePixelFormat.Bgr24 : ImagePixelFormat.Bgra32,
                pixels);
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }
    }

    private ImageFrame? TryReadRecentImageFile(nint drop)
    {
        var count = NativeMethods.DragQueryFile(drop, uint.MaxValue, null, 0);
        if (count == 0)
        {
            return null;
        }

        var length = NativeMethods.DragQueryFile(drop, 0, null, 0);
        var buffer = new char[length + 1];
        NativeMethods.DragQueryFile(drop, 0, buffer, (uint)buffer.Length);
        var path = new string(buffer, 0, (int)length);
        if (!File.Exists(path) || Path.GetExtension(path).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp"))
        {
            return null;
        }

        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromSeconds(5))
        {
            return null;
        }

        return codec.Decode(File.ReadAllBytes(path));
    }

    private static bool ShouldIgnoreProducer()
    {
        var owner = GetProcessName(NativeMethods.GetClipboardOwner());
        if (owner.Equals("ShareX", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var foreground = GetProcessName(NativeMethods.GetForegroundWindow());
        return owner.Equals("EXCEL", StringComparison.OrdinalIgnoreCase) || foreground.Equals("EXCEL", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetProcessName(nint window)
    {
        if (window == 0)
        {
            return string.Empty;
        }

        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            return string.Empty;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    private static string ComputeThumbnailHash(ImageFrame image)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var step = Math.Max(1, image.Pixels.Length / 4096);
        for (var index = 0; index < image.Pixels.Length; index += step)
        {
            hash.AppendData(image.Pixels.AsSpan(index, 1));
        }

        return Convert.ToHexString(hash.GetHashAndReset().AsSpan(0, 8));
    }
}

internal static partial class ClipboardMonitorLog
{
    [LoggerMessage(EventId = 2000, Level = LogLevel.Debug, Message = "Đã bỏ clipboard do app tự ghi, sequence {sequence}.")]
    public static partial void OwnWriteIgnored(ILogger logger, uint sequence);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Debug, Message = "Đã bỏ clipboard Excel, sequence {sequence}.")]
    public static partial void ExcelClipboardIgnored(ILogger logger, uint sequence);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Warning, Message = "Clipboard sequence {sequence} không có payload ảnh sau thời gian retry.")]
    public static partial void PayloadUnavailable(ILogger logger, uint sequence);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Debug, Message = "Đã queue clipboard sequence {sequence}, hash {hash}.")]
    public static partial void ImageQueued(ILogger logger, uint sequence, string hash);
}
