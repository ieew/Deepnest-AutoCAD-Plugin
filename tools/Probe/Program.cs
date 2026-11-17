using System.Text.Json;
using DeepNest.Core.Geometry;

// 读取 tools/export_part_json.py 导出的零件几何，跑真实的 ContourBuilder，
// 用于离线复现「外轮廓没被识别」这类块轮廓问题。
var file = args.Length > 0
    ? args[0]
    : Path.Combine(Path.GetTempPath(), "deepnest-part.json");

if (!File.Exists(file))
{
    Console.WriteLine($"找不到输入文件：{file}");
    Console.WriteLine("先运行：python tools\\export_part_json.py 0.DXF");
    return;
}

using var doc = JsonDocument.Parse(File.ReadAllText(file));
var root = doc.RootElement;

double curveTolerance = root.GetProperty("curveTolerance").GetDouble();
double joinTolerance = root.GetProperty("baseTolerance").GetDouble();
Console.WriteLine($"块 [{root.GetProperty("block").GetString()}] 曲线容差={curveTolerance} 焊接容差={joinTolerance}");

static Point2D Pt(JsonElement v) => new(v[0].GetDouble(), v[1].GetDouble(), true);

var paths = new List<TessellatedPath>();
foreach (var entity in root.GetProperty("entities").EnumerateArray())
{
    var type = entity.GetProperty("type").GetString();
    if (type == "LINE")
    {
        var path = new TessellatedPath { Closed = false };
        path.Points.Add(Pt(entity.GetProperty("a")));
        path.Points.Add(Pt(entity.GetProperty("b")));
        paths.Add(path);
    }
    else if (type == "LWPOLYLINE")
    {
        bool closed = entity.GetProperty("closed").GetBoolean();
        var verts = new List<(Point2D Point, double Bulge)>();
        foreach (var v in entity.GetProperty("verts").EnumerateArray())
        {
            double bulge = v.GetArrayLength() > 2 ? v[2].GetDouble() : 0.0;
            verts.Add((Pt(v), bulge));
        }

        if (verts.Count < 2)
        {
            continue;
        }

        var path = new TessellatedPath { Closed = closed };
        path.Points.Add(verts[0].Point);
        int segments = closed ? verts.Count : verts.Count - 1;
        for (int i = 0; i < segments; i++)
        {
            var p1 = verts[i].Point;
            var p2 = verts[(i + 1) % verts.Count].Point;
            ArcTessellation.AppendArcSegment(path.Points, p1, p2, verts[i].Bulge, curveTolerance);
        }

        if (closed && path.Points.Count > 1)
        {
            var first = path.Points[0];
            var last = path.Points[^1];
            if (Math.Abs(first.X - last.X) < 1e-9 && Math.Abs(first.Y - last.Y) < 1e-9)
            {
                path.Points.RemoveAt(path.Points.Count - 1);
            }
        }

        paths.Add(path);
    }
}

Console.WriteLine($"离散后 {paths.Count} 条路径：");
for (int i = 0; i < paths.Count; i++)
{
    var path = paths[i];
    var bounds = GeometryUtil.GetBounds(path.Points);
    Console.WriteLine(
        $"  #{i,-2} closed={(path.Closed ? 1 : 0)} n={path.Points.Count,-4} " +
        $"bbox=[{bounds.MinX:0.##},{bounds.MinY:0.##} - {bounds.MaxX:0.##},{bounds.MaxY:0.##}]");
}

var built = ContourBuilder.Build(paths, joinTolerance, curveTolerance);

Console.WriteLine();
Console.WriteLine($"结果: kind={built.Kind} 环数={built.LoopCount} 开口端={built.OpenEndCount} " +
                  $"采用容差={built.RepairTolerance:0.###} 剔除路径={built.DroppedPathCount}");
Console.WriteLine($"保留几何 bbox=[{built.KeptBounds.MinX:0.##},{built.KeptBounds.MinY:0.##} - " +
                  $"{built.KeptBounds.MaxX:0.##},{built.KeptBounds.MaxY:0.##}]");
foreach (var warning in built.Warnings)
{
    Console.WriteLine("  警告: " + warning);
}

Console.WriteLine($"根轮廓 {built.Roots.Count} 个：");
foreach (var poly in built.Roots)
{
    var bounds = poly.Bounds;
    Console.WriteLine(
        $"  area={Math.Abs(GeometryUtil.SignedArea(poly.Points)):0.##} n={poly.Points.Count} " +
        $"bbox=[{bounds.MinX:0.##},{bounds.MinY:0.##} - {bounds.MaxX:0.##},{bounds.MaxY:0.##}] 孔={poly.Children.Count}");
}
