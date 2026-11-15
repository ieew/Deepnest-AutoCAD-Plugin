using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using DeepNest.Core.Geometry;
using DeepNest.Core.Nesting;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Polyline = Autodesk.AutoCAD.DatabaseServices.Polyline;

namespace DeepNest.Cad.Cad;

/// <summary>
/// 方案预览：在专用图层 DEEPNEST_PREVIEW 上绘制板料框与零件轮廓。
/// 每次预览先清空该图层再重建，保证切换方案一定能刷新。
/// </summary>
public static class PreviewRenderer
{
    public const string LayerName = "DEEPNEST_PREVIEW";

    public static void Clear()
    {
        var document = AcadApp.DocumentManager.MdiActiveDocument;
        if (document is null)
        {
            return;
        }

        try
        {
            using var documentLock = document.LockDocument();
            using var transaction = document.TransactionManager.StartTransaction();
            DeletePreviewEntities(document.Database, transaction);
            transaction.Commit();
            document.Editor.Regen();
        }
        catch
        {
            // 预览清理失败不打断流程
        }
    }

    public static void Show(
        CollectedModel model,
        NestResult result,
        ApplyOptions options,
        IReadOnlyList<PartSourceInfo> parts)
    {
        var document = AcadApp.DocumentManager.MdiActiveDocument;
        if (document is null || model.SheetShape is null)
        {
            return;
        }

        var database = document.Database;
        var sheetBounds = model.SheetShape.Bounds;

        using var documentLock = document.LockDocument();
        using var transaction = document.TransactionManager.StartTransaction();

        var space = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite, false);
        EnsureLayer(transaction, database);

        DeletePreviewEntities(database, transaction);

        double partMinX = double.MaxValue, partMinY = double.MaxValue;
        double partMaxX = double.MinValue, partMaxY = double.MinValue;
        double sheetMinX = double.MaxValue, sheetMinY = double.MaxValue;
        double sheetMaxX = double.MinValue, sheetMaxY = double.MinValue;
        int drawn = 0;

        for (int sheetIndex = 0; sheetIndex < result.Sheets.Count; sheetIndex++)
        {
            var origin = ResultWriter.SheetOrigin(sheetIndex, sheetBounds, options);

            var sheetMatrix = Matrix3d.Displacement(new Vector3d(
                model.SheetOffset.X + origin.X,
                model.SheetOffset.Y + origin.Y,
                0));

            if (Append(transaction, space, model.SheetShape, sheetMatrix, 5) is { } sheetBox)
            {
                sheetMinX = Math.Min(sheetMinX, sheetBox.MinX);
                sheetMinY = Math.Min(sheetMinY, sheetBox.MinY);
                sheetMaxX = Math.Max(sheetMaxX, sheetBox.MaxX);
                sheetMaxY = Math.Max(sheetMaxY, sheetBox.MaxY);
            }

            foreach (var placement in result.Sheets[sheetIndex].Placements)
            {
                if (placement.InstanceId < 0 || placement.InstanceId >= parts.Count)
                {
                    continue;
                }

                var info = parts[placement.InstanceId];
                // 预览画的是归一化轮廓（包围盒左下角在原点），
                // 所以用归一化矩阵，不能再减 partOffset。
                var matrix = ResultWriter.BuildNormalizedPlacementMatrix(placement, model.SheetOffset, origin);
                if (Append(transaction, space, info.Shape, matrix, 3) is { } box)
                {
                    partMinX = Math.Min(partMinX, box.MinX);
                    partMinY = Math.Min(partMinY, box.MinY);
                    partMaxX = Math.Max(partMaxX, box.MaxX);
                    partMaxY = Math.Max(partMaxY, box.MaxY);
                    drawn++;
                }

                foreach (var hole in info.Shape.Children)
                {
                    Append(transaction, space, hole, matrix, 3);
                }
            }
        }

        transaction.Commit();
        document.Editor.Regen();

        if (drawn > 0)
        {
            bool intersects = partMinX <= sheetMaxX && partMaxX >= sheetMinX &&
                              partMinY <= sheetMaxY && partMaxY >= sheetMinY;
            ResultWriter.Log(
                $"PREVIEW sheets={result.Sheets.Count} parts={drawn} " +
                $"partRange=[{partMinX:0.###},{partMinY:0.###} - {partMaxX:0.###},{partMaxY:0.###}] " +
                $"sheetRange=[{sheetMinX:0.###},{sheetMinY:0.###} - {sheetMaxX:0.###},{sheetMaxY:0.###}] " +
                $"intersect={intersects}");
            if (!intersects)
            {
                AcadApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(
                    "\n[DeepNest] 预览范围与板料框不重叠，可能是排版数据异常，详见 %TEMP%\\deepnest-apply.log");
            }
        }
    }

    private static Bounds2D? Append(
        Transaction transaction,
        BlockTableRecord space,
        Poly shape,
        Matrix3d matrix,
        short colorIndex)
    {
        var points = shape.Points;
        if (points.Count < 3)
        {
            return null;
        }

        var polyline = new Polyline(points.Count)
        {
            Closed = true,
            Layer = LayerName,
            Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex)
        };

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        for (int i = 0; i < points.Count; i++)
        {
            var p = new Point3d(points[i].X, points[i].Y, 0).TransformBy(matrix);
            polyline.AddVertexAt(i, new Point2d(p.X, p.Y), 0, 0, 0);
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }

        space.AppendEntity(polyline);
        transaction.AddNewlyCreatedDBObject(polyline, true);

        return new Bounds2D(minX, minY, maxX, maxY);
    }

    private static void DeletePreviewEntities(Database database, Transaction transaction)
    {
        var space = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForRead, false);
        var targets = new List<ObjectId>();

        foreach (var id in space)
        {
            if (id.IsErased)
            {
                continue;
            }

            if (transaction.GetObject(id, OpenMode.ForRead, false) is Entity entity &&
                string.Equals(entity.Layer, LayerName, StringComparison.OrdinalIgnoreCase))
            {
                targets.Add(id);
            }
        }

        foreach (var id in targets)
        {
            if (transaction.GetObject(id, OpenMode.ForWrite, false) is Entity entity)
            {
                entity.Erase();
            }
        }
    }

    private static void EnsureLayer(Transaction transaction, Database database)
    {
        var table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false);
        if (table.Has(LayerName))
        {
            return;
        }

        table.UpgradeOpen();
        var layer = new LayerTableRecord { Name = LayerName };
        table.Add(layer);
        transaction.AddNewlyCreatedDBObject(layer, true);
    }
}
