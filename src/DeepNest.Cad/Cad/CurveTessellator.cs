using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using DeepNest.Core.Geometry;

namespace DeepNest.Cad.Cad;

/// <summary>
/// 把 AutoCAD 曲线实体离散成折线。
/// 支持 Line / Arc / Circle / Ellipse / Polyline(bulge) / Polyline2d / Spline / 其它 Curve。
/// </summary>
public static class CurveTessellator
{
    private const int MaxSegments = 2048;

    public static TessellatedPath? Tessellate(Entity entity, Matrix3d transform, double tolerance)
    {
        if (tolerance <= 0)
        {
            tolerance = 0.1;
        }

        switch (entity)
        {
            case Line line:
                {
                    var path = new TessellatedPath { Closed = false };
                    path.Points.Add(ToPoint(line.StartPoint, transform));
                    path.Points.Add(ToPoint(line.EndPoint, transform));
                    return path;
                }

            case Arc arc:
                return TessellateArc(arc, transform, tolerance);

            case Circle circle:
                return TessellateCircle(circle, transform, tolerance);

            case Ellipse ellipse:
                return TessellateEllipse(ellipse, transform, tolerance);

            case Polyline polyline:
                return TessellatePolyline(polyline, transform, tolerance);

            case Polyline2d polyline2d:
                return TessellatePolyline2d(polyline2d, transform, tolerance);

            case Spline spline:
                return TessellateCurve(spline, transform, tolerance, spline.Closed);

            case Curve curve:
                return TessellateCurve(curve, transform, tolerance, curve.Closed);

            default:
                return null;
        }
    }

    private static Point2D ToPoint(Point3d point, Matrix3d transform)
    {
        var p = point.TransformBy(transform);
        return new Point2D(p.X, p.Y, true);
    }

    private static TessellatedPath TessellateArc(Arc arc, Matrix3d transform, double tolerance)
    {
        var path = new TessellatedPath { Closed = false };
        var center = arc.Center;
        var radius = arc.Radius;
        double start = arc.StartAngle;
        double end = arc.EndAngle;

        // 圆弧方向：AutoCAD 圆弧总是逆时针从 StartAngle 到 EndAngle
        double sweep = end - start;
        while (sweep <= 0)
        {
            sweep += Math.PI * 2;
        }

        int steps = ArcTessellation.StepsForArc(radius, sweep, tolerance);
        for (int i = 0; i <= steps; i++)
        {
            double angle = start + sweep * i / steps;
            var p = new Point3d(
                center.X + radius * Math.Cos(angle),
                center.Y + radius * Math.Sin(angle),
                center.Z);
            path.Points.Add(ToPoint(p, transform));
        }

        return path;
    }

    private static TessellatedPath TessellateCircle(Circle circle, Matrix3d transform, double tolerance)
    {
        var path = new TessellatedPath { Closed = true };
        var center = circle.Center;
        double radius = circle.Radius;

        int steps = ArcTessellation.StepsForArc(radius, Math.PI * 2, tolerance);
        for (int i = 0; i < steps; i++)
        {
            double angle = Math.PI * 2 * i / steps;
            var p = new Point3d(
                center.X + radius * Math.Cos(angle),
                center.Y + radius * Math.Sin(angle),
                center.Z);
            path.Points.Add(ToPoint(p, transform));
        }

        return path;
    }

    private static TessellatedPath TessellateEllipse(Ellipse ellipse, Matrix3d transform, double tolerance)
    {
        var center = ellipse.Center;
        var major = ellipse.MajorAxis.GetNormal();
        var minor = ellipse.Normal.CrossProduct(major).GetNormal();
        double rx = ellipse.MajorRadius;
        double ry = ellipse.MinorRadius;

        double start = ellipse.StartAngle;
        double end = ellipse.EndAngle;
        double sweep = end - start;
        bool closed = Math.Abs(Math.Abs(sweep) - Math.PI * 2) < 1e-9;
        while (sweep <= 0)
        {
            sweep += Math.PI * 2;
        }

        double radius = Math.Max(rx, ry);
        int steps = ArcTessellation.StepsForArc(radius, sweep, tolerance);

        var path = new TessellatedPath { Closed = closed };
        int count = closed ? steps : steps + 1;
        for (int i = 0; i < count; i++)
        {
            double t = start + sweep * i / steps;
            var p = center
                    + major * (rx * Math.Cos(t))
                    + minor * (ry * Math.Sin(t));
            path.Points.Add(ToPoint(p, transform));
        }

        return path;
    }

    private static TessellatedPath TessellatePolyline(Polyline polyline, Matrix3d transform, double tolerance)
    {
        var path = new TessellatedPath { Closed = polyline.Closed };
        int count = polyline.NumberOfVertices;
        if (count < 2)
        {
            return path;
        }

        int segments = polyline.Closed ? count : count - 1;
        path.Points.Add(ToPoint(polyline.GetPoint3dAt(0), transform));

        for (int i = 0; i < segments; i++)
        {
            var p1 = ToPoint(polyline.GetPoint3dAt(i), transform);
            var p2 = ToPoint(polyline.GetPoint3dAt((i + 1) % count), transform);
            double bulge = polyline.GetBulgeAt(i);
            ArcTessellation.AppendArcSegment(path.Points, p1, p2, bulge, tolerance);
        }

        if (polyline.Closed && path.Points.Count > 1)
        {
            // 闭合时去掉与首点重合的尾点（内部约定不重复存首点）
            var first = path.Points[0];
            var last = path.Points[^1];
            if (Math.Abs(first.X - last.X) < 1e-9 && Math.Abs(first.Y - last.Y) < 1e-9)
            {
                path.Points.RemoveAt(path.Points.Count - 1);
            }
        }

        return path;
    }

    private static TessellatedPath? TessellatePolyline2d(Polyline2d polyline, Matrix3d transform, double tolerance)
    {
        var path = new TessellatedPath { Closed = polyline.Closed };
        var positions = new List<(Point2D Point, double Bulge)>();

        foreach (ObjectId vertexId in polyline)
        {
            if (vertexId.GetObject(OpenMode.ForRead, false) is not Vertex2d vertex)
            {
                continue;
            }
            positions.Add((ToPoint(vertex.Position, transform), vertex.Bulge));
        }

        if (positions.Count < 2)
        {
            return null;
        }

        path.Points.Add(positions[0].Point);
        for (int i = 0; i < positions.Count - (polyline.Closed ? 0 : 1); i++)
        {
            var p1 = positions[i].Point;
            var p2 = positions[(i + 1) % positions.Count].Point;
            var bulge = positions[i].Bulge;
            ArcTessellation.AppendArcSegment(path.Points, p1, p2, bulge, tolerance);
        }

        if (polyline.Closed && path.Points.Count > 1)
        {
            var first = path.Points[0];
            var last = path.Points[^1];
            if (Math.Abs(first.X - last.X) < 1e-9 && Math.Abs(first.Y - last.Y) < 1e-9)
            {
                path.Points.RemoveAt(path.Points.Count - 1);
            }
        }

        return path;
    }

    private static TessellatedPath? TessellateCurve(Curve curve, Matrix3d transform, double tolerance, bool closed)
    {
        try
        {
            double start = curve.StartParam;
            double end = curve.EndParam;
            if (Math.Abs(end - start) < 1e-12)
            {
                return null;
            }

            double length = curve.GetDistanceAtParameter(end);
            int steps = Math.Clamp((int)Math.Ceiling(length / Math.Max(tolerance, 1e-6)), 8, MaxSegments);

            var path = new TessellatedPath { Closed = closed };
            int count = closed ? steps : steps + 1;
            for (int i = 0; i < count; i++)
            {
                double param = start + (end - start) * i / steps;
                var p = curve.GetPointAtParameter(param);
                path.Points.Add(ToPoint(p, transform));
            }

            return path;
        }
        catch
        {
            return null;
        }
    }
}
