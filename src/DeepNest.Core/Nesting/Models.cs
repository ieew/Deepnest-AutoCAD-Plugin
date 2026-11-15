using DeepNest.Core.Geometry;

namespace DeepNest.Core.Nesting;

/// <summary>一个待排零件实例（一个选中的块参照 = 一件）。</summary>
public sealed class PartInstance
{
    /// <summary>实例唯一编号（0..N-1），GA 用它保证基因是排列。</summary>
    public int Id { get; init; }

    /// <summary>几何来源编号：形状完全相同的实例共享同一 SourceId（只用于 NFP 缓存）。</summary>
    public int SourceId { get; set; }

    /// <summary>归一化形状：包围盒左下角已移到 (0,0)，包含孔。引擎只读该对象。</summary>
    public Poly Shape { get; set; } = new();

    /// <summary>归一化时减去的原始包围盒左下角（世界坐标）。</summary>
    public Point2D Offset { get; set; }

    /// <summary>CAD 侧携带的附加信息（原始 ObjectId、块名等）。</summary>
    public object? Tag { get; set; }

    /// <summary>是否允许镜像（默认跟随全局配置）。</summary>
    public bool AllowMirror { get; set; } = true;
}

/// <summary>板料模板。所有板料实例共享同一形状，按需生成。</summary>
public sealed class SheetTemplate
{
    public int SourceId { get; set; }

    /// <summary>归一化形状（包围盒左下角在 (0,0)）。</summary>
    public Poly Shape { get; set; } = new();

    /// <summary>归一化时减去的原始包围盒左下角（世界坐标）。</summary>
    public Point2D Offset { get; set; }

    public object? Tag { get; set; }

    /// <summary>是否允许零件排进板料的内孔。</summary>
    public bool AllowHoles { get; set; } = true;
}

/// <summary>基因：某实例的旋转索引与是否镜像。</summary>
public readonly record struct Gene(int InstanceId, int RotationIndex, bool Mirrored);

/// <summary>GA 个体：基因顺序即放置顺序。</summary>
public sealed class Individual
{
    public Individual(Gene[] genes) => Genes = genes;

    public Gene[] Genes { get; set; }
    public double Fitness { get; set; } = double.PositiveInfinity;
    public bool Processing { get; set; }
    public int EvaluatedGeneration { get; set; } = -1;
}

public readonly record struct Segment(Point2D A, Point2D B);

/// <summary>一次放置：t 为归一化坐标系下的平移量（即零件参考点落点）。</summary>
public sealed class Placement
{
    public int InstanceId { get; init; }
    public int SourceId { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double Rotation { get; init; }
    public bool Mirrored { get; init; }
    public double MergedLength { get; init; }
    public List<Segment> MergedSegments { get; init; } = new();
    public List<Point2D>? Hull { get; init; }
    public List<Point2D>? HullSheet { get; init; }

    /// <summary>true = 因无法正常放置而强制放到一张新板的原点（需人工调整，但绝不丢件）。</summary>
    public bool Forced { get; init; }
}

public sealed class SheetPlacement
{
    public int SheetIndex { get; init; }
    public List<Placement> Placements { get; } = new();
}

public sealed class NestResult
{
    public List<SheetPlacement> Sheets { get; } = new();
    public double Fitness { get; set; }
    public double TotalSheetArea { get; set; }
    public double MergedLength { get; set; }
    public int PlacedCount { get; set; }
    public int UnplacedCount { get; set; }
    public int Generation { get; set; }

    /// <summary>被强制放置（需人工调整）的零件数。</summary>
    public int ForcedCount { get; set; }

    public int SheetCount => Sheets.Count;
}
