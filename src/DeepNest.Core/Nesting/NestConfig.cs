namespace DeepNest.Core.Nesting;

/// <summary>排样评价方式（对应原版 placementType）。</summary>
public enum PlacementType
{
    /// <summary>重力感：优先压左边与下边（原版 Y 向下时为左上，这里改为左下）。</summary>
    Gravity,

    /// <summary>包围盒面积最小。</summary>
    Box,

    /// <summary>凸包面积最小。</summary>
    ConvexHull
}

/// <summary>排样参数。所有长度均为图纸单位。</summary>
public sealed class NestConfig
{
    /// <summary>零件之间的间隙。每个零件外扩 spacing/2，每层轮廓按其材料方向偏移。</summary>
    public double Spacing { get; set; }

    /// <summary>曲线离散与几何清理的容差（默认 0.1 图纸单位，适合 mm 图纸）。</summary>
    public double CurveTolerance { get; set; } = 0.1;

    /// <summary>旋转等分数，步长 = 360 / Rotations。</summary>
    public int Rotations { get; set; } = 4;

    public int PopulationSize { get; set; } = 10;

    /// <summary>变异率（百分数，10 表示 10%）。</summary>
    public int MutationRate { get; set; } = 10;

    public PlacementType PlacementType { get; set; } = PlacementType.Gravity;

    /// <summary>是否把共边（重合边）计入收益。默认关闭：它是最大性能热点。</summary>
    public bool MergeLines { get; set; }

    /// <summary>共边收益的时间/材料权重。</summary>
    public double TimeRatio { get; set; } = 0.5;

    /// <summary>是否允许镜像（默认关闭）。</summary>
    public bool AllowMirror { get; set; }

    /// <summary>共边判定的最小边长（小于该长度的边不参与）。</summary>
    public double MinMergeLength { get; set; } = 12.7;

    /// <summary>单张板料最多生成多少份（防止无限增长）。</summary>
    public int MaxSheets { get; set; } = 200;

    /// <summary>总运行时长上限；null 表示不做时间限制。</summary>
    public TimeSpan? MaxDuration { get; set; }

    /// <summary>最大世代数；null 表示不做限制。</summary>
    public int? MaxGenerations { get; set; }

    /// <summary>随机种子（保证可复现）。</summary>
    public int Seed { get; set; } = 12345;

    /// <summary>NFP 预计算并行度。</summary>
    public int Threads { get; set; } = Math.Max(1, Environment.ProcessorCount - 1);

    public double RotationStep => 360.0 / Math.Max(1, Rotations);

    /// <summary>每张板料按需生成时，单个零件最多尝试的旋转次数。</summary>
    public int RotationAttempts => Math.Max(1, (int)Math.Round(360.0 / RotationStep));

    public NestConfig Clone() => (NestConfig)MemberwiseClone();

    public void Validate()
    {
        if (Rotations < 1) Rotations = 1;
        if (Rotations > 360) Rotations = 360;
        if (PopulationSize < 3) PopulationSize = 3;
        if (MutationRate < 1) MutationRate = 1;
        if (MutationRate > 100) MutationRate = 100;
        if (Spacing < 0) Spacing = 0;
        if (CurveTolerance <= 0) CurveTolerance = 0.1;
        if (MinMergeLength < 0) MinMergeLength = 0;
        if (MaxSheets < 1) MaxSheets = 1;
        if (Threads < 1) Threads = 1;
    }
}
