using System.IO;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using DeepNest.Cad.Cad;
using DeepNest.Cad.Settings;
using DeepNest.Cad.UI;
using DeepNest.Core.Geometry;

namespace DeepNest.Cad;

public static class Commands
{
    /// <summary>冒烟命令：确认插件已正确加载。</summary>
    [CommandMethod("DEEPNESTINFO", CommandFlags.Modal)]
    public static void Info()
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        if (document is null)
        {
            return;
        }

        document.Editor.WriteMessage(
            $"\n[DeepNest] 版本 {PluginEntry.Version}" +
            $"\n[DeepNest] 设置文件：{SettingsStore.DefaultPath}" +
            $"\n[DeepNest] 图纸单位：{document.Database.Insunits}" +
            $"\n[DeepNest] 命令：DEEPNEST / DEEPNESTINFO / DEEPNESTDUMP / DEEPNESTCLEAN");
    }

    /// <summary>
    /// 清理上一次的排版结果（带 DEEPNEST XData 的副本、板料框、预览）。
    /// 只删本插件写的东西，不会动用户的原始块。
    /// </summary>
    [CommandMethod("DEEPNESTCLEAN", CommandFlags.Modal)]
    public static void Clean()
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        if (document is null)
        {
            return;
        }

        var editor = document.Editor;
        int removed = ResultWriter.CleanResults(document);
        editor.WriteMessage($"\n[DeepNest] 已删除 {removed} 个上一次的排版对象（副本/板料框/预览）。");
    }

    /// <summary>打开排样面板。</summary>
    [CommandMethod("DEEPNEST", CommandFlags.Modal)]
    public static void ShowPalette() => NestPalette.Show();

    /// <summary>面板上的"选择对象"按钮通过它进入命令上下文执行选择。</summary>
    [CommandMethod("DEEPNESTPICK", CommandFlags.Modal)]
    public static void PickForPalette() => NestPaletteControl.Current?.PickInCommandContext();

    /// <summary>
    /// 诊断用：把选择集解析出的板料/零件轮廓导出成 JSON，便于离线跑 Core 测试。
    /// </summary>
    [CommandMethod("DEEPNESTDUMP", CommandFlags.Modal)]
    public static void Dump()
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        if (document is null)
        {
            return;
        }

        var editor = document.Editor;
        var prompt = editor.GetSelection();
        if (prompt.Status != PromptStatus.OK || prompt.Value is null)
        {
            return;
        }

        var ids = prompt.Value.GetObjectIds().ToList();

        var settings = SettingsStore.Load();
        var model = PartCollector.Collect(document, ids, settings);
        editor.WriteMessage("\n" + model.Message);
        foreach (var warning in model.Warnings)
        {
            editor.WriteMessage("\n  - " + warning);
        }

        if (!model.Success || model.SheetShape is null)
        {
            return;
        }

        var dto = new
        {
            sheet = new
            {
                offset = new[] { model.SheetOffset.X, model.SheetOffset.Y },
                outer = ToPoints(model.SheetShape)
            },
            parts = model.Parts.Select(p => new
            {
                block = p.BlockName,
                sourceId = p.SourceId,
                kind = p.Kind.ToString(),
                offset = new[] { p.Offset.X, p.Offset.Y },
                outer = ToPoints(p.Shape),
                holes = p.Shape.Children.Select(ToPoints).ToArray()
            }).ToArray(),
            settings = new
            {
                spacing = settings.Spacing,
                tolerance = settings.CurveTolerance,
                joinTolerance = settings.JoinTolerance,
                rotations = settings.Rotations,
                population = settings.PopulationSize
            }
        };

        var path = Path.Combine(Path.GetTempPath(), "deepnest-dump.json");
        File.WriteAllText(path, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
        editor.WriteMessage($"\n已导出：{path}");
    }

    private static double[][] ToPoints(Poly poly) =>
        poly.Points.Select(p => new[] { Math.Round(p.X, 6), Math.Round(p.Y, 6) }).ToArray();
}
