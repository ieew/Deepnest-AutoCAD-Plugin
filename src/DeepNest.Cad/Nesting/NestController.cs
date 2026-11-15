using DeepNest.Cad.Cad;
using DeepNest.Cad.Settings;
using DeepNest.Core.Nesting;

namespace DeepNest.Cad.Nesting;

/// <summary>把 UI 设置 + 收集结果转换成排样内核输入并运行。</summary>
public static class NestController
{
    public static NestConfig ToConfig(NestSettings settings) => new()
    {
        Spacing = settings.Spacing,
        CurveTolerance = settings.CurveTolerance,
        Rotations = settings.Rotations,
        PopulationSize = settings.PopulationSize,
        MutationRate = settings.MutationRate,
        PlacementType = Enum.TryParse<PlacementType>(settings.PlacementType, true, out var type)
            ? type
            : PlacementType.Gravity,
        MergeLines = settings.MergeLines,
        AllowMirror = settings.AllowMirror,
        MaxSheets = settings.MaxSheets,
        MaxDuration = settings.MaxSeconds > 0 ? TimeSpan.FromSeconds(settings.MaxSeconds) : null,
        MaxGenerations = settings.MaxGenerations > 0 ? settings.MaxGenerations : null,
        Seed = Environment.TickCount
    };

    public static List<PartInstance> ToInstances(CollectedModel model)
    {
        var instances = new List<PartInstance>(model.Parts.Count);
        for (int i = 0; i < model.Parts.Count; i++)
        {
            var info = model.Parts[i];
            instances.Add(new PartInstance
            {
                Id = i,
                SourceId = info.SourceId,
                Shape = info.Shape,
                Offset = info.Offset,
                Tag = info
            });
        }
        return instances;
    }

    /// <summary>运行排样，返回按 fitness 升序的方案列表（最多 10 个）。</summary>
    public static List<NestResult> Run(
        CollectedModel model,
        NestSettings settings,
        CancellationToken cancellationToken,
        IProgress<NestProgress>? progress)
    {
        if (!model.Success || model.SheetShape is null)
        {
            return new List<NestResult>();
        }

        var instances = ToInstances(model);
        var sheet = new SheetTemplate
        {
            Shape = model.SheetShape,
            Offset = model.SheetOffset,
            SourceId = model.SheetShape.SourceId
        };

        var session = new NestSession(ToConfig(settings), instances, sheet);
        session.Run(cancellationToken, progress);
        return session.BestResults.ToList();
    }
}
