namespace AutoMarkerReID.Domain;

public enum ImagePixelFormat
{
    Bgr24,
    Bgra32,
    Gray8,
}

public sealed record ImageFrame(
    int Width,
    int Height,
    int Stride,
    ImagePixelFormat PixelFormat,
    byte[] Pixels)
{
    public int BytesPerPixel => PixelFormat switch
    {
        ImagePixelFormat.Gray8 => 1,
        ImagePixelFormat.Bgr24 => 3,
        ImagePixelFormat.Bgra32 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(PixelFormat)),
    };

    public void Validate()
    {
        if (Width <= 0 || Height <= 0 || Stride < Width * BytesPerPixel)
        {
            throw new InvalidDataException("Kích thước hoặc stride của ảnh không hợp lệ.");
        }

        if (Pixels.Length < checked(Stride * Height))
        {
            throw new InvalidDataException("Buffer ảnh ngắn hơn kích thước đã khai báo.");
        }
    }
}

public enum ImageJobSource
{
    Clipboard,
    NewCapture,
    RepeatCapture,
    File,
    CommandLine,
}

public sealed record ImageJob(
    Guid Id,
    ImageFrame Image,
    ImageJobSource Source,
    DateTimeOffset CreatedAt,
    string? SourcePath = null)
{
    public static ImageJob Create(ImageFrame image, ImageJobSource source, string? sourcePath = null) =>
        new(Guid.NewGuid(), image, source, DateTimeOffset.UtcNow, sourcePath);
}

public readonly record struct ClipboardGeneration(uint Sequence, string ThumbnailHash)
{
    public bool IsEmpty => Sequence == 0 && string.IsNullOrEmpty(ThumbnailHash);
}
