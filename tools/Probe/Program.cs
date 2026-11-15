using DeepNest.Core.Geometry;
using DeepNest.Core.Nesting;

static Poly Square(double x, double y, double w, double h)
{
    return new Poly(new[]
    {
        new Point2D(x, y, true),
        new Point2D(x + w, y, true),
        new Point2D(x + w, y + h, true),
        new Point2D(x, y + h, true)
    });
}

static void Dump(string title, List<Poly>? polys)
{
    if (polys is null)
    {
        Console.WriteLine($"{title}: null");
        return;
    }
    Console.WriteLine($"{title}: count={polys.Count}");
    foreach (var p in polys)
    {
        var b = p.Bounds;
        Console.WriteLine($"   pts={p.Points.Count} bounds=[{b.MinX:0.###},{b.MinY:0.###} - {b.MaxX:0.###},{b.MaxY:0.###}]");
    }
}

var engine = new ClipperNfpEngine();

// 1) 旋转 180° 的零件：允许区域应上移到 [10,100]
var sheet = Square(0, 0, 100, 100);
sheet.SourceId = 1;
var part = Square(0, 0, 10, 10);
part.SourceId = 2;
var rotated = GeometryUtil.Rotate(part, 180);
Console.WriteLine($"rotated part bounds = {rotated.Bounds}");
Dump("InnerNfp(sheet, part@180)", engine.InnerNfp(sheet, rotated));

// 2) 旋转 90° 的零件
var rotated90 = GeometryUtil.Rotate(part, 90);
Console.WriteLine($"rotated90 part bounds = {rotated90.Bounds}");
Dump("InnerNfp(sheet, part@90)", engine.InnerNfp(sheet, rotated90));

// 3) 完整摆放：4 件 45x45，全部旋转 90
var config = new NestConfig { Rotations = 4, AllowMirror = false, MaxSheets = 5 };
var sheet2 = Square(0, 0, 100, 100);
sheet2.SourceId = 900;
var template = new SheetTemplate { Shape = sheet2, SourceId = 900 };

var parts = new List<PartInstance>();
for (int i = 0; i < 4; i++)
{
    var s = Square(0, 0, 45, 45);
    s.SourceId = 100;
    parts.Add(new PartInstance { Id = i, SourceId = 100, Shape = s });
}

var placement = new PlacementEngine(engine, config);
var genes = parts.Select(p => new Gene(p.Id, 1, false)).ToArray(); // 旋转索引 1 = 90°
var result = placement.Place(template, parts, genes, CancellationToken.None);
Console.WriteLine($"\nplace: placed={result.PlacedCount} sheets={result.SheetCount} forced={result.ForcedCount}");
foreach (var sp in result.Sheets)
{
    foreach (var p in sp.Placements)
    {
        var shape = Square(0, 0, 45, 45).Points;
        var rotatedShape = GeometryUtil.RotatePoints(shape, p.Rotation);
        var b = GeometryUtil.GetBounds(rotatedShape.Select(pt => new Point2D(pt.X + p.X, pt.Y + p.Y)));
        Console.WriteLine($"  sheet{sp.SheetIndex} id{p.InstanceId} t=({p.X:0.##},{p.Y:0.##}) rot={p.Rotation} 落点bbox=[{b.MinX:0.##},{b.MinY:0.##} - {b.MaxX:0.##},{b.MaxY:0.##}]");
    }
}

// 4) 零件远离原点 + 旋转
var far = Square(500, 500, 45, 45);
far.SourceId = 101;
var farParts = new List<PartInstance> { new() { Id = 0, SourceId = 101, Shape = far } };
var r2 = placement.Place(template, farParts, new[] { new Gene(0, 2, false) }, CancellationToken.None);
var p2 = r2.Sheets[0].Placements[0];
var shape2 = GeometryUtil.RotatePoints(far.Points, p2.Rotation);
var b2 = GeometryUtil.GetBounds(shape2.Select(pt => new Point2D(pt.X + p2.X, pt.Y + p2.Y)));
Console.WriteLine($"\nfar part: t=({p2.X:0.##},{p2.Y:0.##}) rot={p2.Rotation} 落点bbox=[{b2.MinX:0.##},{b2.MinY:0.##} - {b2.MaxX:0.##},{b2.MaxY:0.##}]");
