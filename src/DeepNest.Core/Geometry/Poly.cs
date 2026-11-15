namespace DeepNest.Core.Geometry;

/// <summary>
/// 多边形（外环 + 孔）。对应原 Deepnest 里"数组即多边形 + children 属性"的结构，
/// 但改为强类型。<see cref="Children"/> 为孔，孔的 <see cref="Children"/> 为孔中岛，递归。
/// </summary>
public sealed class Poly
{
    public List<Point2D> Points { get; set; }

    /// <summary>子轮廓（孔 / 孔中岛）。</summary>
    public List<Poly> Children { get; } = new();

    public Poly? Parent { get; set; }

    /// <summary>几何来源标识：几何相同（归一化后）的实例共享同一个 SourceId，用于 NFP 缓存。</summary>
    public int SourceId { get; set; } = -1;

    /// <summary>当前旋转角（度，逆时针）。</summary>
    public double Rotation { get; set; }

    /// <summary>是否关于本地 Y 轴镜像（x → -x）。</summary>
    public bool Mirrored { get; set; }

    /// <summary>辅助标记（例如"开放轮廓弦闭合"），不参与计算。</summary>
    public string? Tag { get; set; }

    public Poly()
    {
        Points = new List<Point2D>();
    }

    public Poly(IEnumerable<Point2D> points)
    {
        Points = new List<Point2D>(points);
    }

    public int Count => Points.Count;
    public Point2D this[int index] => Points[index];

    public Bounds2D Bounds => GeometryUtil.GetBounds(Points);
    public double Area => GeometryUtil.SignedArea(Points);

    public Poly Clone(bool withChildren = true)
    {
        var copy = new Poly(Points)
        {
            SourceId = SourceId,
            Rotation = Rotation,
            Mirrored = Mirrored,
            Tag = Tag
        };
        if (withChildren)
        {
            foreach (var child in Children)
            {
                var cc = child.Clone();
                cc.Parent = copy;
                copy.Children.Add(cc);
            }
        }
        return copy;
    }

    /// <summary>先序遍历所有轮廓（含自身）。</summary>
    public IEnumerable<Poly> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var p in child.SelfAndDescendants())
            {
                yield return p;
            }
        }
    }

    public override string ToString() => $"Poly[{Points.Count} pts, {Children.Count} holes]";
}
