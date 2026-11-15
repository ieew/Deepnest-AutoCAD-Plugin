namespace DeepNest.Core.Nesting;

/// <summary>
/// 遗传算法。逐行对齐原 <c>deepnest.js</c> 的实现：
/// 个体 0 随机初始化，其余由个体 0 变异而来；世代保留 1 个精英，
/// 其余由"轮盘权重选择父母 + OX 顺序交叉 + 变异"产生。
/// </summary>
public sealed class GeneticAlgorithm
{
    private readonly NestConfig _config;
    private readonly Random _random;

    public GeneticAlgorithm(IReadOnlyList<int> orderedInstanceIds, NestConfig config, Random? random = null)
    {
        _config = config;
        _random = random ?? new Random(config.Seed);

        var first = CreateRandom(orderedInstanceIds);
        Population = new Individual[config.PopulationSize];
        Population[0] = first;
        for (int i = 1; i < Population.Length; i++)
        {
            Population[i] = Mutate(first);
        }
    }

    public Individual[] Population { get; private set; }

    public int GenerationCount { get; private set; }

    private Individual CreateRandom(IReadOnlyList<int> ids)
    {
        var genes = new Gene[ids.Count];
        for (int i = 0; i < ids.Count; i++)
        {
            genes[i] = new Gene(
                ids[i],
                _random.Next(_config.Rotations),
                _config.AllowMirror && _random.Next(2) == 1);
        }
        return new Individual(genes);
    }

    /// <summary>变异：相邻换位、换角度、（可选）翻转镜像。</summary>
    public Individual Mutate(Individual individual)
    {
        int n = individual.Genes.Length;
        var genes = (Gene[])individual.Genes.Clone();
        if (n == 0)
        {
            return new Individual(genes);
        }

        double probability = 0.01 * _config.MutationRate;

        for (int i = 0; i < n; i++)
        {
            if (i < n - 1 && _random.NextDouble() < probability)
            {
                (genes[i], genes[i + 1]) = (genes[i + 1], genes[i]);
            }

            if (_random.NextDouble() < probability)
            {
                genes[i] = genes[i] with { RotationIndex = _random.Next(_config.Rotations) };
            }

            if (_config.AllowMirror && _random.NextDouble() < probability)
            {
                genes[i] = genes[i] with { Mirrored = !genes[i].Mirrored };
            }
        }

        return new Individual(genes);
    }

    /// <summary>顺序交叉（OX）。基因按 InstanceId 去重，保证结果仍是完整排列。</summary>
    public (Individual First, Individual Second) Mate(Individual male, Individual female)
    {
        int n = male.Genes.Length;
        if (n == 0)
        {
            return (new Individual(Array.Empty<Gene>()), new Individual(Array.Empty<Gene>()));
        }

        double ratio = 0.1 + (_random.NextDouble() * 0.8);
        int cut = (int)Math.Round(ratio * (n - 1));
        cut = Math.Clamp(cut, 1, Math.Max(1, n - 1));

        var first = new List<Gene>(n);
        var seenFirst = new HashSet<int>();
        for (int i = 0; i < cut; i++)
        {
            first.Add(male.Genes[i]);
            seenFirst.Add(male.Genes[i].InstanceId);
        }
        foreach (var gene in female.Genes)
        {
            if (seenFirst.Add(gene.InstanceId))
            {
                first.Add(gene);
            }
        }

        var second = new List<Gene>(n);
        var seenSecond = new HashSet<int>();
        for (int i = 0; i < cut; i++)
        {
            second.Add(female.Genes[i]);
            seenSecond.Add(female.Genes[i].InstanceId);
        }
        foreach (var gene in male.Genes)
        {
            if (seenSecond.Add(gene.InstanceId))
            {
                second.Add(gene);
            }
        }

        return (new Individual(first.ToArray()), new Individual(second.ToArray()));
    }

    /// <summary>产生下一代：精英保留 1 个，其余由交叉 + 变异填充。</summary>
    public void Generation()
    {
        Array.Sort(Population, (a, b) => a.Fitness.CompareTo(b.Fitness));

        var next = new List<Individual>(Population.Length) { Population[0] };
        while (next.Count < Population.Length)
        {
            var male = RandomWeighted(null);
            var female = RandomWeighted(male);
            var (child1, child2) = Mate(male, female);
            next.Add(Mutate(child1));
            if (next.Count < Population.Length)
            {
                next.Add(Mutate(child2));
            }
        }

        Population = next.ToArray();
        GenerationCount++;
    }

    /// <summary>原版轮盘选择：排名越靠前，区间越宽。</summary>
    private Individual RandomWeighted(Individual? exclude)
    {
        var pool = exclude is null
            ? Population
            : Population.Where(p => !ReferenceEquals(p, exclude)).ToArray();

        if (pool.Length == 0)
        {
            return Population[0];
        }

        double random = _random.NextDouble();
        double weight = 1.0 / pool.Length;
        double lower = 0;
        double upper = weight;

        for (int i = 0; i < pool.Length; i++)
        {
            if (random > lower && random < upper)
            {
                return pool[i];
            }
            lower = upper;
            upper += 2 * weight * ((pool.Length - i) / (double)pool.Length);
        }

        return pool[0];
    }
}
