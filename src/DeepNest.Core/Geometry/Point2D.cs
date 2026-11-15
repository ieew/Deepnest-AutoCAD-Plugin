namespace DeepNest.Core.Geometry;

/// <summary>
/// 二维点（图纸单位，Y 向上）。
/// <para><see cref="Exact"/> 表示该点来自图纸原始顶点（不是离散/插值产生的），供共边合并判断使用。</para>
/// </summary>
public readonly struct Point2D : IEquatable<Point2D>
{
    public readonly double X;
    public readonly double Y;
    public readonly bool Exact;

    public Point2D(double x, double y, bool exact = false)
    {
        X = x;
        Y = y;
        Exact = exact;
    }

    public Point2D WithExact(bool exact) => new(X, Y, exact);

    public static Point2D operator +(Point2D a, Point2D b) => new(a.X + b.X, a.Y + b.Y, a.Exact && b.Exact);
    public static Point2D operator -(Point2D a, Point2D b) => new(a.X - b.X, a.Y - b.Y, a.Exact && b.Exact);
    public static Point2D operator *(Point2D a, double k) => new(a.X * k, a.Y * k, a.Exact);

    public double Length => Math.Sqrt(X * X + Y * Y);
    public double LengthSq => X * X + Y * Y;

    public bool Equals(Point2D other) => X.Equals(other.X) && Y.Equals(other.Y);
    public override bool Equals(object? obj) => obj is Point2D p && Equals(p);
    public override int GetHashCode() => HashCode.Combine(X, Y);
    public override string ToString() => $"({X:0.####},{Y:0.####})";
}

/// <summary>轴对齐包围盒。</summary>
public readonly struct Bounds2D
{
    public readonly double MinX;
    public readonly double MinY;
    public readonly double MaxX;
    public readonly double MaxY;

    public Bounds2D(double minX, double minY, double maxX, double maxY)
    {
        MinX = minX;
        MinY = minY;
        MaxX = maxX;
        MaxY = maxY;
    }

    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;
    public Point2D Min => new(MinX, MinY);
    public Point2D Max => new(MaxX, MaxY);
    public double Area => Width * Height;
    public Point2D Center => new((MinX + MaxX) * 0.5, (MinY + MaxY) * 0.5);

    public bool Intersects(in Bounds2D other) =>
        MinX <= other.MaxX && MaxX >= other.MinX && MinY <= other.MaxY && MaxY >= other.MinY;

    public Bounds2D Union(in Bounds2D other) => new(
        Math.Min(MinX, other.MinX), Math.Min(MinY, other.MinY),
        Math.Max(MaxX, other.MaxX), Math.Max(MaxY, other.MaxY));

    public override string ToString() => $"[{MinX:0.###},{MinY:0.###} - {MaxX:0.###},{MaxY:0.###}]";
}
