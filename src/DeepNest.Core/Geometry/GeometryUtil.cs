using Clipper2Lib;

namespace DeepNest.Core.Geometry;

/// <summary>
/// 纯几何工具。对应原 <c>main/util/geometryutil.js</c> 中被主流程实际使用的部分
/// （面积、包围盒、近似相等、旋转、点在多边形内、凸包）。
/// </summary>
public static class GeometryUtil
{
    public const double Tolerance = 1e-9;

    public static bool AlmostEqual(double a, double b, double tolerance = Tolerance) =>
        Math.Abs(a - b) < tolerance;

    public static bool AlmostEqualPoints(Point2D a, Point2D b, double tolerance = Tolerance) =>
        AlmostEqual(a.X, b.X, tolerance) && AlmostEqual(a.Y, b.Y, tolerance);

    /// <summary>鞋带公式。正数 = 逆时针（CCW），负数 = 顺时针，与 Clipper2 的 Area 同号。</summary>
    public static double SignedArea(IReadOnlyList<Point2D> points)
    {
        int n = points.Count;
        if (n < 3)
        {
            return 0;
        }

        double sum = 0;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            sum += (points[j].X * points[i].Y) - (points[i].X * points[j].Y);
        }
        return sum * 0.5;
    }

    public static Bounds2D GetBounds(IReadOnlyList<Point2D> points)
    {
        if (points.Count == 0)
        {
            return new Bounds2D(0, 0, 0, 0);
        }

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }
        return new Bounds2D(minX, minY, maxX, maxY);
    }

    public static Bounds2D GetBounds(IEnumerable<Point2D> points)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        bool any = false;
        foreach (var p in points)
        {
            any = true;
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }
        return any ? new Bounds2D(minX, minY, maxX, maxY) : new Bounds2D(0, 0, 0, 0);
    }

    public static List<Point2D> Translate(IReadOnlyList<Point2D> points, double dx, double dy)
    {
        var result = new List<Point2D>(points.Count);
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            result.Add(new Point2D(p.X + dx, p.Y + dy, p.Exact));
        }
        return result;
    }

    public static List<Point2D> Translate(IReadOnlyList<Point2D> points, Point2D delta) =>
        Translate(points, delta.X, delta.Y);

    /// <summary>绕原点逆时针旋转（度）。子轮廓一并旋转。</summary>
    public static Poly Rotate(Poly poly, double degrees)
    {
        var rotated = RotatePoints(poly.Points, degrees);
        var result = new Poly(rotated)
        {
            SourceId = poly.SourceId,
            Rotation = poly.Rotation + degrees,
            Mirrored = poly.Mirrored,
            Tag = poly.Tag
        };
        foreach (var child in poly.Children)
        {
            var rc = Rotate(child, degrees);
            rc.Parent = result;
            result.Children.Add(rc);
        }
        return result;
    }

    public static List<Point2D> RotatePoints(IReadOnlyList<Point2D> points, double degrees)
    {
        double rad = degrees * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        var result = new List<Point2D>(points.Count);
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            result.Add(new Point2D(
                p.X * cos - p.Y * sin,
                p.X * sin + p.Y * cos,
                p.Exact));
        }
        return result;
    }

    /// <summary>关于本地 Y 轴镜像（x → -x）。子轮廓一并镜像。</summary>
    public static Poly Mirror(Poly poly)
    {
        var mirrored = MirrorPoints(poly.Points);
        var result = new Poly(mirrored)
        {
            SourceId = poly.SourceId,
            Rotation = poly.Rotation,
            Mirrored = !poly.Mirrored,
            Tag = poly.Tag
        };
        foreach (var child in poly.Children)
        {
            var mc = Mirror(child);
            mc.Parent = result;
            result.Children.Add(mc);
        }
        return result;
    }

    public static List<Point2D> MirrorPoints(IReadOnlyList<Point2D> points)
    {
        var result = new List<Point2D>(points.Count);
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            result.Add(new Point2D(-p.X, p.Y, p.Exact));
        }
        return result;
    }

    /// <summary>
    /// 点与多边形关系：1 = 内部，0 = 边界上，-1 = 外部。射线法。
    /// </summary>
    public static int PointInPolygon(Point2D point, IReadOnlyList<Point2D> polygon)
    {
        int n = polygon.Count;
        if (n < 3)
        {
            return -1;
        }

        // 边界判断（含端点）
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var a = polygon[j];
            var b = polygon[i];
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double cross = (point.X - a.X) * dy - (point.Y - a.Y) * dx;
            if (Math.Abs(cross) > 1e-12)
            {
                continue;
            }

            double dot = (point.X - a.X) * dx + (point.Y - a.Y) * dy;
            double lenSq = dx * dx + dy * dy;
            if (dot >= 0 && dot <= lenSq)
            {
                return 0;
            }
        }

        bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var pi = polygon[i];
            var pj = polygon[j];
            if ((pi.Y > point.Y) != (pj.Y > point.Y))
            {
                double x = pi.X + (point.Y - pi.Y) * (pj.X - pi.X) / (pj.Y - pi.Y);
                if (point.X < x)
                {
                    inside = !inside;
                }
            }
        }
        return inside ? 1 : -1;
    }

    public static bool IsPointInPolygon(Point2D point, IReadOnlyList<Point2D> polygon) =>
        PointInPolygon(point, polygon) >= 0;

    /// <summary>Andrew 单调链凸包，返回逆时针、无重复点/共线点的凸包；点数不足 3 时返回原始点集。</summary>
    public static List<Point2D> ConvexHull(IReadOnlyList<Point2D> points)
    {
        if (points.Count < 3)
        {
            return new List<Point2D>(points);
        }

        var sorted = points
            .OrderBy(p => p.X)
            .ThenBy(p => p.Y)
            .ToList();

        var hull = new List<Point2D>(sorted.Count * 2);

        // 下凸包
        foreach (var p in sorted)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= 0)
            {
                hull.RemoveAt(hull.Count - 1);
            }
            hull.Add(p);
        }

        // 上凸包
        int lowerCount = hull.Count + 1;
        for (int i = sorted.Count - 2; i >= 0; i--)
        {
            var p = sorted[i];
            while (hull.Count >= lowerCount && Cross(hull[^2], hull[^1], p) <= 0)
            {
                hull.RemoveAt(hull.Count - 1);
            }
            hull.Add(p);
        }

        hull.RemoveAt(hull.Count - 1);
        return hull;
    }

    private static double Cross(Point2D o, Point2D a, Point2D b) =>
        (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    public static double PathLength(IReadOnlyList<Point2D> points, bool closed = true)
    {
        if (points.Count < 2)
        {
            return 0;
        }
        double sum = 0;
        for (int i = 1; i < points.Count; i++)
        {
            sum += (points[i] - points[i - 1]).Length;
        }
        if (closed)
        {
            sum += (points[0] - points[^1]).Length;
        }
        return sum;
    }

    /// <summary>确保绕向：positive=true 时逆时针（面积 &gt; 0）。返回是否发生了反转。</summary>
    public static bool EnsureOrientation(List<Point2D> points, bool positive)
    {
        bool isPositive = SignedArea(points) > 0;
        if (isPositive == positive)
        {
            return false;
        }
        points.Reverse();
        return true;
    }

    /// <summary>
    /// 形状哈希（量化到 1e-4），用于把几何完全相同的实例归到同一个 SourceId。
    /// 只对坐标做哈希，不含子轮廓。
    /// </summary>
    public static long ShapeHash(IReadOnlyList<Point2D> points)
    {
        unchecked
        {
            long hash = 1469598103934665603L;
            foreach (var p in points)
            {
                long qx = (long)Math.Round(p.X * 10000);
                long qy = (long)Math.Round(p.Y * 10000);
                hash = (hash ^ qx) * 1099511628211L;
                hash = (hash ^ qy) * 1099511628211L;
            }
            hash = (hash ^ points.Count) * 1099511628211L;
            return hash;
        }
    }
}
