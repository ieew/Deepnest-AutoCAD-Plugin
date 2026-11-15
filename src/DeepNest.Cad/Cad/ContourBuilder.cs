using DeepNest.Core.Geometry;

namespace DeepNest.Cad.Cad;

public enum ContourKind
{
    /// <summary>全部由闭合轮廓构成。</summary>
    Closed,

    /// <summary>存在真实开口/缺口，已自动接合后参与排样（标黄）。</summary>
    OpenChord,

    /// <summary>无法构成可信轮廓（标红，由上层用外包矩形兜底）。</summary>
    Unusable
}

public sealed class BuiltContours
{
    public ContourKind Kind { get; set; } = ContourKind.Unusable;

    /// <summary>根轮廓（每个是"外轮廓 + 孔"）。</summary>
    public List<Poly> Roots { get; } = new();

    public List<string> Warnings { get; } = new();

    public int LoopCount { get; set; }

    public int OpenChainCount { get; set; }

    /// <summary>实际采用的端点焊接容差。大于用户设定值说明做过缺口修补。</summary>
    public double RepairTolerance { get; set; }

    /// <summary>修补后仍残留的开口端数量。</summary>
    public int OpenEndCount { get; set; }

    /// <summary>实际参与计算的几何包围盒（已剔除离群几何）。</summary>
    public Bounds2D KeptBounds { get; set; }

    /// <summary>被当作离群几何剔除的路径数。</summary>
    public int DroppedPathCount { get; set; }
}

/// <summary>
/// 把离散路径组装成闭合轮廓树：
/// 端点焊接（容差自适应放大） → 图遍历成环 → 包含关系建孔树 → 可信度校验。
///
/// 真实图纸里零件外轮廓经常是"断开的"：拐角处两段线差 2~20 个单位没接上。
/// 一旦按固定 0.1 容差焊接就拼不出外轮廓，只剩下角上的小缺口成环，
/// 于是"缺口"被当成了零件，而真实块比零件大 → 排版结果互相重叠。
/// 因此这里按"最先能完全闭合"的容差逐级尝试，并用包围盒覆盖率做可信度校验。
/// </summary>
public static class ContourBuilder
{
    private const double Eps = 1e-9;

    public static BuiltContours Build(
        IReadOnlyList<TessellatedPath> paths,
        double joinTolerance,
        double curveTolerance)
    {
        var result = new BuiltContours();
        if (joinTolerance <= 0)
        {
            joinTolerance = 0.1;
        }

        var closedLoops = new List<List<Point2D>>();
        var openChains = new List<List<Point2D>>();
        var allPoints = new List<Point2D>();

        var cleaned = new List<(List<Point2D> Points, bool Closed)>();
        double maxPathDiagonal = 0;
        foreach (var path in paths)
        {
            var points = Cleanup(path.Points);
            if (points.Count < 2)
            {
                continue;
            }

            var bounds = GeometryUtil.GetBounds(points);
            double pathDiagonal = Math.Sqrt(bounds.Width * bounds.Width + bounds.Height * bounds.Height);
            if (pathDiagonal > maxPathDiagonal)
            {
                maxPathDiagonal = pathDiagonal;
            }

            cleaned.Add((points, path.Closed));
        }

        // 离群几何剔除：块里常混入远处的杂物（另一个零件的残留、构造线等），
        // 一旦并进包围盒就会算出几万单位大的"零件"，排版必然大面积重叠。
        var clusterIndex = ClusterPaths(cleaned, Math.Max(joinTolerance * 10, maxPathDiagonal * 0.03));
        int chosenCluster = ChooseCluster(cleaned, clusterIndex);
        for (int i = 0; i < cleaned.Count; i++)
        {
            if (clusterIndex[i] != chosenCluster)
            {
                result.DroppedPathCount++;
                continue;
            }

            var points = cleaned[i].Points;
            allPoints.AddRange(points);
            if (cleaned[i].Closed && points.Count >= 3)
            {
                closedLoops.Add(points);
            }
            else
            {
                openChains.Add(points);
            }
        }

        result.OpenChainCount = openChains.Count;

        if (allPoints.Count == 0)
        {
            result.Kind = ContourKind.Unusable;
            return result;
        }

        var geometryBounds = GeometryUtil.GetBounds(allPoints);
        result.KeptBounds = geometryBounds;

        if (result.DroppedPathCount > 0)
        {
            result.Warnings.Add(
                $"[黄色] 已剔除 {result.DroppedPathCount} 条离群几何（与主体相距过远）。");
        }
        double diagonal = Math.Sqrt(
            geometryBounds.Width * geometryBounds.Width + geometryBounds.Height * geometryBounds.Height);

        double minArea = Math.Max(curveTolerance * curveTolerance, 1e-9);

        // 逐级放大焊接容差，取"最先能完全闭合"的那一级
        Attempt? best = null;
        foreach (var tolerance in BuildToleranceLadder(joinTolerance, diagonal))
        {
            var attempt = RunAttempt(closedLoops, openChains, tolerance);
            attempt.Loops.RemoveAll(loop => Math.Abs(GeometryUtil.SignedArea(loop)) <= minArea);
            attempt.MaxLoopArea = attempt.Loops.Count == 0
                ? 0
                : attempt.Loops.Max(loop => Math.Abs(GeometryUtil.SignedArea(loop)));
            attempt.Tolerance = tolerance;

            if (best is null ||
                attempt.OpenEndCount < best.OpenEndCount ||
                (attempt.OpenEndCount == best.OpenEndCount && attempt.MaxLoopArea > best.MaxLoopArea))
            {
                best = attempt;
            }

            if (attempt.OpenEndCount == 0 && attempt.Loops.Count > 0)
            {
                break; // 已经完全闭合，无需再放大
            }
        }

        if (best is null || best.Loops.Count == 0)
        {
            result.Kind = ContourKind.Unusable;
            result.Warnings.Add("块内没有任何可闭合的轮廓。");
            return result;
        }

        result.LoopCount = best.Loops.Count;
        result.RepairTolerance = best.Tolerance;
        result.OpenEndCount = best.OpenEndCount;

        // 建树（偶数层外轮廓、奇数层孔）
        BuildTree(best.Loops, result);
        if (result.Roots.Count == 0)
        {
            result.Kind = ContourKind.Unusable;
            result.Warnings.Add("块内轮廓无法构成外轮廓。");
            return result;
        }

        var root = result.Roots
            .OrderByDescending(p => Math.Abs(GeometryUtil.SignedArea(p.Points)))
            .First();

        // 可信度校验：外轮廓必须覆盖块内几何的大部分范围，
        // 否则说明真正的轮廓没合上（只剩小缺口/小孔成环）。
        if (!IsTrustworthy(root, geometryBounds, out string? reason))
        {
            result.Roots.Clear();
            result.Kind = ContourKind.Unusable;
            if (reason is not null)
            {
                result.Warnings.Add(reason);
            }
            return result;
        }

        // 只保留最大的一块，语义更明确
        result.Roots.Clear();
        result.Roots.Add(root);

        bool repaired = best.OpenEndCount > 0 || best.Tolerance > joinTolerance * 1.5;
        result.Kind = repaired ? ContourKind.OpenChord : ContourKind.Closed;
        if (repaired)
        {
            result.Warnings.Add(
                $"[黄色] 轮廓存在缺口/断裂，已用 {best.Tolerance:0.###} 的焊接容差自动接合。");
        }

        return result;
    }

    /// <summary>按包围盒邻近度做连通聚类（单连接），返回每条路径的簇号。</summary>
    private static int[] ClusterPaths(List<(List<Point2D> Points, bool Closed)> paths, double threshold)
    {
        int count = paths.Count;
        var cluster = new int[count];
        if (count == 0)
        {
            return cluster;
        }

        if (threshold <= 0)
        {
            threshold = 1e-9;
        }

        var bounds = new Bounds2D[count];
        for (int i = 0; i < count; i++)
        {
            bounds[i] = GeometryUtil.GetBounds(paths[i].Points);
        }

        var parent = new int[count];
        for (int i = 0; i < count; i++)
        {
            parent[i] = i;
        }

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        for (int i = 0; i < count; i++)
        {
            for (int j = i + 1; j < count; j++)
            {
                if (BoundsDistance(bounds[i], bounds[j]) > threshold)
                {
                    continue;
                }

                int a = Find(i);
                int b = Find(j);
                if (a != b)
                {
                    parent[b] = a;
                }
            }
        }

        for (int i = 0; i < count; i++)
        {
            cluster[i] = Find(i);
        }

        return cluster;
    }

    /// <summary>取"路径数最多、其次范围最大"的那一簇，作为零件主体。</summary>
    private static int ChooseCluster(List<(List<Point2D> Points, bool Closed)> paths, int[] clusterIndex)
    {
        var counts = new Dictionary<int, int>();
        var minX = new Dictionary<int, double>();
        var minY = new Dictionary<int, double>();
        var maxX = new Dictionary<int, double>();
        var maxY = new Dictionary<int, double>();

        for (int i = 0; i < paths.Count; i++)
        {
            int key = clusterIndex[i];
            var bounds = GeometryUtil.GetBounds(paths[i].Points);

            if (!counts.TryGetValue(key, out int c))
            {
                counts[key] = 1;
                minX[key] = bounds.MinX;
                minY[key] = bounds.MinY;
                maxX[key] = bounds.MaxX;
                maxY[key] = bounds.MaxY;
                continue;
            }

            counts[key] = c + 1;
            if (bounds.MinX < minX[key])
            {
                minX[key] = bounds.MinX;
            }
            if (bounds.MinY < minY[key])
            {
                minY[key] = bounds.MinY;
            }
            if (bounds.MaxX > maxX[key])
            {
                maxX[key] = bounds.MaxX;
            }
            if (bounds.MaxY > maxY[key])
            {
                maxY[key] = bounds.MaxY;
            }
        }

        int best = clusterIndex.Length > 0 ? clusterIndex[0] : 0;
        int bestCount = -1;
        double bestArea = -1;

        foreach (var pair in counts)
        {
            int key = pair.Key;
            double area = (maxX[key] - minX[key]) * (maxY[key] - minY[key]);
            if (pair.Value > bestCount || (pair.Value == bestCount && area > bestArea))
            {
                best = key;
                bestCount = pair.Value;
                bestArea = area;
            }
        }

        return best;
    }

    private static double BoundsDistance(in Bounds2D a, in Bounds2D b)
    {
        double dx = Math.Max(0, Math.Max(a.MinX - b.MaxX, b.MinX - a.MaxX));
        double dy = Math.Max(0, Math.Max(a.MinY - b.MaxY, b.MinY - a.MaxY));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static bool IsTrustworthy(Poly root, in Bounds2D geometryBounds, out string? reason)
    {
        reason = null;
        if (geometryBounds.Width <= Eps && geometryBounds.Height <= Eps)
        {
            return true;
        }

        var rootBounds = root.Bounds;

        // 外轮廓至少要在两个方向上都覆盖几何范围的一半
        if (geometryBounds.Width > Eps && rootBounds.Width < geometryBounds.Width * 0.5)
        {
            reason =
                $"外轮廓宽度 {rootBounds.Width:0.##} 远小于块内几何宽度 {geometryBounds.Width:0.##}，已改用外包矩形。";
            return false;
        }

        if (geometryBounds.Height > Eps && rootBounds.Height < geometryBounds.Height * 0.5)
        {
            reason =
                $"外轮廓高度 {rootBounds.Height:0.##} 远小于块内几何高度 {geometryBounds.Height:0.##}，已改用外包矩形。";
            return false;
        }

        return true;
    }

    private static List<double> BuildToleranceLadder(double baseTolerance, double diagonal)
    {
        var candidates = new List<double> { baseTolerance, baseTolerance * 3, 1, 3, 10 };
        if (diagonal > Eps)
        {
            candidates.Add(diagonal * 0.005);
            candidates.Add(diagonal * 0.01);
            candidates.Add(diagonal * 0.03);
        }

        double cap = diagonal > Eps ? diagonal * 0.05 : baseTolerance * 100;
        if (cap < baseTolerance)
        {
            cap = baseTolerance;
        }

        var ladder = new List<double>();
        foreach (var value in candidates.OrderBy(v => v))
        {
            if (value < baseTolerance - 1E-12 || value > cap)
            {
                continue;
            }

            if (ladder.Count > 0 && Math.Abs(ladder[^1] - value) < 1E-9)
            {
                continue;
            }

            ladder.Add(value);
        }

        if (ladder.Count == 0)
        {
            ladder.Add(baseTolerance);
        }

        return ladder;
    }

    private sealed class Attempt
    {
        public List<List<Point2D>> Loops { get; } = new();
        public int OpenEndCount { get; set; }
        public double MaxLoopArea { get; set; }
        public double Tolerance { get; set; }
    }

    private static Attempt RunAttempt(
        List<List<Point2D>> closedLoops,
        List<List<Point2D>> openChains,
        double tolerance)
    {
        var attempt = new Attempt { Tolerance = tolerance };
        foreach (var loop in closedLoops)
        {
            attempt.Loops.Add(loop);
        }

        if (openChains.Count > 0)
        {
            attempt.OpenEndCount = JoinChains(openChains, tolerance, attempt.Loops);
        }

        return attempt;
    }

    private static List<Point2D> Cleanup(IReadOnlyList<Point2D> source)
    {
        var points = new List<Point2D>(source.Count);
        foreach (var p in source)
        {
            if (points.Count > 0)
            {
                var last = points[^1];
                if (Math.Abs(last.X - p.X) < Eps && Math.Abs(last.Y - p.Y) < Eps)
                {
                    continue;
                }
            }
            points.Add(p);
        }

        while (points.Count > 1)
        {
            var first = points[0];
            var last = points[^1];
            if (Math.Abs(first.X - last.X) < 1E-7 && Math.Abs(first.Y - last.Y) < 1E-7)
            {
                points.RemoveAt(points.Count - 1);
            }
            else
            {
                break;
            }
        }

        return points;
    }

    /// <summary>
    /// 端点焊接 + 图遍历，把开放链拼成闭合环。
    /// 返回"仍未接上的开口端数量"（0 = 全部闭合）。
    /// </summary>
    private static int JoinChains(
        List<List<Point2D>> chains,
        double tolerance,
        List<List<Point2D>> closedLoops)
    {
        var nodes = new List<Point2D>();
        var grid = new Dictionary<(long, long), List<int>>();
        double cell = Math.Max(tolerance, 1e-9);
        double tolSq = tolerance * tolerance;

        int GetNode(Point2D p)
        {
            long cx = (long)Math.Floor(p.X / cell);
            long cy = (long)Math.Floor(p.Y / cell);
            for (long dx = -1; dx <= 1; dx++)
            {
                for (long dy = -1; dy <= 1; dy++)
                {
                    if (!grid.TryGetValue((cx + dx, cy + dy), out var list))
                    {
                        continue;
                    }
                    foreach (var index in list)
                    {
                        var q = nodes[index];
                        if ((q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y) <= tolSq)
                        {
                            return index;
                        }
                    }
                }
            }

            int id = nodes.Count;
            nodes.Add(p);
            var key = (cx, cy);
            if (!grid.TryGetValue(key, out var bucket))
            {
                bucket = new List<int>();
                grid[key] = bucket;
            }
            bucket.Add(id);
            return id;
        }

        var edges = new List<(int ChainIndex, int A, int B)>();
        for (int i = 0; i < chains.Count; i++)
        {
            var chain = chains[i];
            int a = GetNode(chain[0]);
            int b = GetNode(chain[^1]);
            if (a == b && chain.Count >= 3)
            {
                closedLoops.Add(chain);
            }
            edges.Add((i, a, b));
        }

        var adjacency = new Dictionary<int, List<int>>();
        for (int i = 0; i < edges.Count; i++)
        {
            var edge = edges[i];
            if (edge.A == edge.B)
            {
                continue;
            }
            Add(adjacency, edge.A, i);
            Add(adjacency, edge.B, i);
        }

        var used = new bool[edges.Count];

        for (int start = 0; start < edges.Count; start++)
        {
            if (used[start] || edges[start].A == edges[start].B)
            {
                continue;
            }

            var walk = new List<Point2D>();
            int startNode = edges[start].A;
            int currentNode = startNode;
            int currentEdge = start;
            bool closed = false;

            while (true)
            {
                used[currentEdge] = true;
                var edge = edges[currentEdge];

                int nextNode = edge.A == currentNode ? edge.B : edge.A;
                var chain = chains[edge.ChainIndex];
                bool reversed = edge.B == currentNode;

                if (walk.Count == 0)
                {
                    AppendAll(walk, chain, reversed);
                }
                else
                {
                    AppendSkipFirst(walk, chain, reversed);
                }

                currentNode = nextNode;

                if (currentNode == startNode)
                {
                    closed = true;
                    break;
                }

                int nextEdge = -1;
                if (adjacency.TryGetValue(currentNode, out var candidates))
                {
                    foreach (var candidate in candidates)
                    {
                        if (!used[candidate] && edges[candidate].A != edges[candidate].B)
                        {
                            nextEdge = candidate;
                            break;
                        }
                    }
                }

                if (nextEdge < 0)
                {
                    break;
                }

                currentEdge = nextEdge;
            }

            if (closed && walk.Count >= 3)
            {
                closedLoops.Add(Cleanup(walk));
            }
        }

        // 统计仍然没有接上的开口端
        var openNodes = new HashSet<int>();
        for (int i = 0; i < edges.Count; i++)
        {
            if (used[i] || edges[i].A == edges[i].B)
            {
                continue;
            }
            openNodes.Add(edges[i].A);
            openNodes.Add(edges[i].B);
        }

        return openNodes.Count;
    }

    private static void Add(Dictionary<int, List<int>> map, int key, int value)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<int>();
            map[key] = list;
        }
        list.Add(value);
    }

    private static void AppendAll(List<Point2D> target, List<Point2D> chain, bool reversed)
    {
        if (reversed)
        {
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                target.Add(chain[i]);
            }
        }
        else
        {
            target.AddRange(chain);
        }
    }

    private static void AppendSkipFirst(List<Point2D> target, List<Point2D> chain, bool reversed)
    {
        if (reversed)
        {
            for (int i = chain.Count - 2; i >= 0; i--)
            {
                target.Add(chain[i]);
            }
        }
        else
        {
            for (int i = 1; i < chain.Count; i++)
            {
                target.Add(chain[i]);
            }
        }
    }

    private static void BuildTree(List<List<Point2D>> loops, BuiltContours result)
    {
        int count = loops.Count;
        var areas = new double[count];
        var bounds = new Bounds2D[count];
        for (int i = 0; i < count; i++)
        {
            areas[i] = Math.Abs(GeometryUtil.SignedArea(loops[i]));
            bounds[i] = GeometryUtil.GetBounds(loops[i]);
        }

        // 按面积从大到小，逐个找"最小的包含者"作为父级
        var order = Enumerable.Range(0, count).OrderByDescending(i => areas[i]).ToArray();
        var parent = Enumerable.Repeat(-1, count).ToArray();
        var depth = new int[count];

        foreach (var i in order)
        {
            var sample = loops[i][0];
            int best = -1;
            double bestArea = double.MaxValue;

            foreach (var j in order)
            {
                if (j == i || areas[j] <= areas[i])
                {
                    continue;
                }
                if (!ContainsBounds(bounds[j], bounds[i]))
                {
                    continue;
                }
                if (areas[j] >= bestArea)
                {
                    continue;
                }
                if (GeometryUtil.PointInPolygon(sample, loops[j]) < 0)
                {
                    continue;
                }

                best = j;
                bestArea = areas[j];
            }

            parent[i] = best;
        }

        // 计算层级
        for (int i = 0; i < count; i++)
        {
            int d = 0;
            int p = parent[i];
            while (p >= 0)
            {
                d++;
                p = parent[p];
            }
            depth[i] = d;
        }

        var polys = new Poly?[count];
        for (int i = 0; i < count; i++)
        {
            if (depth[i] % 2 == 0)
            {
                polys[i] = new Poly(loops[i]);
            }
        }

        for (int i = 0; i < count; i++)
        {
            if (depth[i] % 2 == 0)
            {
                continue;
            }

            // 奇数层 = 孔，挂到最近的偶数层祖先
            int p = parent[i];
            while (p >= 0 && depth[p] % 2 != 0)
            {
                p = parent[p];
            }
            if (p >= 0 && polys[p] is not null)
            {
                var hole = new Poly(loops[i]);
                hole.Parent = polys[p];
                polys[p]!.Children.Add(hole);
            }
        }

        for (int i = 0; i < count; i++)
        {
            if (depth[i] % 2 == 0 && polys[i] is not null)
            {
                result.Roots.Add(polys[i]!);
            }
        }
    }

    private static bool ContainsBounds(in Bounds2D outer, in Bounds2D inner) =>
        inner.MinX >= outer.MinX - 1e-6 && inner.MinY >= outer.MinY - 1e-6 &&
        inner.MaxX <= outer.MaxX + 1e-6 && inner.MaxY <= outer.MaxY + 1e-6;
}
