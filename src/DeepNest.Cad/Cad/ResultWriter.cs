using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using DeepNest.Core.Geometry;
using DeepNest.Core.Nesting;

namespace DeepNest.Cad.Cad;

public sealed class ApplyOptions
{
    public int SheetsPerRow { get; set; } = 2;
    public double ColumnGap { get; set; } = 50;
    public double RowGap { get; set; } = 50;
    public string SheetLayer { get; set; } = "DEEPNEST_SHEET";
}

/// <summary>
/// 把排样结果落到当前空间：
/// 只做「克隆块参照 + TransformBy」，不炸开、不新建块定义、不改变块名。
/// 板料框用新建闭合多段线绘制（网格排列，间距可配）。
/// </summary>
public static class ResultWriter
{
    public const string RegAppName = "DEEPNEST";

    public static int Apply(
        Document document,
        CollectedModel model,
        NestResult result,
        IReadOnlyList<PartSourceInfo> orderedParts,
        ApplyOptions options,
        out double maxDeviation)
    {
        var database = document.Database;
        int written = 0;
        maxDeviation = 0;

        try
        {
            System.IO.File.WriteAllText(LogPath, $"=== {DateTime.Now:O} ==={Environment.NewLine}");
        }
        catch
        {
        }

        using var documentLock = document.LockDocument();
        using var transaction = document.TransactionManager.StartTransaction();

        try
        {
            var currentSpace = (BlockTableRecord)transaction.GetObject(
                database.CurrentSpaceId, OpenMode.ForWrite, false);

            EnsureLayer(transaction, database, options.SheetLayer);
            EnsureRegApp(transaction, database);

            var sheetShape = model.SheetShape ?? throw new InvalidOperationException("缺少板料几何。");
            var sheetBounds = sheetShape.Bounds;

            for (int sheetIndex = 0; sheetIndex < result.Sheets.Count; sheetIndex++)
            {
                var sheetPlacement = result.Sheets[sheetIndex];
                var origin = SheetOrigin(sheetIndex, sheetBounds, options);

                // 板料框
                var frame = CreatePolyline(sheetShape, model.SheetOffset, origin, options.SheetLayer);
                frame.XData = BuildXData($"SHEET|{sheetIndex}");
                currentSpace.AppendEntity(frame);
                transaction.AddNewlyCreatedDBObject(frame, true);

                foreach (var placement in sheetPlacement.Placements)
                {
                    if (placement.InstanceId < 0 || placement.InstanceId >= orderedParts.Count)
                    {
                        continue;
                    }

                    var info = orderedParts[placement.InstanceId];
                    if (transaction.GetObject(info.ReferenceId, OpenMode.ForRead, false) is not BlockReference original)
                    {
                        continue;
                    }

                    var clone = (BlockReference)original.Clone();
                    if (clone is null)
                    {
                        continue;
                    }

                    var expectedMin = ExpectedBBoxMin(placement, info, model.SheetOffset, origin);

                    if (written < 20)
                    {
                        LogLine($"part#{written} sheet={sheetIndex} t=({placement.X:0.###},{placement.Y:0.###}) " +
                                $"rot={placement.Rotation:0.###} mir={(placement.Mirrored ? 1 : 0)} " +
                                $"po=({info.Offset.X:0.###},{info.Offset.Y:0.###}) " +
                                $"ins=({original.Position.X:0.###},{original.Position.Y:0.###}) " +
                                $"oro={original.Rotation * 180 / Math.PI:0.###} " +
                                $"oscale=({original.ScaleFactors.X:0.###},{original.ScaleFactors.Y:0.###}) " +
                                $"so=({model.SheetOffset.X:0.###},{model.SheetOffset.Y:0.###}) " +
                                $"origin=({origin.X:0.###},{origin.Y:0.###})");
                    }

                    // 先入库，再改写属性（BlockTransform 依赖数据库）
                    currentSpace.AppendEntity(clone);
                    transaction.AddNewlyCreatedDBObject(clone, true);

                    // 显式设置旋转/镜像/插入点，不依赖 TransformBy 的矩阵分解
                    ApplyPlacement(clone, placement, info, model.SheetOffset, origin);

                    double deviation = MeasureDeviation(clone, expectedMin, written);
                    if (deviation > maxDeviation)
                    {
                        maxDeviation = deviation;
                    }

                    clone.XData = BuildXData(
                        $"PART|{original.Handle}|{sheetIndex}|{placement.InstanceId}|{placement.Rotation:R}|{(placement.Mirrored ? 1 : 0)}|{(placement.Forced ? 1 : 0)}");

                    written++;
                }
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Abort();
            throw;
        }

        return written;
    }

    /// <summary>板料在排版区里的网格原点（各板依次按行排列）。预览与写入共用。</summary>
    public static Point2D SheetOrigin(int sheetIndex, in Bounds2D sheetBounds, ApplyOptions options)
    {
        int perRow = Math.Max(1, options.SheetsPerRow);
        int column = sheetIndex % perRow;
        int row = sheetIndex / perRow;

        double dx = column * (sheetBounds.Width + options.ColumnGap);
        double dy = -row * (sheetBounds.Height + options.RowGap);
        return new Point2D(dx, dy);
    }

    /// <summary>
    /// 【唯一真源】把「归一化形状」（包围盒左下角在原点，即 world − partOffset）映射到最终世界位置：
    ///
    ///     world' = R(θ)·S^m·q + T
    ///
    /// 其中 S = diag(-1,1)（x → -x，见 GeometryUtil.Mirror），T = t + 板料偏移 + 排版原点。
    /// 预览画轮廓用这个矩阵；写块参照（<see cref="ApplyPlacement"/>）和写完后的校核
    /// （<see cref="ExpectedBBoxMin"/>）也都是由它推导出来的，三者不会再跑偏。
    /// </summary>
    public static Matrix3d BuildNormalizedPlacementMatrix(
        Placement placement,
        Point2D sheetOffset,
        Point2D sheetOrigin)
    {
        double radians = placement.Rotation * Math.PI / 180.0;
        Matrix3d mirror = placement.Mirrored
            ? Matrix3d.Mirroring(new Plane(Point3d.Origin, Vector3d.XAxis))
            : Matrix3d.Identity;

        // 注意：AutoCAD 的 Matrix3d 是「标准数学」列向量约定，A * B 表示【先应用 B、再应用 A】。
        // 目标变换是 R(θ)·S·q + T（先镜像、再旋转、最后平移），所以最后一个因子必须是 Displacement。
        return Matrix3d.Displacement(new Vector3d(
                   placement.X + sheetOffset.X + sheetOrigin.X,
                   placement.Y + sheetOffset.Y + sheetOrigin.Y,
                   0))
               * Matrix3d.Rotation(radians, Vector3d.ZAxis, Point3d.Origin)
               * mirror;
    }

    /// <summary>
    /// 零件包围盒左下角的世界坐标。直接复用预览矩阵计算，
    /// 所以它等于「预览里这个零件画在哪里」的最后一块信息，用来校核写入结果。
    /// </summary>
    public static Point2D ExpectedBBoxMin(
        Placement placement,
        PartSourceInfo info,
        Point2D sheetOffset,
        Point2D sheetOrigin)
    {
        var matrix = BuildNormalizedPlacementMatrix(placement, sheetOffset, sheetOrigin);

        double minX = double.MaxValue;
        double minY = double.MaxValue;
        foreach (var point in info.Shape.Points)
        {
            var mapped = new Point3d(point.X, point.Y, 0).TransformBy(matrix);
            if (mapped.X < minX)
            {
                minX = mapped.X;
            }

            if (mapped.Y < minY)
            {
                minY = mapped.Y;
            }
        }

        return new Point2D(minX, minY);
    }

    /// <summary>
    /// 直接改写块参照的 旋转 / 比例 / 插入点。
    /// 不再用 TransformBy 的矩阵分解（它会把镜像揉成负比例或额外 180°，不好验证），
    /// 而是按下面的代数关系显式算出结果。
    ///
    /// 重要：排版内核的镜像约定是 S = diag(-1, 1)，即 <b>x → -x</b>
    /// （见 GeometryUtil.Mirror："关于本地 Y 轴镜像"）。它和 diag(1,-1)（y → -y）
    /// 是两个不同的变换，相差 180°，用错会导致镜像件既错位又错向。
    ///
    ///   目标：world' = R(θ)·S^m·(world − po) + T   （T = t + 板料偏移 + 排版原点）
    ///   原块参照：world = Rot(φ)·diag(sx, sy)·def + ins
    ///   合成（因为 S·Rot(φ) = Rot(−φ)·S）：
    ///     非镜像：A = Rot(θ+φ)·diag(sx, sy)，   Position' = R(θ)·(ins − po) + T
    ///     镜像  ：A = Rot(θ−φ)·diag(−sx, sy)，  Position' = R(θ)·S·(ins − po) + T
    /// </summary>
    private static void ApplyPlacement(
        BlockReference clone,
        Placement placement,
        PartSourceInfo info,
        Point2D sheetOffset,
        Point2D sheetOrigin)
    {
        double theta = placement.Rotation * Math.PI / 180.0;
        double phi = clone.Rotation;
        var scale = clone.ScaleFactors;
        var position = clone.Position;

        double newRotation = placement.Mirrored ? theta - phi : theta + phi;
        double newScaleX = placement.Mirrored ? -scale.X : scale.X;
        double newScaleY = scale.Y;

        double relX = position.X - info.Offset.X;
        double relY = position.Y - info.Offset.Y;
        if (placement.Mirrored)
        {
            relX = -relX; // S = diag(-1, 1)：x → -x
        }

        double cos = Math.Cos(theta);
        double sin = Math.Sin(theta);
        double dx = relX * cos - relY * sin;
        double dy = relX * sin + relY * cos;

        clone.Rotation = newRotation;
        clone.ScaleFactors = new Scale3d(newScaleX, newScaleY, scale.Z);
        clone.Position = new Point3d(
            dx + placement.X + sheetOffset.X + sheetOrigin.X,
            dy + placement.Y + sheetOffset.Y + sheetOrigin.Y,
            position.Z);
    }

    /// <summary>
    /// 写完校核：实测包围盒左下角 vs 预期值。只记录、不修正 ——
    /// 轮廓与实际几何不一致时（块里有多余几何）不应该偷偷移动块。
    /// </summary>
    private static double MeasureDeviation(BlockReference clone, Point2D expectedMin, int index)
    {
        try
        {
            var extents = clone.GeometricExtents;
            double dx = expectedMin.X - extents.MinPoint.X;
            double dy = expectedMin.Y - extents.MinPoint.Y;
            double deviation = Math.Max(Math.Abs(dx), Math.Abs(dy));

            if (index < 20 || deviation > 5.0)
            {
                LogLine($"part#{index} expected=({expectedMin.X:0.###},{expectedMin.Y:0.###}) " +
                        $"actual=({extents.MinPoint.X:0.###},{extents.MinPoint.Y:0.###}) " +
                        $"rot={clone.Rotation * 180 / Math.PI:0.###} " +
                        $"scale=({clone.ScaleFactors.X:0.###},{clone.ScaleFactors.Y:0.###}) " +
                        $"d=({dx:0.###},{dy:0.###})");
            }

            return deviation;
        }
        catch (System.Exception ex)
        {
            LogLine($"part#{index} measure failed: {ex.GetType().Name} {ex.Message}");
            return 0;
        }
    }

    private static string LogPath => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deepnest-apply.log");

    /// <summary>诊断日志（预览等模块也可用）。</summary>
    public static void Log(string line) => LogLine(line);

    private static void LogLine(string line)
    {
        try
        {
            System.IO.File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch
        {
            // 诊断日志失败不影响主流程
        }
    }

    /// <summary>
    /// 删除上一次的排版结果：带 DEEPNEST XData 的副本（PART|/SHEET|）、
    /// 板料框图层、预览图层上的对象。不会动用户自己的原始块。
    /// </summary>
    public static int CleanResults(Document document)
    {
        int removed = 0;
        var database = document.Database;
        string sheetLayer = Settings.SettingsStore.Load().SheetLayer;

        using var documentLock = document.LockDocument();
        using var transaction = document.TransactionManager.StartTransaction();

        var space = (BlockTableRecord)transaction.GetObject(
            database.CurrentSpaceId, OpenMode.ForRead, false);

        var targets = new List<ObjectId>();
        foreach (var id in space)
        {
            if (id.IsErased)
            {
                continue;
            }

            if (transaction.GetObject(id, OpenMode.ForRead, false) is not Entity entity)
            {
                continue;
            }

            bool onOurLayer =
                string.Equals(entity.Layer, sheetLayer, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entity.Layer, PreviewRenderer.LayerName, StringComparison.OrdinalIgnoreCase);

            if (onOurLayer || HasOurXData(entity))
            {
                targets.Add(id);
            }
        }

        foreach (var id in targets)
        {
            if (transaction.GetObject(id, OpenMode.ForWrite, false) is Entity entity)
            {
                entity.Erase();
                removed++;
            }
        }

        transaction.Commit();
        return removed;
    }

    private static bool HasOurXData(Entity entity)
    {
        try
        {
            using var buffer = entity.GetXDataForApplication(RegAppName);
            if (buffer is null)
            {
                return false;
            }

            foreach (TypedValue value in buffer)
            {
                if (value.Value is string text &&
                    (text.StartsWith("PART|", StringComparison.Ordinal) ||
                     text.StartsWith("SHEET|", StringComparison.Ordinal)))
                {
                    return true;
                }
            }
        }
        catch
        {
            // 取不到 XData 就当不是我们的对象
        }

        return false;
    }

    private static Polyline CreatePolyline(Poly shape, Point2D sheetOffset, Point2D origin, string layer)
    {
        var points = shape.Points;
        var polyline = new Polyline(points.Count) { Closed = true, Layer = layer };

        for (int i = 0; i < points.Count; i++)
        {
            polyline.AddVertexAt(
                i,
                new Point2d(points[i].X + sheetOffset.X + origin.X, points[i].Y + sheetOffset.Y + origin.Y),
                0,
                0,
                0);
        }

        return polyline;
    }

    private static ResultBuffer BuildXData(string payload) => new(
        new TypedValue((int)DxfCode.ExtendedDataRegAppName, RegAppName),
        new TypedValue((int)DxfCode.ExtendedDataAsciiString, payload));

    private static void EnsureRegApp(Transaction transaction, Database database)
    {
        var table = (RegAppTable)transaction.GetObject(database.RegAppTableId, OpenMode.ForRead, false);
        if (table.Has(RegAppName))
        {
            return;
        }

        table.UpgradeOpen();
        var record = new RegAppTableRecord { Name = RegAppName };
        table.Add(record);
        transaction.AddNewlyCreatedDBObject(record, true);
    }

    private static void EnsureLayer(Transaction transaction, Database database, string layerName)
    {
        var layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false);
        if (layerTable.Has(layerName))
        {
            return;
        }

        layerTable.UpgradeOpen();
        var layer = new LayerTableRecord { Name = layerName };
        layerTable.Add(layer);
        transaction.AddNewlyCreatedDBObject(layer, true);
    }
}
