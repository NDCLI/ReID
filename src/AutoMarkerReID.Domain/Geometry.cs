namespace AutoMarkerReID.Domain;

public readonly record struct BoundingBox(int X1, int Y1, int X2, int Y2)
{
    public int Width => Math.Max(0, X2 - X1);
    public int Height => Math.Max(0, Y2 - Y1);
    public int Area => Width * Height;
    public int CenterX => X1 + (Width / 2);
    public int CenterY => Y1 + (Height / 2);

    public BoundingBox Normalize() => new(
        Math.Min(X1, X2),
        Math.Min(Y1, Y2),
        Math.Max(X1, X2),
        Math.Max(Y1, Y2));

    public BoundingBox Clamp(int imageWidth, int imageHeight)
    {
        var normalized = Normalize();
        return new BoundingBox(
            Math.Clamp(normalized.X1, 0, imageWidth),
            Math.Clamp(normalized.Y1, 0, imageHeight),
            Math.Clamp(normalized.X2, 0, imageWidth),
            Math.Clamp(normalized.Y2, 0, imageHeight));
    }

    public bool Contains(int x, int y) => x >= X1 && x < X2 && y >= Y1 && y < Y2;

    public double IntersectionOverUnion(BoundingBox other)
    {
        var intersectionWidth = Math.Max(0, Math.Min(X2, other.X2) - Math.Max(X1, other.X1));
        var intersectionHeight = Math.Max(0, Math.Min(Y2, other.Y2) - Math.Max(Y1, other.Y1));
        var intersection = intersectionWidth * intersectionHeight;
        var union = Area + other.Area - intersection;
        return union <= 0 ? 0 : (double)intersection / union;
    }
}
