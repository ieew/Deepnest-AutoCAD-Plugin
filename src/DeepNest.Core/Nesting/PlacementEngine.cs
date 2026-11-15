using Clipper2Lib;
using DeepNest.Core.Geometry;

namespace DeepNest.Core.Nesting;

/// <summary>
/// 顺序贪心摆放。逐行对齐原 <c>background.js</c> 的 <c>placeParts</c>：
/// 首件贴角 → 已放置件的 NFP 求并 → 与板料允许区域求差 → 顶点逐个评分（gravity/box/convexhull）
/// → 取最优。板料按需生成，直到零件放完或达到上限。
/// </summary>
public sealed class PlacementEngine
{
    private readonly INfpEngine _nfp;
    private readonly NestConfig _config;

    public PlacementEngine(INfpEngine nfp, NestConfig config)
    {
        _nfp = nfp;
        _config = config;
    }

    private sealed class Item
    {
        public required Poly Poly;
        public required int InstanceId;
        public required int SourceId;
        public double Rotation;
        public bool Mirrored;
    }

    public NestResult Place(
        SheetTemplate sheet,
        IReadOnlyList<PartInstance> instances,
        IReadOnlyList<Gene> genes,
        CancellationToken cancellationToken,
        Action<double>? progress = null)
    {
        var instanceById = instances.ToDictionary(i => i.Id);

        var parts = new List<Item>(genes.Count);
        foreach (var gene in genes)
        {
            if (!instanceById.TryGetValue(gene.InstanceId, out var instance))
            {
                continue;
            }

            var poly = instance.Shape.Clone();
            poly.SourceId = instance.SourceId;
            poly.Rotation = 0;
            poly.Mirrored = false;

            if (gene.Mirrored && _config.AllowMirror && instance.AllowMirror)
            {
                poly = GeometryUtil.Mirror(poly);
            }
            if (gene.RotationIndex != 0)
            {
                poly = GeometryUtil.Rotate(poly, gene.RotationIndex * _config.RotationStep);
            }

            parts.Add(new Item
            {
                Poly = poly,
                InstanceId = instance.Id,
                SourceId = instance.SourceId,
                Rotation = poly.Rotation,
                Mirrored = poly.Mirrored
            });
        }

        var result = new NestResult();
        double fitness = 0;
        double totalSheetArea = 0;
        double totalMerged = 0;
        int totalParts = parts.Count;
        int placedTotal = 0;

        var remaining = parts;

        for (int sheetIndex = 0; sheetIndex < _config.MaxSheets && remaining.Count > 0; sheetIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sheetShape = sheet.Shape;
            double sheetArea = Math.Abs(GeometryUtil.SignedArea(sheetShape.Points));
            if (sheetArea <= 0)
            {
                break;
            }

            totalSheetArea += sheetArea;
            fitness += sheetArea;

            var placed = new List<Item>();
            var placements = new List<Placement>();

            var sheetOuterSet = ClipperBridge.PolygonSet(sheetShape.Clone(withChildren: false));

            double minWidth = 0;
            double minArea = 0;

            for (int i = 0; i < remaining.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var part = remaining[i];
                List<Poly>? sheetNfp = null;

                // 每件都尝试换角度直到能放进板料（原版行为：会就地累加到 remaining[i]）
                for (int attempt = 0; attempt < _config.RotationAttempts; attempt++)
                {
                    sheetNfp = sheet.AllowHoles
                        ? _nfp.InnerNfp(sheetShape, part.Poly)
                        : InnerNfpWithoutHoles(sheetShape, part.Poly);

                    if (sheetNfp is { Count: > 0 })
                    {
                        break;
                    }

                    var rotated = GeometryUtil.Rotate(part.Poly, _config.RotationStep);
                    part.Poly = rotated;
                    part.Rotation = rotated.Rotation;
                    part.Mirrored = rotated.Mirrored;
                    remaining[i] = part;

                    if (part.Rotation >= 360)
                    {
                        part.Rotation %= 360;
                    }
                }

                if (sheetNfp is null || sheetNfp.Count == 0)
                {
                    continue; // 该件在这张板上放不下
                }

                if (placed.Count == 0)
                {
                    // 首件：贴左下角（原版贴左上角，因 SVG 的 Y 向下）。
                    // 同样先排序再校验，避免落到 NFP 伪影区域。
                    var firstCandidates = new List<(double X, double Y)>();
                    foreach (var nf in sheetNfp)
                    {
                        foreach (var point in nf.Points)
                        {
                            firstCandidates.Add((point.X, point.Y));
                        }
                    }

                    firstCandidates.Sort((a, b) =>
                    {
                        int cmp = a.X.CompareTo(b.X);
                        return cmp != 0 ? cmp : a.Y.CompareTo(b.Y);
                    });

                    Placement? firstPlacement = null;
                    foreach (var (x, y) in firstCandidates)
                    {
                        if (!IsPlacementValid(part.Poly, x, y, placed, placements, sheetOuterSet))
                        {
                            continue;
                        }

                        firstPlacement = CreatePlacement(part, x, y, 0, null, null, null);
                        break;
                    }

                    if (firstPlacement is null)
                    {
                        continue;
                    }

                    placements.Add(firstPlacement);
                    placed.Add(part);
                    placedTotal++;
                    ReportProgress(progress, placedTotal, totalParts);
                    continue;
                }

                // 已放置件的禁入区域求并
                var forbidden = new PathsD();
                bool failed = false;
                for (int j = 0; j < placed.Count; j++)
                {
                    var nfp = _nfp.OuterNfp(placed[j].Poly, part.Poly);
                    if (nfp is null)
                    {
                        failed = true;
                        break;
                    }
                    TranslatePoly(nfp, placements[j].X, placements[j].Y);
                    forbidden.AddRange(ClipperBridge.PolygonSet(nfp));
                }

                if (failed)
                {
                    continue;
                }

                var combined = Clipper.Union(forbidden, FillRule.NonZero);
                if (combined.Count == 0)
                {
                    continue;
                }

                // 允许区域 - 禁入区域
                var sheetPaths = ClipperBridge.PolygonSet(sheetNfp);
                var finalPaths = Clipper.Difference(sheetPaths, combined, FillRule.NonZero, ClipperBridge.Precision);
                if (finalPaths.Count == 0)
                {
                    continue;
                }

                var candidates = new List<Poly>(finalPaths.Count);
                foreach (var path in finalPaths)
                {
                    if (path.Count < 3)
                    {
                        continue;
                    }
                    if (Math.Abs(Clipper.Area(path)) <= 1e-6)
                    {
                        continue;
                    }
                    candidates.Add(ClipperBridge.ToPoly(path));
                }

                if (candidates.Count == 0)
                {
                    continue;
                }

                // 候选点评分
                var (allPoints, allBounds) = CollectPlacedPoints(placed, placements);
                var partBounds = GeometryUtil.GetBounds(part.Poly.Points);
                List<Point2D>? hullPoints = null;
                if (_config.PlacementType == PlacementType.ConvexHull)
                {
                    hullPoints = GeometryUtil.ConvexHull(allPoints);
                }

                List<Poly>? shiftedPlaced = null;

                if (_config.MergeLines)
                {
                    shiftedPlaced = new List<Poly>(placed.Count);
                    for (int m = 0; m < placed.Count; m++)
                    {
                        var clone = placed[m].Poly.Clone();
                        TranslatePoly(clone, placements[m].X, placements[m].Y);
                        shiftedPlaced.Add(clone);
                    }
                }

                var scored = new List<ScoredCandidate>();

                foreach (var candidate in candidates)
                {
                    foreach (var point in candidate.Points)
                    {
                        // NFP / 允许区域都是"平移量"空间：候选值直接就是 t
                        double shiftX = point.X;
                        double shiftY = point.Y;

                        double area;
                        double width = 0;
                        List<Point2D>? hull = null;
                        List<Point2D>? hullSheet = null;

                        if (_config.PlacementType == PlacementType.ConvexHull)
                        {
                            var localPoints = new List<Point2D>(hullPoints!);
                            foreach (var p in part.Poly.Points)
                            {
                                localPoints.Add(new Point2D(p.X + shiftX, p.Y + shiftY, p.Exact));
                            }
                            hull = GeometryUtil.ConvexHull(localPoints);
                            area = Math.Abs(GeometryUtil.SignedArea(hull));
                            hullSheet = GeometryUtil.ConvexHull(sheetShape.Points);
                        }
                        else
                        {
                            var partShifted = new Bounds2D(
                                partBounds.MinX + shiftX,
                                partBounds.MinY + shiftY,
                                partBounds.MaxX + shiftX,
                                partBounds.MaxY + shiftY);
                            var rect = allBounds.Union(partShifted);
                            width = rect.Width;
                            area = _config.PlacementType == PlacementType.Gravity
                                ? rect.Width * 2 + rect.Height
                                : rect.Width * rect.Height;
                        }

                        double mergedLength = 0;
                        List<Segment>? mergedSegments = null;

                        if (_config.MergeLines)
                        {
                            var shiftedPart = part.Poly.Clone();
                            TranslatePoly(shiftedPart, shiftX, shiftY);
                            var (length, segments) = MergedLengthCalculator.Compute(
                                shiftedPlaced!, shiftedPart, _config.MinMergeLength,
                                0.1 * _config.CurveTolerance);
                            mergedLength = length;
                            mergedSegments = segments;
                            area -= mergedLength * _config.TimeRatio;
                        }

                        scored.Add(new ScoredCandidate(area, shiftX, shiftY, width, mergedLength, mergedSegments, hull, hullSheet));
                    }
                }

                if (scored.Count == 0)
                {
                    continue;
                }

                // 评分升序；同分时先 x 后 y（左下优先）。
                // NFP 只作为候选生成器，是否合法由精确的布尔相交检查决定，
                // 这样即使 NFP 有近似误差也不会产生干涉。
                scored.Sort((a, b) =>
                {
                    int cmp = a.Area.CompareTo(b.Area);
                    if (Math.Abs(a.Area - b.Area) <= GeometryUtil.Tolerance)
                    {
                        cmp = 0;
                    }
                    if (cmp == 0)
                    {
                        cmp = a.X.CompareTo(b.X);
                    }
                    if (cmp == 0)
                    {
                        cmp = a.Y.CompareTo(b.Y);
                    }
                    return cmp;
                });

                Placement? chosen = null;
                foreach (var candidate in scored)
                {
                    if (!IsPlacementValid(part.Poly, candidate.X, candidate.Y, placed, placements, sheetOuterSet))
                    {
                        continue;
                    }

                    chosen = CreatePlacement(part, candidate.X, candidate.Y, candidate.MergedLength,
                        candidate.MergedSegments, candidate.Hull, candidate.HullSheet);
                    minWidth = candidate.Width;
                    minArea = candidate.Area;
                    break;
                }

                if (chosen is not null)
                {
                    placements.Add(chosen);
                    placed.Add(part);
                    placedTotal++;
                    totalMerged += chosen.MergedLength;
                    ReportProgress(progress, placedTotal, totalParts);
                }
            }

            fitness += (minWidth / sheetArea) + minArea;

            remaining = remaining.Where(p => !placed.Contains(p)).ToList();

            if (placements.Count == 0)
            {
                break; // 这张板一件也放不下，避免空转
            }

            var sheetPlacement = new SheetPlacement { SheetIndex = sheetIndex };
            sheetPlacement.Placements.AddRange(placements);
            result.Sheets.Add(sheetPlacement);
        }

        // 绝不丢件：仍未能放置的零件，每件强制放到一张新板的左下角（需人工微调）
        int forcedCount = 0;
        if (remaining.Count > 0)
        {
            double forcedSheetArea = Math.Abs(GeometryUtil.SignedArea(sheet.Shape.Points));
            if (forcedSheetArea <= 0)
            {
                forcedSheetArea = 1;
            }

            foreach (var part in remaining)
            {
                var bounds = GeometryUtil.GetBounds(part.Poly.Points);

                var forcedSheet = new SheetPlacement { SheetIndex = result.Sheets.Count };
                forcedSheet.Placements.Add(CreatePlacement(
                    part,
                    -bounds.MinX,
                    -bounds.MinY,
                    0,
                    null,
                    null,
                    null,
                    forced: true));

                result.Sheets.Add(forcedSheet);
                totalSheetArea += forcedSheetArea;
                fitness += forcedSheetArea;
                placedTotal++;
                forcedCount++;
            }

            remaining = new List<Item>();
        }

        result.Fitness = fitness;
        result.TotalSheetArea = totalSheetArea;
        result.MergedLength = totalMerged;
        result.PlacedCount = placedTotal;
        result.UnplacedCount = 0;
        result.ForcedCount = forcedCount;
        return result;
    }

    /// <summary>板料不支持孔时的允许区域：外框内、且不与板料边界相交的位置。</summary>
    private List<Poly>? InnerNfpWithoutHoles(Poly sheet, Poly part)
    {
        var noHoles = sheet.Clone(withChildren: false);
        return _nfp.InnerNfp(noHoles, part);
    }

    private readonly record struct ScoredCandidate(
        double Area,
        double X,
        double Y,
        double Width,
        double MergedLength,
        List<Segment>? MergedSegments,
        List<Point2D>? Hull,
        List<Point2D>? HullSheet);

    /// <summary>
    /// 精确合法性校验（不依赖 NFP 的精度）：
    /// 1) 必须完全落在板料外轮廓内；
    /// 2) 不得与已放置零件的材料相交（其他零件的孔内是允许的）。
    /// </summary>
    private static bool IsPlacementValid(
        Poly part,
        double shiftX,
        double shiftY,
        List<Item> placed,
        List<Placement> placements,
        PathsD sheetOuterSet)
    {
        var moved = part.Clone();
        TranslatePoly(moved, shiftX, shiftY);
        var movedSet = ClipperBridge.PolygonSet(moved);

        var outside = Clipper.Difference(movedSet, sheetOuterSet, FillRule.NonZero, ClipperBridge.Precision);
        if (Math.Abs(Clipper.Area(outside)) > 1e-6)
        {
            return false;
        }

        for (int i = 0; i < placed.Count; i++)
        {
            var material = placed[i].Poly.Clone();
            TranslatePoly(material, placements[i].X, placements[i].Y);
            var materialSet = ClipperBridge.PolygonSet(material);

            var intersection = Clipper.Intersect(movedSet, materialSet, FillRule.NonZero, ClipperBridge.Precision);
            if (Math.Abs(Clipper.Area(intersection)) > 1e-6)
            {
                return false;
            }
        }

        return true;
    }

    private static Placement CreatePlacement(
        Item part,
        double x,
        double y,
        double mergedLength,
        List<Segment>? mergedSegments,
        List<Point2D>? hull,
        List<Point2D>? hullSheet,
        bool forced = false) => new()
        {
            InstanceId = part.InstanceId,
            SourceId = part.SourceId,
            X = x,
            Y = y,
            Rotation = part.Rotation,
            Mirrored = part.Mirrored,
            MergedLength = mergedLength,
            MergedSegments = mergedSegments ?? new List<Segment>(),
            Hull = hull,
            HullSheet = hullSheet,
            Forced = forced
        };

    private static (List<Point2D> Points, Bounds2D Bounds) CollectPlacedPoints(
        List<Item> placed,
        List<Placement> placements)
    {
        var points = new List<Point2D>();
        for (int i = 0; i < placed.Count; i++)
        {
            var shiftX = placements[i].X;
            var shiftY = placements[i].Y;
            foreach (var p in placed[i].Poly.Points)
            {
                points.Add(new Point2D(p.X + shiftX, p.Y + shiftY, p.Exact));
            }
        }
        return (points, GeometryUtil.GetBounds(points));
    }

    private static void TranslatePoly(Poly poly, double dx, double dy)
    {
        for (int i = 0; i < poly.Points.Count; i++)
        {
            var p = poly.Points[i];
            poly.Points[i] = new Point2D(p.X + dx, p.Y + dy, p.Exact);
        }
        foreach (var child in poly.Children)
        {
            TranslatePoly(child, dx, dy);
        }
    }

    private static void ReportProgress(Action<double>? progress, int placed, int total)
    {
        if (progress is null || total <= 0)
        {
            return;
        }
        progress(0.5 + 0.5 * (placed / (double)total));
    }
}
