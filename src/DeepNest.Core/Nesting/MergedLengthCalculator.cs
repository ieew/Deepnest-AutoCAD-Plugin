using DeepNest.Core.Geometry;

namespace DeepNest.Core.Nesting;

/// <summary>
/// 共边（重合边）合并长度计算。对应原 <c>background.js</c> 的 <c>mergedLength</c>：
/// 只有两端点都是图纸原始顶点（<see cref="Point2D.Exact"/>）且长度超过阈值的边才参与。
/// </summary>
public static class MergedLengthCalculator
{
    public static (double Length, List<Segment> Segments) Compute(
        IReadOnlyList<Poly> placedParts,
        Poly movingPart,
        double minLength,
        double tolerance)
    {
        double total = 0;
        var segments = new List<Segment>();

        if (minLength <= 0)
        {
            minLength = 1e-9;
        }
        double minLengthSq = minLength * minLength;

        var movingEdges = CollectEdges(movingPart, minLength).ToList();
        if (movingEdges.Count == 0)
        {
            return (0, segments);
        }

        var placedEdges = new List<(Point2D A, Point2D B)>();
        foreach (var placed in placedParts)
        {
            placedEdges.AddRange(CollectEdges(placed, minLength));
        }
        if (placedEdges.Count == 0)
        {
            return (0, segments);
        }

        foreach (var (a1, a2) in movingEdges)
        {
            double dx = a2.X - a1.X;
            double dy = a2.Y - a1.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length <= 0)
            {
                continue;
            }

            double ux = dx / length;
            double uy = dy / length;

            foreach (var (b1, b2) in placedEdges)
            {
                // 把 B 边变换到 A 边的局部坐标系（A1 为原点，x 轴沿 A 边）
                double bx1 = b1.X - a1.X;
                double by1 = b1.Y - a1.Y;
                double bx2 = b2.X - a1.X;
                double by2 = b2.Y - a1.Y;

                double y1 = -bx1 * uy + by1 * ux;
                double y2 = -bx2 * uy + by2 * ux;
                if (Math.Abs(y1) > tolerance || Math.Abs(y2) > tolerance)
                {
                    continue;
                }

                double x1 = bx1 * ux + by1 * uy;
                double x2 = bx2 * ux + by2 * uy;

                double low = Math.Max(0, Math.Min(x1, x2));
                double high = Math.Min(length, Math.Max(x1, x2));
                double overlap = high - low;

                if (overlap <= 0 || overlap * overlap <= minLengthSq)
                {
                    continue;
                }

                total += overlap;
                segments.Add(new Segment(
                    new Point2D(a1.X + ux * low, a1.Y + uy * low),
                    new Point2D(a1.X + ux * high, a1.Y + uy * high)));
            }
        }

        return (total, segments);
    }

    private static IEnumerable<(Point2D A, Point2D B)> CollectEdges(Poly poly, double minLength)
    {
        var points = poly.Points;
        for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
        {
            var a = points[j];
            var b = points[i];
            if (!a.Exact || !b.Exact)
            {
                continue;
            }
            if ((b - a).LengthSq < minLength * minLength)
            {
                continue;
            }
            yield return (a, b);
        }

        foreach (var child in poly.Children)
        {
            foreach (var edge in CollectEdges(child, minLength))
            {
                yield return edge;
            }
        }
    }
}
