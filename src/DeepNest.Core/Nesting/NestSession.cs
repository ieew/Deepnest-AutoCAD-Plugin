using System.Diagnostics;
using DeepNest.Core.Geometry;

namespace DeepNest.Core.Nesting;

public sealed class NestProgress
{
    public string Phase { get; init; } = string.Empty;
    public double Value { get; init; }
    public int Generation { get; init; }
    public double BestFitness { get; init; }
    public int BestSheetCount { get; init; }
    public int Placed { get; init; }
    public int Total { get; init; }
}

/// <summary>
/// 排样会话：并行预计算 NFP → 逐代评估个体（串行、确定性）→ 维护最优方案列表。
/// </summary>
public sealed class NestSession
{
    public const int MaxResults = 500;

    private readonly NestConfig _config;
    private readonly IReadOnlyList<PartInstance> _parts;
    private readonly SheetTemplate _sheet;
    private readonly INfpEngine _nfp;

    public NestSession(
        NestConfig config,
        IReadOnlyList<PartInstance> parts,
        SheetTemplate sheet,
        INfpEngine? nfp = null)
    {
        config.Validate();
        _config = config;
        _parts = parts;
        _sheet = sheet;
        _nfp = nfp ?? new ClipperNfpEngine();

        // 把零件间隙传给 NFP 引擎：零件外扩 Spacing/2，孔内缩 Spacing/2。
        if (_nfp is ClipperNfpEngine clipperEngine)
        {
            clipperEngine.Spacing = config.Spacing;
        }
    }

    public List<NestResult> BestResults { get; } = new();

    private readonly HashSet<long> _resultSignatures = new();

    public int GenerationCount { get; private set; }

    public NestResult? Run(CancellationToken cancellationToken, IProgress<NestProgress>? progress = null)
    {
        if (_parts.Count == 0)
        {
            return null;
        }

        // 大面积优先（原版 adam 排序），减少碎片
        var ordered = _parts
            .OrderByDescending(p => Math.Abs(GeometryUtil.SignedArea(p.Shape.Points)))
            .Select(p => p.Id)
            .ToArray();

        var random = new Random(_config.Seed);
        var ga = new GeneticAlgorithm(ordered, _config, random);

        // 用第一个个体做 NFP 预热（与后续缓存共享）
        PrecomputeNfp(ga.Population[0], cancellationToken, progress);

        var engine = new PlacementEngine(_nfp, _config);
        var stopwatch = Stopwatch.StartNew();
        double lastReported = -1;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (_config.MaxDuration is { } maxDuration && stopwatch.Elapsed >= maxDuration)
            {
                break;
            }
            if (_config.MaxGenerations is { } maxGenerations && GenerationCount >= maxGenerations)
            {
                break;
            }

            int index = Array.FindIndex(ga.Population, p => double.IsPositiveInfinity(p.Fitness));
            if (index < 0)
            {
                ga.Generation();
                GenerationCount++;
                continue;
            }

            var individual = ga.Population[index];
            individual.Processing = true;

            var result = engine.Place(
                _sheet,
                _parts,
                individual.Genes,
                cancellationToken,
                value =>
                {
                    if (value - lastReported < 0.02)
                    {
                        return;
                    }
                    lastReported = value;
                    progress?.Report(new NestProgress
                    {
                        Phase = "placing",
                        Value = Math.Min(1, value),
                        Generation = GenerationCount,
                        Placed = BestResults.Count > 0 ? BestResults[0].PlacedCount : 0,
                        Total = _parts.Count
                    });
                });

            individual.Processing = false;
            individual.Fitness = result.Fitness;
            individual.EvaluatedGeneration = GenerationCount;

            UpdateBest(result, GenerationCount);

            progress?.Report(new NestProgress
            {
                Phase = "generation",
                Value = double.IsPositiveInfinity(ga.Population[0].Fitness) ? 0 : 1,
                Generation = GenerationCount,
                BestFitness = BestResults.Count > 0 ? BestResults[0].Fitness : double.NaN,
                BestSheetCount = BestResults.Count > 0 ? BestResults[0].SheetCount : 0,
                Placed = BestResults.Count > 0 ? BestResults[0].PlacedCount : 0,
                Total = _parts.Count
            });
        }

        return BestResults.Count > 0 ? BestResults[0] : null;
    }

    private void UpdateBest(NestResult result, int generation)
    {
        result.Generation = generation;

        // 结果去重：遗传过程中很多个体算出来的落位是完全一样的，
        // 不去重的话列表里会出现大量重复方案。
        long signature = SignatureOf(result);
        if (!_resultSignatures.Add(signature))
        {
            return;
        }

        if (BestResults.Count >= MaxResults && BestResults[^1].Fitness <= result.Fitness)
        {
            _resultSignatures.Remove(signature);
            return;
        }

        int insertAt = BestResults.FindIndex(r => r.Fitness > result.Fitness);
        if (insertAt < 0)
        {
            BestResults.Add(result);
        }
        else
        {
            BestResults.Insert(insertAt, result);
        }

        if (BestResults.Count > MaxResults)
        {
            var dropped = BestResults[^1];
            BestResults.RemoveAt(BestResults.Count - 1);
            _resultSignatures.Remove(SignatureOf(dropped));
        }
    }

    /// <summary>落位签名：同一套落位（忽略世代号）视为同一个方案。</summary>
    private static long SignatureOf(NestResult result)
    {
        var hash = new HashCode();
        hash.Add(result.Sheets.Count);
        foreach (var sheet in result.Sheets)
        {
            foreach (var placement in sheet.Placements)
            {
                hash.Add(placement.InstanceId);
                hash.Add(Math.Round(placement.X, 3));
                hash.Add(Math.Round(placement.Y, 3));
                hash.Add(Math.Round(placement.Rotation, 3));
                hash.Add(placement.Mirrored);
            }
        }

        return hash.ToHashCode();
    }

    /// <summary>并行预热第一个个体涉及的 NFP 组合（板料允许区域 + 两两禁入区域）。</summary>
    private void PrecomputeNfp(Individual individual, CancellationToken cancellationToken, IProgress<NestProgress>? progress)
    {
        var variants = new List<(PartInstance Instance, Poly Poly)>(individual.Genes.Length);
        foreach (var gene in individual.Genes)
        {
            var instance = _parts.FirstOrDefault(p => p.Id == gene.InstanceId);
            if (instance is null)
            {
                continue;
            }

            var poly = instance.Shape.Clone();
            poly.SourceId = instance.SourceId;
            if (gene.Mirrored && _config.AllowMirror)
            {
                poly = GeometryUtil.Mirror(poly);
            }
            poly = GeometryUtil.Rotate(poly, gene.RotationIndex * _config.RotationStep);
            variants.Add((instance, poly));
        }

        if (variants.Count == 0)
        {
            return;
        }

        var pairs = new List<(int A, int B)>();
        for (int i = 0; i < variants.Count; i++)
        {
            for (int j = 0; j < i; j++)
            {
                pairs.Add((j, i));
            }
        }

        int done = 0;
        int total = pairs.Count + variants.Count;
        object gate = new();

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = _config.Threads,
            CancellationToken = cancellationToken
        };

        try
        {
            Parallel.ForEach(variants, options, variant =>
            {
                _nfp.InnerNfp(_sheet.Shape, variant.Poly);
                Report(Interlocked.Increment(ref done));
            });

            Parallel.ForEach(pairs, options, pair =>
            {
                _nfp.OuterNfp(variants[pair.A].Poly, variants[pair.B].Poly);
                Report(Interlocked.Increment(ref done));
            });
        }
        catch (OperationCanceledException)
        {
            return;
        }

        void Report(int current)
        {
            double ratio;
            lock (gate)
            {
                ratio = current / (double)Math.Max(1, total);
            }
            progress?.Report(new NestProgress
            {
                Phase = "nfp",
                Value = 0.5 * ratio,
                Total = _parts.Count
            });
        }
    }
}
