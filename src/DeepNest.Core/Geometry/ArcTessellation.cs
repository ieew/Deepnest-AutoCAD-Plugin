namespace DeepNest.Core.Geometry;

/// <summary>
/// 圆弧离散（纯数学，不依赖 AutoCAD）。
/// 从 <c>CurveTessellator</c> 抽出来，方便离线复现和单元测试。
/// </summary>
public static class ArcTessellation
{
    public const int MaxSegments = 2048;

    /// <summary>按弦高误差估算圆弧段数。</summary>
    public static int StepsForArc(double radius, double sweep, double tolerance)
    {
        if (radius <= 0)
        {
            return 1;
        }

        double ratio = 1.0 - tolerance / radius;
        ratio = Math.Clamp(ratio, -1.0, 1.0);
        double step = 2.0 * Math.Acos(ratio);
        if (step <= 1e-9 || double.IsNaN(step))
        {
            step = Math.PI / 8;
        }

        int steps = (int)Math.Ceiling(Math.Abs(sweep) / step);
        return Math.Clamp(steps, 1, MaxSegments);
    }

    /// <summary>
    /// 把带 bulge 的圆弧段展开成折线（不包含起点，包含终点）。
    /// bulge = tan(θ/4)，θ 为有符号包含角；|bulge| &gt; 1 表示弧超过 180°。
    /// </summary>
    public static void AppendArcSegment(List<Point2D> output, Point2D p1, Point2D p2, double bulge, double tolerance)
    {
        if (Math.Abs(bulge) < 1e-9)
        {
            output.Add(p2);
            return;
        }

        double dx = p2.X - p1.X;
        double dy = p2.Y - p1.Y;
        double chord = Math.Sqrt(dx * dx + dy * dy);
        if (chord < 1e-12)
        {
            return;
        }

        double theta = 4.0 * Math.Atan(bulge); // 有符号包含角
        double radius = chord / (2.0 * Math.Sin(theta / 2.0));
        if (double.IsNaN(radius) || double.IsInfinity(radius) || Math.Abs(radius) < 1e-12)
        {
            output.Add(p2);
            return;
        }

        double midX = (p1.X + p2.X) * 0.5;
        double midY = (p1.Y + p2.Y) * 0.5;
        double nx = -dy / chord;
        double ny = dx / chord;
        double h = radius * Math.Cos(theta / 2.0);
        double cx = midX + nx * h;
        double cy = midY + ny * h;

        double a0 = Math.Atan2(p1.Y - cy, p1.X - cx);
        int steps = StepsForArc(Math.Abs(radius), theta, tolerance);

        for (int i = 1; i <= steps; i++)
        {
            double angle = a0 + theta * i / steps;
            output.Add(new Point2D(
                cx + Math.Abs(radius) * Math.Cos(angle),
                cy + Math.Abs(radius) * Math.Sin(angle)));
        }
    }
}
