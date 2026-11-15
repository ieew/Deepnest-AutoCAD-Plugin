using System.Collections.Concurrent;
using Clipper2Lib;

namespace DeepNest.Core.Geometry;

/// <summary>
/// 禁入/允许区域计算接口。对应原版 <c>getOuterNfp</c> / <c>getInnerNfp</c>。
/// 留成接口是为了将来可以把实现换成原生 C++（Boost.Polygon）而不影响上层。
/// </summary>
public interface INfpEngine
{
    /// <summary>
    /// 禁入区域：把 <paramref name="b"/> 放到此处会与 <paramref name="a"/> 的材料重叠。
    /// <paramref name="a"/> 可带孔（孔内会成为禁入区域的"洞" = 允许放置）。
    /// 返回的多边形坐标是 <paramref name="b"/> 第一个顶点应落到的位置。
    /// </summary>
    Poly? OuterNfp(Poly a, Poly b);

    /// <summary>
    /// 允许区域：<paramref name="part"/> 完全落在 <paramref name="container"/> 内部的可行位置集合。
    /// </summary>
    List<Poly>? InnerNfp(Poly container, Poly part);

    void ClearCache();
}

/// <summary>
/// NFP 实现（Clipper2）：
/// <para>禁入区域 = 填充 Minkowski 和(A外环, -B)，再按孔层级减去/加回「B 完全落在该轮廓内」的区域（腐蚀）。</para>
/// <para>允许区域 = 对 container 做腐蚀（erosion = 逐个采样点平移求交）。</para>
/// <para>说明：孔级腐蚀用采样点近似（凸件精确），摆放阶段还有精确布尔校验兜底，不会产生干涉。</para>
/// </summary>
public sealed class ClipperNfpEngine : INfpEngine
{
    private const int MaxErodeSamples = 64;
    private const double Eps = 1e-6;

    private readonly ConcurrentDictionary<string, Poly?> _outerCache = new();
    private readonly ConcurrentDictionary<string, List<Poly>?> _innerCache = new();

    public long HitCount;
    public long MissCount;

    /// <summary>
    /// 零件间隙（图纸单位）。计算 NFP 前先把每个零件外扩 Spacing/2，
    /// 于是两个零件之间自然留出 Spacing；孔按材料方向内缩 Spacing/2，
    /// 所以把零件排进别人的孔里时也是 Spacing 的间隙。
    /// 注意：只影响 NFP（禁入/允许区域），不改变零件本身几何。
    /// </summary>
    public double Spacing { get; set; }

    public Poly? OuterNfp(Poly a, Poly b)
    {
        string key = "O|" + Spacing.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "|" +
                     KeyOf(a) + "|" + KeyOf(b);
        if (_outerCache.TryGetValue(key, out var cached))
        {
            Interlocked.Increment(ref HitCount);
            return cached?.Clone();
        }

        Interlocked.Increment(ref MissCount);

        // 间隙：两侧各外扩 Spacing/2
        double half = Spacing * 0.5;
        var aSpaced = half > 0 ? OffsetPoly(a, half) ?? a : a;
        var bSpaced = half > 0 ? OffsetPoly(b, half) ?? b : b;

        var result = ComputeOuterNfp(aSpaced, bSpaced);
        _outerCache[key] = result;
        return result?.Clone();
    }

    public List<Poly>? InnerNfp(Poly container, Poly part)
    {
        string key = "I|" + Spacing.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "|" +
                     KeyOf(container) + "|" + KeyOf(part);
        if (_innerCache.TryGetValue(key, out var cached))
        {
            Interlocked.Increment(ref HitCount);
            return CloneList(cached);
        }

        Interlocked.Increment(ref MissCount);

        // 间隙：零件外扩 Spacing/2（板料不缩，即零件与板边留 Spacing/2）
        double half = Spacing * 0.5;
        var partSpaced = half > 0 ? OffsetPoly(part, half) ?? part : part;

        // 允许区域必然落在 container 的包围盒内，用它作为腐蚀的初始区域。
        // 注意：允许区域是"平移量"空间，数值本身可以超出 container 的包围盒
        // （零件坐标不在原点时），所以这里不能再按包围盒过滤。
        var region = ClipperBridge.PolygonSet(container);
        var result = Erode(region, partSpaced);

        List<Poly>? allowed = null;
        if (result.Count > 0)
        {
            allowed = new List<Poly>(result.Count);
            foreach (var poly in result)
            {
                if (Math.Abs(GeometryUtil.SignedArea(poly.Points)) <= Eps)
                {
                    continue;
                }
                allowed.Add(poly);
            }
            if (allowed.Count == 0)
            {
                allowed = null;
            }
        }

        _innerCache[key] = allowed;
        return CloneList(allowed);
    }

    public void ClearCache()
    {
        _outerCache.Clear();
        _innerCache.Clear();
    }

    /// <summary>
    /// 按间隙偏移多边形：delta &gt; 0 表示材料向外长。子轮廓（孔）按相反方向偏移，
    /// 孔中岛再反过来，如此交替 —— 对应"每层轮廓沿材料方向偏移"。
    /// 返回 null 表示偏移后该轮廓退化（例如孔被填平）。
    /// </summary>
    private static Poly? OffsetPoly(Poly poly, double delta)
    {
        if (Math.Abs(delta) < 1e-12)
        {
            return poly;
        }

        var ring = OffsetRing(poly.Points, delta);
        if (ring is null)
        {
            return null;
        }

        var result = new Poly(ring)
        {
            SourceId = poly.SourceId,
            Rotation = poly.Rotation,
            Mirrored = poly.Mirrored,
            Tag = poly.Tag
        };

        foreach (var child in poly.Children)
        {
            // 孔：反向偏移（缩小孔洞）
            var offsetChild = OffsetPoly(child, -delta);
            if (offsetChild is null)
            {
                continue; // 孔被填平，直接丢掉
            }

            offsetChild.Parent = result;
            result.Children.Add(offsetChild);
        }

        return result;
    }

    private static List<Point2D>? OffsetRing(IReadOnlyList<Point2D> ring, double delta)
    {
        if (ring.Count < 3)
        {
            return null;
        }

        // 先把输入环统一成逆时针，这样 delta > 0 就一定表示"这个区域变大"，
        // 不会被孔环的顺时针走向把符号翻过来。
        var inputPath = ClipperBridge.ToPath(ring);
        if (!Clipper.IsPositive(inputPath))
        {
            inputPath = Clipper.ReversePath(inputPath);
        }

        var paths = new PathsD { inputPath };
        var inflated = ClipperBridge.Inflate(paths, delta, 0.05);

        PathD? best = null;
        double bestArea = -1;
        foreach (var offsetPath in inflated)
        {
            if (offsetPath.Count < 3)
            {
                continue;
            }

            double area = Math.Abs(Clipper.Area(offsetPath));
            if (area > bestArea)
            {
                bestArea = area;
                best = offsetPath;
            }
        }

        if (best is null || bestArea <= Eps)
        {
            return null;
        }

        if (!Clipper.IsPositive(best))
        {
            best = Clipper.ReversePath(best);
        }

        return ClipperBridge.FromPath(best);
    }

    private static Poly? ComputeOuterNfp(Poly a, Poly b)
    {
        if (a.Points.Count < 3 || b.Points.Count < 3)
        {
            return null;
        }

        // 1) 只对 A 的外环做填充 Minkowski 和（结果无孔）
        var filled = MinkowskiFilled(a.Points, b.Points);
        if (filled is null)
        {
            return null;
        }

        // 2) 按层级处理孔：奇数层（孔）减去「B 完全落在其中」，偶数层（孔中岛）加回
        PathsD region = ClipperBridge.PolygonSet(filled);
        foreach (var child in a.Children)
        {
            region = ApplyChild(region, child, b, level: 1);
        }

        if (region.Count == 0)
        {
            return null;
        }

        var tree = ClipperBridge.UnionTree(region);
        return ClipperBridge.PickLargestOuter(tree);
    }

    private static PathsD ApplyChild(PathsD region, Poly child, Poly b, int level)
    {
        var eroded = Erode(ClipperBridge.PolygonSet(child), b);
        if (eroded.Count > 0)
        {
            var erodedPaths = ClipperBridge.PolygonSet(eroded);
            region = level % 2 == 1
                ? Clipper.Difference(region, erodedPaths, FillRule.NonZero, ClipperBridge.Precision)
                : Clipper.Union(region, erodedPaths, FillRule.NonZero, ClipperBridge.Precision);
        }

        foreach (var grandChild in child.Children)
        {
            region = ApplyChild(region, grandChild, b, level + 1);
        }

        return region;
    }

    /// <summary>
    /// Minkowski 和的填充区域：逐边卷积求并后去掉边界扫描产生的内部伪孔。
    /// </summary>
    private static Poly? MinkowskiFilled(IReadOnlyList<Point2D> aPoints, IReadOnlyList<Point2D> bPoints)
    {
        var negativeB = new List<Point2D>(bPoints.Count);
        foreach (var p in bPoints)
        {
            negativeB.Add(new Point2D(-p.X, -p.Y, p.Exact));
        }

        var quads = new PathsD();
        int n = aPoints.Count;
        int m = negativeB.Count;

        for (int i = 0; i < n; i++)
        {
            var a0 = aPoints[i];
            var a1 = aPoints[(i + 1) % n];

            for (int j = 0; j < m; j++)
            {
                var b0 = negativeB[j];
                var b1 = negativeB[(j + 1) % m];

                var quad = new PathD(4)
                {
                    new PointD(a0.X + b0.X, a0.Y + b0.Y),
                    new PointD(a1.X + b0.X, a1.Y + b0.Y),
                    new PointD(a1.X + b1.X, a1.Y + b1.Y),
                    new PointD(a0.X + b1.X, a0.Y + b1.Y)
                };

                if (!Clipper.IsPositive(quad))
                {
                    quad = Clipper.ReversePath(quad);
                }

                quads.Add(quad);
            }
        }

        if (quads.Count == 0)
        {
            return null;
        }

        var tree = ClipperBridge.UnionTree(quads);
        var outer = ClipperBridge.PickLargestOuter(tree);
        if (outer is null)
        {
            return null;
        }

        // 伪孔（边界扫描形成的内部空腔）一律丢弃
        outer.Children.Clear();
        return outer;
    }

    /// <summary>
    /// 腐蚀：{ t | 对 B 的所有采样点 p，t + p 都在 region 内 }。
    /// 初始区域必须取"外包启动区域"（不能直接用 region，那等价于强加 t ∈ region，
    /// 只有当 p=(0,0) 属于零件时才成立，零件不在原点时会整体错位）。
    /// 对凸 B 用顶点即精确；非凸件加边中点近似，最终由摆放阶段的精确校验兜底。
    /// </summary>
    private static List<Poly> Erode(PathsD region, Poly b)
    {
        if (region.Count == 0 || b.Points.Count < 3)
        {
            return new List<Poly>();
        }

        // region 的包围盒
        double rMinX = double.MaxValue, rMinY = double.MaxValue;
        double rMaxX = double.MinValue, rMaxY = double.MinValue;
        foreach (var path in region)
        {
            foreach (var p in path)
            {
                if (p.x < rMinX) rMinX = p.x;
                if (p.y < rMinY) rMinY = p.y;
                if (p.x > rMaxX) rMaxX = p.x;
                if (p.y > rMaxY) rMaxY = p.y;
            }
        }

        if (rMinX > rMaxX)
        {
            return new List<Poly>();
        }

        var partBounds = GeometryUtil.GetBounds(b.Points);

        // t ∈ [rMin − pMax, rMax − pMin]，是真实腐蚀区域的超集
        var start = new PathD(4)
        {
            new PointD(rMinX - partBounds.MaxX, rMinY - partBounds.MaxY),
            new PointD(rMaxX - partBounds.MinX, rMinY - partBounds.MaxY),
            new PointD(rMaxX - partBounds.MinX, rMaxY - partBounds.MinY),
            new PointD(rMinX - partBounds.MaxX, rMaxY - partBounds.MinY)
        };

        var current = new PathsD { start };

        foreach (var p in SamplePoints(b))
        {
            var shifted = TranslatePaths(region, -p.X, -p.Y);
            current = Clipper.Intersect(current, shifted, FillRule.NonZero, ClipperBridge.Precision);
            if (current.Count == 0)
            {
                return new List<Poly>();
            }
        }

        return ClipperBridge.ToPolyList(ClipperBridge.UnionTree(current));
    }

    private static List<Point2D> SamplePoints(Poly b)
    {
        var points = new List<Point2D>(b.Points.Count * 2);
        var src = b.Points;
        for (int i = 0; i < src.Count; i++)
        {
            var a = src[i];
            var c = src[(i + 1) % src.Count];
            points.Add(new Point2D(a.X, a.Y, a.Exact));
            points.Add(new Point2D((a.X + c.X) * 0.5, (a.Y + c.Y) * 0.5, false));
        }

        if (points.Count <= MaxErodeSamples)
        {
            return points;
        }

        var result = new List<Point2D>(MaxErodeSamples);
        for (int i = 0; i < MaxErodeSamples; i++)
        {
            result.Add(points[(int)((long)i * points.Count / MaxErodeSamples)]);
        }
        return result;
    }

    private static PathsD TranslatePaths(PathsD paths, double dx, double dy)
    {
        var result = new PathsD(paths.Count);
        foreach (var path in paths)
        {
            var translated = new PathD(path.Count);
            foreach (var p in path)
            {
                translated.Add(new PointD(p.x + dx, p.y + dy));
            }
            result.Add(translated);
        }
        return result;
    }

    private static void TranslatePolyInPlace(Poly poly, double dx, double dy)
    {
        for (int i = 0; i < poly.Points.Count; i++)
        {
            var p = poly.Points[i];
            poly.Points[i] = new Point2D(p.X + dx, p.Y + dy, p.Exact);
        }
        foreach (var child in poly.Children)
        {
            TranslatePolyInPlace(child, dx, dy);
        }
    }

    private static List<Poly>? CloneList(List<Poly>? source)
    {
        if (source is null)
        {
            return null;
        }
        var clones = new List<Poly>(source.Count);
        foreach (var p in source)
        {
            clones.Add(p.Clone());
        }
        return clones;
    }

    /// <summary>缓存键：几何来源 + 旋转 + 镜像；无来源标识时退化为坐标哈希。</summary>
    public static string KeyOf(Poly p)
    {
        string baseKey = p.SourceId >= 0
            ? "S" + p.SourceId
            : "H" + HashOf(p).ToString("X16");

        string rot = Math.Round(p.Rotation, 6).ToString("R");
        return baseKey + ":r" + rot + (p.Mirrored ? ":m" : string.Empty);
    }

    private static long HashOf(Poly p)
    {
        unchecked
        {
            long hash = GeometryUtil.ShapeHash(p.Points);
            foreach (var child in p.Children)
            {
                hash = (hash ^ HashOf(child)) * 1099511628211L;
            }
            hash = (hash ^ p.Children.Count) * 1099511628211L;
            return hash;
        }
    }
}
