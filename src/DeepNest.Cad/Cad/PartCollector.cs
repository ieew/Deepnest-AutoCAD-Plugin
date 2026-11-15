using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using DeepNest.Cad.Settings;
using DeepNest.Core.Geometry;

namespace DeepNest.Cad.Cad;

public sealed class PartSourceInfo
{
    public ObjectId ReferenceId { get; init; }
    public ObjectId DefinitionId { get; init; }
    public string BlockName { get; init; } = string.Empty;

    /// <summary>归一化形状（包围盒左下角在原点）。</summary>
    public Poly Shape { get; init; } = new();

    /// <summary>归一化时减去的世界包围盒左下角。</summary>
    public Point2D Offset { get; init; }

    public int SourceId { get; set; }
    public ContourKind Kind { get; set; }

    /// <summary>true = 没有找到闭合轮廓，用外包矩形代替（需人工确认）。</summary>
    public bool Approximated { get; set; }
}

public sealed class CollectedModel
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;

    public ObjectId SheetId { get; set; }
    public Poly? SheetShape { get; set; }
    public Point2D SheetOffset { get; set; }
    public string SheetLayer { get; set; } = string.Empty;

    public List<PartSourceInfo> Parts { get; } = new();
    public List<ObjectId> OpenDefinitionIds { get; } = new();
    public List<ObjectId> UnusableDefinitionIds { get; } = new();
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// 把选择集整理成"板料 + 零件实例"。
/// 规则：选择集中恰好 1 条闭合多段线 + 其余为块参照 → 该多段线自动作为板料。
/// </summary>
public static class PartCollector
{
    public static CollectedModel Collect(Document document, IEnumerable<ObjectId> selection, NestSettings settings)
    {
        var model = new CollectedModel();
        var polylines = new List<ObjectId>();
        var blocks = new List<ObjectId>();

        using var transaction = document.TransactionManager.StartTransaction();

        foreach (var id in selection)
        {
            if (id.IsNull || id.IsErased)
            {
                continue;
            }

            var entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
            switch (entity)
            {
                case BlockReference:
                    blocks.Add(id);
                    break;
                case Polyline polyline when polyline.Closed:
                    polylines.Add(id);
                    break;
                case Polyline:
                    polylines.Add(id); // 未闭合也先收下，后面按缺口处理
                    break;
            }
        }

        if (polylines.Count == 0)
        {
            model.Message = "未找到板料：请选择一条闭合多段线作为板料。";
            return model;
        }

        if (blocks.Count == 0)
        {
            model.Message = "未找到零件：请同时选择要排样的块参照。";
            return model;
        }

        if (polylines.Count > 1)
        {
            model.Warnings.Add($"选择了 {polylines.Count} 条多段线，只把第一条当作板料。");
        }

        // 板料
        var sheetId = polylines[0];
        var sheetPolyline = (Polyline)transaction.GetObject(sheetId, OpenMode.ForRead, false);
        model.SheetLayer = sheetPolyline.Layer;

        var sheetPath = CurveTessellator.Tessellate(sheetPolyline, Matrix3d.Identity, settings.CurveTolerance);
        if (sheetPath is null || sheetPath.Points.Count < 3)
        {
            model.Message = "板料多段线无法离散成有效轮廓。";
            return model;
        }

        var sheetLoop = new List<Point2D>(sheetPath.Points);
        if (!sheetPolyline.Closed)
        {
            model.Warnings.Add("板料多段线未闭合，已用弦闭合。");
        }

        var sheetPoly = new Poly(sheetLoop);
        var sheetBounds = sheetPoly.Bounds;
        model.SheetOffset = sheetBounds.Min;
        model.SheetShape = Normalize(sheetPoly);
        model.SheetShape.SourceId = 900000;
        model.SheetId = sheetId;

        // 零件
        var sourceIds = new Dictionary<long, int>();
        int nextSourceId = 1;
        int index = 0;

        foreach (var blockId in blocks)
        {
            index++;
            if (transaction.GetObject(blockId, OpenMode.ForRead, false) is not BlockReference reference)
            {
                continue;
            }

            var geometry = BlockGeometryExtractor.Extract(transaction, reference, settings.CurveTolerance);
            foreach (var warning in geometry.Warnings)
            {
                model.Warnings.Add(warning);
            }

            var contours = ContourBuilder.Build(geometry.Paths, settings.JoinTolerance, settings.CurveTolerance);
            foreach (var warning in contours.Warnings)
            {
                model.Warnings.Add(warning);
            }

            if (contours.Roots.Count == 0)
            {
                // 绝不跳过：没有可用闭合轮廓时，用外包矩形代替参与排样
                model.UnusableDefinitionIds.Add(reference.BlockTableRecord);

                var fallback = BuildFallbackShape(geometry, reference, contours.KeptBounds);
                if (fallback is null)
                {
                    model.Warnings.Add(
                        $"[红色] 块 {SafeName(transaction, reference)} 内没有任何可用几何，已用插入点小方框占位（必须人工处理）。");
                    fallback = BuildPlaceholder(reference);
                }
                else
                {
                    model.Warnings.Add(
                        $"[红色] 块 {SafeName(transaction, reference)} 无闭合轮廓，已改用外包矩形排样（需人工确认）。");
                }

                var fallbackNormalized = Normalize(fallback);
                long fallbackHash = HashWithChildren(fallbackNormalized);
                if (!sourceIds.TryGetValue(fallbackHash, out int fallbackSource))
                {
                    fallbackSource = nextSourceId++;
                    sourceIds[fallbackHash] = fallbackSource;
                }

                model.Parts.Add(new PartSourceInfo
                {
                    ReferenceId = blockId,
                    DefinitionId = reference.BlockTableRecord,
                    BlockName = SafeName(transaction, reference),
                    Shape = fallbackNormalized,
                    Offset = fallback.Bounds.Min,
                    SourceId = fallbackSource,
                    Kind = ContourKind.Unusable,
                    Approximated = true
                });
                continue;
            }

            if (contours.Roots.Count > 1)
            {
                model.Warnings.Add($"[黄色] 块 {SafeName(transaction, reference)} 内有 {contours.Roots.Count} 个独立区域，只取最大一块。");
            }

            var root = contours.Roots
                .OrderByDescending(p => Math.Abs(GeometryUtil.SignedArea(p.Points)))
                .First();

            var normalized = Normalize(root);

            long hash = HashWithChildren(normalized);
            if (!sourceIds.TryGetValue(hash, out int sourceId))
            {
                sourceId = nextSourceId++;
                sourceIds[hash] = sourceId;
            }

            if (contours.Kind == ContourKind.OpenChord)
            {
                model.OpenDefinitionIds.Add(reference.BlockTableRecord);
                model.Warnings.Add($"[黄色] 块 {SafeName(transaction, reference)} 为开放轮廓，已按弦闭合参与排样。");
            }

            model.Parts.Add(new PartSourceInfo
            {
                ReferenceId = blockId,
                DefinitionId = reference.BlockTableRecord,
                BlockName = SafeName(transaction, reference),
                Shape = normalized,
                Offset = root.Bounds.Min,
                SourceId = sourceId,
                Kind = contours.Kind
            });
        }

        transaction.Commit();

        if (model.Parts.Count == 0)
        {
            model.Message = "没有可排样的零件（选中的块都无法识别轮廓）。";
            return model;
        }

        model.Success = true;
        model.Message = $"识别到 {model.Parts.Count} 件零件。";
        return model;
    }

    /// <summary>
    /// 无可用轮廓时的兜底：优先用“已剔除离群几何后的主体范围”，
    /// 否则用所有离散点（或块参照包围盒）的外包矩形。
    /// </summary>
    private static Poly? BuildFallbackShape(
        ExtractedGeometry geometry,
        BlockReference reference,
        in Bounds2D keptBounds)
    {
        if (keptBounds.Width > 1e-9 && keptBounds.Height > 1e-9)
        {
            return Rectangle(keptBounds);
        }

        var points = new List<Point2D>();
        foreach (var path in geometry.Paths)
        {
            points.AddRange(path.Points);
        }

        if (points.Count == 0)
        {
            try
            {
                var extents = reference.GeometricExtents;
                double minX = extents.MinPoint.X;
                double minY = extents.MinPoint.Y;
                double maxX = extents.MaxPoint.X;
                double maxY = extents.MaxPoint.Y;
                if (maxX > minX && maxY > minY)
                {
                    points.Add(new Point2D(minX, minY));
                    points.Add(new Point2D(maxX, maxY));
                }
            }
            catch
            {
                // 取不到包围盒就退化为占位框
            }
        }

        if (points.Count == 0)
        {
            return null;
        }

        var bounds = GeometryUtil.GetBounds(points);
        if (bounds.Width <= 1e-9 || bounds.Height <= 1e-9)
        {
            return null;
        }

        return Rectangle(bounds);
    }

    /// <summary>连包围盒都取不到时的占位框（1×1，位于插入点）。</summary>
    private static Poly BuildPlaceholder(BlockReference reference)
    {
        double x = reference.Position.X;
        double y = reference.Position.Y;
        return Rectangle(new Bounds2D(x, y, x + 1.0, y + 1.0));
    }

    private static Poly Rectangle(in Bounds2D bounds) => new(new[]
    {
        new Point2D(bounds.MinX, bounds.MinY, true),
        new Point2D(bounds.MaxX, bounds.MinY, true),
        new Point2D(bounds.MaxX, bounds.MaxY, true),
        new Point2D(bounds.MinX, bounds.MaxY, true)
    });

    private static string SafeName(Transaction transaction, BlockReference reference)
    {
        try
        {
            var definition = transaction.GetObject(reference.BlockTableRecord, OpenMode.ForRead, false) as BlockTableRecord;
            return definition?.Name ?? "?";
        }
        catch
        {
            return "?";
        }
    }

    private static Poly Normalize(Poly poly)
    {
        var bounds = poly.Bounds;
        return TranslateTree(poly, -bounds.MinX, -bounds.MinY, poly.SourceId);
    }

    private static Poly TranslateTree(Poly poly, double dx, double dy, int sourceId)
    {
        var points = new List<Point2D>(poly.Points.Count);
        foreach (var p in poly.Points)
        {
            points.Add(new Point2D(p.X + dx, p.Y + dy, p.Exact));
        }

        var result = new Poly(points)
        {
            SourceId = sourceId,
            Rotation = poly.Rotation,
            Mirrored = poly.Mirrored
        };

        foreach (var child in poly.Children)
        {
            var translated = TranslateTree(child, dx, dy, child.SourceId);
            translated.Parent = result;
            result.Children.Add(translated);
        }

        return result;
    }

    private static long HashWithChildren(Poly poly)
    {
        unchecked
        {
            long hash = GeometryUtil.ShapeHash(poly.Points);
            foreach (var child in poly.Children)
            {
                hash = (hash ^ HashWithChildren(child)) * 1099511628211L;
            }
            hash = (hash ^ poly.Children.Count) * 1099511628211L;
            return hash;
        }
    }
}
