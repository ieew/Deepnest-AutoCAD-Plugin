using Clipper2Lib;

namespace DeepNest.Core.Geometry;

/// <summary>
/// Clipper2 与内部 <see cref="Poly"/> 之间的桥梁。
/// 约定：外环逆时针（Area &gt; 0），孔顺时针（Area &lt; 0），与 Clipper2 NonZero 填充规则一致。
/// </summary>
public static class ClipperBridge
{
    /// <summary>坐标小数位精度（图纸单位，mm 级足够）。不使用原版 1e7 整型定标。</summary>
    public const int Precision = 4;

    private static readonly PathsD EmptyPaths = new();

    public static PathD ToPath(IReadOnlyList<Point2D> points)
    {
        var path = new PathD(points.Count);
        for (int i = 0; i < points.Count; i++)
        {
            path.Add(new PointD(points[i].X, points[i].Y));
        }
        return path;
    }

    public static List<Point2D> FromPath(PathD path, bool exact = true)
    {
        var result = new List<Point2D>(path.Count);
        foreach (var p in path)
        {
            result.Add(new Point2D(p.x, p.y, exact));
        }
        return result;
    }

    public static Poly ToPoly(PathD path, bool exact = true) => new(FromPath(path, exact));

    /// <summary>把多边形（含孔，按层级交替绕向）转成 Clipper 路径集合。</summary>
    public static PathsD PolygonSet(Poly poly)
    {
        var paths = new PathsD();
        AddPoly(paths, poly, 0);
        return paths;
    }

    public static PathsD PolygonSet(IEnumerable<Poly> polys)
    {
        var paths = new PathsD();
        foreach (var poly in polys)
        {
            AddPoly(paths, poly, 0);
        }
        return paths;
    }

    private static void AddPoly(PathsD paths, Poly poly, int level)
    {
        if (poly.Points.Count < 3)
        {
            return;
        }

        var path = ToPath(poly.Points);
        bool wantPositive = level % 2 == 0;
        if (Clipper.IsPositive(path) != wantPositive)
        {
            path = Clipper.ReversePath(path);
        }
        paths.Add(path);

        foreach (var child in poly.Children)
        {
            AddPoly(paths, child, level + 1);
        }
    }

    public static PolyTreeD UnionTree(PathsD subject)
    {
        var tree = new PolyTreeD();
        Clipper.BooleanOp(ClipType.Union, subject, EmptyPaths, tree, FillRule.NonZero, Precision);
        return tree;
    }

    public static Poly? PickLargestOuter(PolyTreeD tree)
    {
        PolyPathD? best = null;
        double bestArea = -1;
        for (int i = 0; i < tree.Count; i++)
        {
            var node = tree[i];
            var polygon = node.Polygon ?? new PathD();
            double area = Math.Abs(Clipper.Area(polygon));
            if (area > bestArea)
            {
                bestArea = area;
                best = node;
            }
        }
        return best is null ? null : ToPoly(best);
    }

    /// <summary>把 PolyPathD 节点（含其直接子节点作为孔）转成 <see cref="Poly"/>。</summary>
    public static Poly ToPoly(PolyPathD node)
    {
        var poly = ToPoly(node.Polygon ?? new PathD());
        for (int i = 0; i < node.Count; i++)
        {
            var child = ToPoly(node[i]);
            child.Parent = poly;
            poly.Children.Add(child);
        }
        // 外环保证逆时针，孔保证顺时针
        GeometryUtil.EnsureOrientation(poly.Points, true);
        foreach (var child in poly.Children)
        {
            GeometryUtil.EnsureOrientation(child.Points, false);
        }
        return poly;
    }

    public static List<Poly> ToPolyList(PolyTreeD tree)
    {
        var result = new List<Poly>(tree.Count);
        for (int i = 0; i < tree.Count; i++)
        {
            result.Add(ToPoly(tree[i]));
        }
        return result;
    }

    /// <summary>布尔并（NonZero），返回带孔的多边形列表。</summary>
    public static List<Poly> Union(PathsD subject)
    {
        var tree = UnionTree(subject);
        return ToPolyList(tree);
    }

    /// <summary>布尔差：subject - clip（两者都按 NonZero 解释）。</summary>
    public static List<Poly> Difference(PathsD subject, PathsD clip)
    {
        var tree = new PolyTreeD();
        Clipper.BooleanOp(ClipType.Difference, subject, clip, tree, FillRule.NonZero, Precision);
        return ToPolyList(tree);
    }

    public static List<Poly> Difference(Poly subject, IEnumerable<Poly> clip)
    {
        var clipPaths = PolygonSet(clip);
        return Difference(PolygonSet(subject), clipPaths);
    }

    public static List<Poly> Difference(IEnumerable<Poly> subject, IEnumerable<Poly> clip) =>
        Difference(PolygonSet(subject), PolygonSet(clip));

    /// <summary>以 miter 方式内外偏移（对应原版 ClipperOffset miterLimit=4）。</summary>
    public static PathsD Inflate(PathsD paths, double delta, double arcTolerance) =>
        Clipper.InflatePaths(paths, delta, JoinType.Miter, EndType.Polygon, 4.0, Precision, arcTolerance);

    public static List<Poly> Inflate(IEnumerable<Poly> polys, double delta, double arcTolerance) =>
        ToPolyList(UnionTree(Inflate(PolygonSet(polys), delta, arcTolerance)));

    /// <summary>消除自交（NonZero 简化）。</summary>
    public static List<Poly> Simplify(IEnumerable<Poly> polys)
    {
        var subject = PolygonSet(polys);
        var tree = UnionTree(subject);
        return ToPolyList(tree);
    }

    /// <summary>去重（相邻点距离小于 distance）+ 去共线点。</summary>
    public static Poly? CleanPolygon(Poly poly, double distance)
    {
        var cleaned = CleanPoints(poly.Points, distance);
        if (cleaned.Count < 3)
        {
            return null;
        }
        var result = new Poly(cleaned)
        {
            SourceId = poly.SourceId,
            Tag = poly.Tag,
            Rotation = poly.Rotation,
            Mirrored = poly.Mirrored
        };
        return result;
    }

    public static List<Point2D> CleanPoints(IReadOnlyList<Point2D> points, double distance)
    {
        var result = new List<Point2D>(points.Count);
        double distSq = distance * distance;

        foreach (var p in points)
        {
            if (result.Count > 0 && (result[^1] - p).LengthSq <= distSq)
            {
                continue;
            }
            result.Add(p);
        }

        while (result.Count > 1 && (result[0] - result[^1]).LengthSq <= distSq)
        {
            result.RemoveAt(result.Count - 1);
        }

        if (result.Count < 3)
        {
            return result;
        }

        // 去共线点
        var collinearFree = new List<Point2D>(result.Count);
        for (int i = 0; i < result.Count; i++)
        {
            var prev = collinearFree.Count > 0 ? collinearFree[^1] : result[(i - 1 + result.Count) % result.Count];
            var cur = result[i];
            var next = result[(i + 1) % result.Count];

            double cross = (cur.X - prev.X) * (next.Y - cur.Y) - (cur.Y - prev.Y) * (next.X - cur.X);
            double len = (next - prev).Length;
            if (Math.Abs(cross) <= 1e-12 * Math.Max(1.0, len))
            {
                continue;
            }
            collinearFree.Add(cur);
        }

        return collinearFree.Count >= 3 ? collinearFree : result;
    }

    public static double AreaOf(IEnumerable<Poly> polys) =>
        Clipper.Area(PolygonSet(polys));
}
