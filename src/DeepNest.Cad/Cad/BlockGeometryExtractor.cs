using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using DeepNest.Core.Geometry;

namespace DeepNest.Cad.Cad;

public sealed class ExtractedGeometry
{
    public List<TessellatedPath> Paths { get; } = new();
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// 从块参照提取几何（不炸开、不修改原实体）。
/// 递归读取块定义内的曲线，套用 BlockTransform 变换到 WCS。
/// </summary>
public static class BlockGeometryExtractor
{
    private const int MaxNestingDepth = 6;

    public static ExtractedGeometry Extract(
        Transaction transaction,
        BlockReference reference,
        double tolerance)
    {
        var result = new ExtractedGeometry();
        Collect(
            transaction,
            reference.BlockTableRecord,
            reference.BlockTransform,
            tolerance,
            depth: 0,
            result);
        return result;
    }

    private static void Collect(
        Transaction transaction,
        ObjectId blockDefinitionId,
        Matrix3d transform,
        double tolerance,
        int depth,
        ExtractedGeometry result)
    {
        if (depth > MaxNestingDepth || blockDefinitionId.IsNull || blockDefinitionId.IsErased)
        {
            return;
        }

        if (transaction.GetObject(blockDefinitionId, OpenMode.ForRead, false) is not BlockTableRecord definition)
        {
            return;
        }

        foreach (ObjectId entityId in definition)
        {
            if (entityId.IsErased)
            {
                continue;
            }

            if (transaction.GetObject(entityId, OpenMode.ForRead, false) is not Entity entity)
            {
                continue;
            }

            switch (entity)
            {
                case BlockReference nested:
                    Collect(
                        transaction,
                        nested.BlockTableRecord,
                        // 嵌套块：先套用嵌套参照自身的变换（嵌套定义 → 外层定义），
                        // 再套用外层的累计变换（外层定义 → WCS）。
                        // AutoCAD 的 A * B 表示「先应用 B、再应用 A」，所以嵌套的放右边。
                        transform * nested.BlockTransform,
                        tolerance,
                        depth + 1,
                        result);
                    break;

                case Hatch:
                    result.Warnings.Add("块内含填充(Hatch)，已忽略其边界。");
                    break;

                case Curve curve:
                    {
                        var path = CurveTessellator.Tessellate(curve, transform, tolerance);
                        if (path is not null && path.Points.Count >= 2)
                        {
                            result.Paths.Add(path);
                        }
                    }
                    break;
            }
        }
    }
}
