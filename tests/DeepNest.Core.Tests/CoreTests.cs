using DeepNest.Core.Geometry;
using DeepNest.Core.Nesting;

namespace DeepNest.Core.Tests;

public class GeometryTests
{
    internal static Poly Square(double x, double y, double w, double h, bool exact = true) =>
        new(new[]
        {
            new Point2D(x, y, exact),
            new Point2D(x + w, y, exact),
            new Point2D(x + w, y + h, exact),
            new Point2D(x, y + h, exact)
        });

    [Fact]
    public void SignArea_IsPositiveForCounterClockwise()
    {
        var square = Square(0, 0, 10, 10);
        Assert.Equal(100, GeometryUtil.SignedArea(square.Points), 9);
    }

    [Fact]
    public void Bounds_AreCorrect()
    {
        var b = Square(2, 3, 10, 5).Bounds;
        Assert.Equal(2, b.MinX, 9);
        Assert.Equal(3, b.MinY, 9);
        Assert.Equal(12, b.MaxX, 9);
        Assert.Equal(8, b.MaxY, 9);
        Assert.Equal(10, b.Width, 9);
        Assert.Equal(5, b.Height, 9);
    }

    [Fact]
    public void Rotate_NinetyDegrees_MapsXAxisToYAxis()
    {
        var points = new List<Point2D> { new(1, 0) };
        var rotated = GeometryUtil.RotatePoints(points, 90);
        Assert.Equal(0, rotated[0].X, 9);
        Assert.Equal(1, rotated[0].Y, 9);
    }

    [Fact]
    public void ConvexHull_DropsInteriorPoint()
    {
        var points = Square(0, 0, 10, 10).Points.ToList();
        points.Add(new Point2D(5, 5));
        var hull = GeometryUtil.ConvexHull(points);
        Assert.Equal(4, hull.Count);
    }

    [Theory]
    [InlineData(5, 5, 1)]
    [InlineData(0, 5, 0)]
    [InlineData(-1, 5, -1)]
    [InlineData(11, 5, -1)]
    public void PointInPolygon_Classifies(double x, double y, int expected)
    {
        var square = Square(0, 0, 10, 10);
        Assert.Equal(expected, GeometryUtil.PointInPolygon(new Point2D(x, y), square.Points));
    }
}

public class NfpTests
{
    [Fact]
    public void OuterNfp_TwoSquares_IsExpectedRectangle()
    {
        var engine = new ClipperNfpEngine();
        var a = GeometryTests.Square(0, 0, 10, 10);
        a.SourceId = 1;
        var b = GeometryTests.Square(0, 0, 2, 2);
        b.SourceId = 2;

        var nfp = engine.OuterNfp(a, b);

        Assert.NotNull(nfp);
        var bounds = nfp!.Bounds;
        Assert.Equal(-2, bounds.MinX, 6);
        Assert.Equal(-2, bounds.MinY, 6);
        Assert.Equal(10, bounds.MaxX, 6);
        Assert.Equal(10, bounds.MaxY, 6);
    }

    [Fact]
    public void InnerNfp_RectangleInsideRectangle_ShrinksByPartSize()
    {
        var engine = new ClipperNfpEngine();
        var sheet = GeometryTests.Square(0, 0, 100, 100);
        sheet.SourceId = 10;
        var part = GeometryTests.Square(0, 0, 10, 10);
        part.SourceId = 11;

        var allowed = engine.InnerNfp(sheet, part);

        Assert.NotNull(allowed);
        Assert.Single(allowed!);
        var bounds = allowed![0].Bounds;
        Assert.Equal(0, bounds.MinX, 6);
        Assert.Equal(0, bounds.MinY, 6);
        Assert.Equal(90, bounds.MaxX, 6);
        Assert.Equal(90, bounds.MaxY, 6);
    }

    [Fact]
    public void OuterNfp_WithHole_CreatesAllowedIsland()
    {
        var engine = new ClipperNfpEngine();
        var outer = GeometryTests.Square(0, 0, 40, 40);
        outer.SourceId = 20;
        var hole = GeometryTests.Square(10, 10, 20, 20);
        hole.SourceId = 21;
        outer.Children.Add(hole);

        var part = GeometryTests.Square(0, 0, 5, 5);
        part.SourceId = 22;

        var nfp = engine.OuterNfp(outer, part);

        Assert.NotNull(nfp);
        Assert.NotEmpty(nfp!.Children);

        // 孔内可放区域应为 [10,25] × [10,25]
        var island = nfp.Children[0].Bounds;
        Assert.Equal(10, island.MinX, 6);
        Assert.Equal(10, island.MinY, 6);
        Assert.Equal(25, island.MaxX, 6);
        Assert.Equal(25, island.MaxY, 6);
    }

    [Fact]
    public void OuterNfp_UsesTranslationSpace_WhenPartIsNotAtOrigin()
    {
        var engine = new ClipperNfpEngine();
        var a = GeometryTests.Square(0, 0, 10, 10);
        a.SourceId = 31;
        var b = GeometryTests.Square(100, 100, 2, 2); // 零件不在原点
        b.SourceId = 32;

        var nfp = engine.OuterNfp(a, b);

        Assert.NotNull(nfp);
        var bounds = nfp!.Bounds;
        // t + B 与 A 相交 → t ∈ (-102, -90)
        Assert.Equal(-102, bounds.MinX, 6);
        Assert.Equal(-90, bounds.MaxX, 6);
        Assert.Equal(-102, bounds.MinY, 6);
        Assert.Equal(-90, bounds.MaxY, 6);
    }

    [Fact]
    public void InnerNfp_UsesTranslationSpace_WhenPartIsNotAtOrigin()
    {
        var engine = new ClipperNfpEngine();
        var sheet = GeometryTests.Square(0, 0, 100, 100);
        sheet.SourceId = 41;
        var part = GeometryTests.Square(50, 50, 10, 10);
        part.SourceId = 42;

        var allowed = engine.InnerNfp(sheet, part);

        Assert.NotNull(allowed);
        Assert.Single(allowed!);
        var bounds = allowed![0].Bounds;
        Assert.Equal(-50, bounds.MinX, 6);
        Assert.Equal(40, bounds.MaxX, 6);
        Assert.Equal(-50, bounds.MinY, 6);
        Assert.Equal(40, bounds.MaxY, 6);
    }
}

public class GeneticAlgorithmTests
{
    [Fact]
    public void Mate_ProducesCompletePermutations()
    {
        var config = new NestConfig { PopulationSize = 4, Rotations = 4, AllowMirror = true };
        var ga = new GeneticAlgorithm(Enumerable.Range(0, 12).ToArray(), config, new Random(7));

        var (first, second) = ga.Mate(ga.Population[0], ga.Population[1]);

        Assert.Equal(12, first.Genes.Length);
        Assert.Equal(12, second.Genes.Length);
        Assert.Equal(12, first.Genes.Select(g => g.InstanceId).Distinct().Count());
        Assert.Equal(12, second.Genes.Select(g => g.InstanceId).Distinct().Count());
    }

    [Fact]
    public void Generation_KeepsEliteAndPopulationSize()
    {
        var config = new NestConfig { PopulationSize = 6, Rotations = 4 };
        var ga = new GeneticAlgorithm(Enumerable.Range(0, 8).ToArray(), config, new Random(11));

        for (int i = 0; i < ga.Population.Length; i++)
        {
            ga.Population[i].Fitness = 100 - i;
        }

        var eliteGenes = ga.Population[5].Genes; // fitness 最小
        ga.Generation();

        Assert.Equal(6, ga.Population.Length);
        Assert.Contains(ga.Population, ind => ReferenceEquals(ind.Genes, eliteGenes));
    }
}

public class PlacementTests
{
    private static (NestConfig Config, SheetTemplate Sheet, List<PartInstance> Parts) Setup(
        int partCount, double partSize, double sheetSize)
    {
        var config = new NestConfig
        {
            Rotations = 1,
            AllowMirror = false,
            MergeLines = false,
            MaxSheets = 50,
            PopulationSize = 3
        };

        var sheetPoly = GeometryTests.Square(0, 0, sheetSize, sheetSize);
        sheetPoly.SourceId = 900;
        var sheet = new SheetTemplate { Shape = sheetPoly, SourceId = 900 };

        var parts = new List<PartInstance>();
        for (int i = 0; i < partCount; i++)
        {
            var shape = GeometryTests.Square(0, 0, partSize, partSize);
            shape.SourceId = 100;
            parts.Add(new PartInstance { Id = i, SourceId = 100, Shape = shape });
        }

        return (config, sheet, parts);
    }

    [Fact]
    public void FourSquares_FitOnSingleSheet()
    {
        var (config, sheet, parts) = Setup(4, 45, 100);
        var engine = new PlacementEngine(new ClipperNfpEngine(), config);
        var genes = parts.Select(p => new Gene(p.Id, 0, false)).ToArray();

        var result = engine.Place(sheet, parts, genes, CancellationToken.None);

        Assert.Equal(4, result.PlacedCount);
        Assert.Equal(1, result.SheetCount);
        Assert.Equal(0, result.UnplacedCount);
    }

    [Fact]
    public void TwoBigSquares_OpenSecondSheet()
    {
        var (config, sheet, parts) = Setup(2, 55, 100);
        var engine = new PlacementEngine(new ClipperNfpEngine(), config);
        var genes = parts.Select(p => new Gene(p.Id, 0, false)).ToArray();

        var result = engine.Place(sheet, parts, genes, CancellationToken.None);

        Assert.Equal(2, result.PlacedCount);
        Assert.Equal(2, result.SheetCount);
    }

    [Fact]
    public void MergedLength_DetectsSharedEdge()
    {
        var left = GeometryTests.Square(0, 0, 10, 10);
        var right = GeometryTests.Square(10, 0, 10, 10);

        var (length, segments) = MergedLengthCalculator.Compute(
            new List<Poly> { left }, right, 0.5, 0.01);

        Assert.Equal(10, length, 6);
        Assert.NotEmpty(segments);
    }

    [Fact]
    public void SmallPart_IsPlacedInsideAnotherPartsHole()
    {
        // 板料 100x100；大件 40x40 带 20x20 孔（孔在 10..30）；小件 10x10
        var config = new NestConfig { Rotations = 1, AllowMirror = false, MaxSheets = 10 };

        var sheetPoly = GeometryTests.Square(0, 0, 100, 100);
        sheetPoly.SourceId = 900;
        var sheet = new SheetTemplate { Shape = sheetPoly, SourceId = 900 };

        var big = GeometryTests.Square(0, 0, 40, 40);
        big.SourceId = 100;
        var hole = GeometryTests.Square(10, 10, 20, 20);
        hole.SourceId = 101;
        big.Children.Add(hole);

        var small = GeometryTests.Square(0, 0, 10, 10);
        small.SourceId = 102;

        var instances = new List<PartInstance>
        {
            new() { Id = 0, SourceId = 100, Shape = big },
            new() { Id = 1, SourceId = 102, Shape = small }
        };

        var engine = new PlacementEngine(new ClipperNfpEngine(), config);
        var genes = instances.Select(p => new Gene(p.Id, 0, false)).ToArray();

        var result = engine.Place(sheet, instances, genes, CancellationToken.None);

        Assert.Equal(2, result.PlacedCount);
        Assert.Equal(1, result.SheetCount);

        var smallPlacement = result.Sheets[0].Placements.Single(p => p.InstanceId == 1);
        Assert.InRange(smallPlacement.X, 10 - 1e-6, 20 + 1e-6);
        Assert.InRange(smallPlacement.Y, 10 - 1e-6, 20 + 1e-6);
    }

    [Fact]
    public void PartLargerThanSheet_IsForcedPlaced_NotDropped()
    {
        // 硬性约束：所有零件都必须落位，绝不跳过
        var config = new NestConfig { Rotations = 1, AllowMirror = false, MaxSheets = 3 };

        var sheetPoly = GeometryTests.Square(0, 0, 100, 100);
        sheetPoly.SourceId = 900;
        var sheet = new SheetTemplate { Shape = sheetPoly, SourceId = 900 };

        var shape = GeometryTests.Square(0, 0, 150, 150); // 比板料还大
        shape.SourceId = 100;

        var instances = new List<PartInstance> { new() { Id = 0, SourceId = 100, Shape = shape } };
        var engine = new PlacementEngine(new ClipperNfpEngine(), config);
        var result = engine.Place(sheet, instances, new[] { new Gene(0, 0, false) }, CancellationToken.None);

        Assert.Equal(1, result.PlacedCount);
        Assert.Equal(0, result.UnplacedCount);
        Assert.Equal(1, result.ForcedCount);
        Assert.Equal(1, result.SheetCount);

        var placement = result.Sheets[0].Placements[0];
        Assert.True(placement.Forced);
        Assert.Equal(0, placement.X, 6);
        Assert.Equal(0, placement.Y, 6);
    }

    [Fact]
    public void Placement_KeepsPartOnSheet_WhenPartIsNotAtOrigin()
    {
        var config = new NestConfig { Rotations = 1, AllowMirror = false, MaxSheets = 10 };

        var sheetPoly = GeometryTests.Square(0, 0, 100, 100);
        sheetPoly.SourceId = 900;
        var sheet = new SheetTemplate { Shape = sheetPoly, SourceId = 900 };

        // 零件坐标远离原点（模拟块内图形不在 0,0 的情况）
        var shape = GeometryTests.Square(500, 500, 45, 45);
        shape.SourceId = 100;

        var instances = new List<PartInstance> { new() { Id = 0, SourceId = 100, Shape = shape } };
        var engine = new PlacementEngine(new ClipperNfpEngine(), config);
        var result = engine.Place(sheet, instances, new[] { new Gene(0, 0, false) }, CancellationToken.None);

        Assert.Equal(1, result.PlacedCount);
        var placement = result.Sheets[0].Placements[0];

        // 平移后第一个顶点必须落在板料范围内
        double x = shape.Points[0].X + placement.X;
        double y = shape.Points[0].Y + placement.Y;
        Assert.InRange(x, -1e-6, 100 + 1e-6);
        Assert.InRange(y, -1e-6, 100 + 1e-6);
    }

    [Fact]
    public void Spacing_IsEnforced_BetweenAdjacentParts()
    {
        // 两件 20x20 放到 100x100 板上：无间隙时贴边（中心距 20）；
        // 设置为 6 后中心距必须 >= 26。
        (double dx, double dy) Place(double spacing)
        {
            var config = new NestConfig
            {
                Spacing = spacing,
                Rotations = 1,
                AllowMirror = false,
                MaxSheets = 5
            };

            var sheetPoly = GeometryTests.Square(0, 0, 100, 100);
            sheetPoly.SourceId = 900;
            var sheet = new SheetTemplate { Shape = sheetPoly, SourceId = 900 };

            var parts = new List<PartInstance>();
            for (int i = 0; i < 2; i++)
            {
                var shape = GeometryTests.Square(0, 0, 20, 20);
                shape.SourceId = 100;
                parts.Add(new PartInstance { Id = i, SourceId = 100, Shape = shape });
            }

            var nfp = new ClipperNfpEngine { Spacing = spacing };
            var engine = new PlacementEngine(nfp, config);
            var genes = parts.Select(p => new Gene(p.Id, 0, false)).ToArray();
            var result = engine.Place(sheet, parts, genes, CancellationToken.None);

            Assert.Equal(1, result.SheetCount);
            var a = result.Sheets[0].Placements[0];
            var b = result.Sheets[0].Placements[1];
            return (Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }

        var tight = Place(0);
        Assert.True(Math.Min(tight.dx, tight.dy) <= 20 + 1e-6,
            $"无间隙时应该贴边，实际 dx={tight.dx}, dy={tight.dy}");

        var spaced = Place(6);
        Assert.True(Math.Max(spaced.dx, spaced.dy) >= 26 - 1e-3 || Math.Min(spaced.dx, spaced.dy) >= 26 - 1e-3,
            $"间隙 6 未生效，实际 dx={spaced.dx}, dy={spaced.dy}");
    }

    [Fact]
    public void Spacing_IsEnforced_InsideHole()
    {
        // 板料 100x100；大件 40x40 带 20x20 孔（孔在 10..30）；小件 10x10；间隙 4
        // 小件中心须离孔壁 >= 4 → t 必须落在 [14, 16]
        var config = new NestConfig
        {
            Spacing = 4,
            Rotations = 1,
            AllowMirror = false,
            MaxSheets = 10
        };

        var sheetPoly = GeometryTests.Square(0, 0, 100, 100);
        sheetPoly.SourceId = 900;
        var sheet = new SheetTemplate { Shape = sheetPoly, SourceId = 900 };

        var big = GeometryTests.Square(0, 0, 40, 40);
        big.SourceId = 100;
        var hole = GeometryTests.Square(10, 10, 20, 20);
        hole.SourceId = 101;
        big.Children.Add(hole);

        var small = GeometryTests.Square(0, 0, 10, 10);
        small.SourceId = 102;

        var instances = new List<PartInstance>
        {
            new() { Id = 0, SourceId = 100, Shape = big },
            new() { Id = 1, SourceId = 102, Shape = small }
        };

        var engine = new PlacementEngine(new ClipperNfpEngine { Spacing = 4 }, config);
        var genes = instances.Select(p => new Gene(p.Id, 0, false)).ToArray();
        var result = engine.Place(sheet, instances, genes, CancellationToken.None);

        Assert.Equal(2, result.PlacedCount);
        Assert.Equal(1, result.SheetCount);

        var smallPlacement = result.Sheets[0].Placements.Single(p => p.InstanceId == 1);
        Assert.InRange(smallPlacement.X, 14 - 1e-6, 16 + 1e-6);
        Assert.InRange(smallPlacement.Y, 14 - 1e-6, 16 + 1e-6);
    }

    [Fact]
    public void NestSession_PassesSpacingToNfpEngine()
    {
        var config = new NestConfig { Spacing = 4.5, Rotations = 1 };        var sheetPoly = GeometryTests.Square(0, 0, 100, 100);
        sheetPoly.SourceId = 900;
        var sheet = new SheetTemplate { Shape = sheetPoly, SourceId = 900 };
        var parts = new List<PartInstance>
        {
            new() { Id = 0, SourceId = 100, Shape = GeometryTests.Square(0, 0, 20, 20) }
        };

        var engine = new ClipperNfpEngine();
        _ = new NestSession(config, parts, sheet, engine);

        Assert.Equal(4.5, engine.Spacing, 6);
    }
}
