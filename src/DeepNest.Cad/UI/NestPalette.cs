using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Windows;
using DeepNest.Cad.Cad;
using DeepNest.Cad.Nesting;
using DeepNest.Cad.Settings;
using DeepNest.Core.Geometry;
using DeepNest.Core.Nesting;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace DeepNest.Cad.UI;

public static class NestPalette
{
    private static PaletteSet? _paletteSet;
    private static NestPaletteControl? _control;

    public static void Show()
    {
        if (_paletteSet is null)
        {
            _control = new NestPaletteControl();
            _paletteSet = new PaletteSet("DeepNest", new Guid("6f1b8c62-9a3e-4f4c-9f2a-2b3f0c5d7e11"))
            {
                Dock = DockSides.Left
            };
            _paletteSet.AddVisual("排样", _control);
        }

        _paletteSet.Visible = true;
        _paletteSet.Activate(0);
        _control?.RefreshTheme();
    }
}

public sealed class NestPaletteControl : UserControl
{
    private readonly TextBox _spacing = NumberBox(out _);
    private readonly TextBox _tolerance = NumberBox(out _);
    private readonly TextBox _joinTolerance = NumberBox(out _);
    private readonly TextBox _rotations = NumberBox(out _);
    private readonly TextBox _population = NumberBox(out _);
    private readonly TextBox _mutation = NumberBox(out _);
    private readonly TextBox _sheetsPerRow = NumberBox(out _);
    private readonly TextBox _columnGap = NumberBox(out _);
    private readonly TextBox _rowGap = NumberBox(out _);
    private readonly TextBox _maxSeconds = NumberBox(out _);
    private readonly TextBox _maxGenerations = NumberBox(out _);
    private readonly CheckBox _allowMirror = new() { Content = "允许镜像（可能镜像文字）" };
    private readonly CheckBox _mergeLines = new() { Content = "共边合并（较慢）" };

    private readonly TextBlock _status = new() { Text = "未选择对象。", TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Height = 16, Minimum = 0, Maximum = 1 };
    private readonly ListBox _results = new() { Height = 120 };
    private readonly Button _pick = new() { Content = "选择板料与零件" };
    private readonly Button _start = new() { Content = "开始排样", IsEnabled = false };
    private readonly Button _stop = new() { Content = "停止", IsEnabled = false };
    private readonly Button _apply = new() { Content = "应用选中方案", IsEnabled = false };

    private readonly NestSettings _settings;
    private readonly List<NestResult> _nestResults = new();
    private CollectedModel? _model;
    private CancellationTokenSource? _cts;

    public NestPaletteControl()
    {
        _settings = SettingsStore.Load();
        LoadSettingsToUi();

        var panel = new StackPanel { Margin = new Thickness(8) };

        panel.Children.Add(_pick);
        panel.Children.Add(new Separator());
        panel.Children.Add(SectionTitle("排样参数"));
        panel.Children.Add(Row("零件间隙", _spacing));
        panel.Children.Add(Row("曲线容差", _tolerance));
        panel.Children.Add(Row("端点焊接容差", _joinTolerance));
        panel.Children.Add(Row("旋转等分数", _rotations));
        panel.Children.Add(Row("种群大小", _population));
        panel.Children.Add(Row("变异率(%)", _mutation));
        panel.Children.Add(_allowMirror);
        panel.Children.Add(_mergeLines);

        panel.Children.Add(new Separator());
        panel.Children.Add(SectionTitle("结果布局"));
        panel.Children.Add(Row("每行板数", _sheetsPerRow));
        panel.Children.Add(Row("列间距", _columnGap));
        panel.Children.Add(Row("行间距", _rowGap));

        panel.Children.Add(new Separator());
        panel.Children.Add(SectionTitle("运行上限"));
        panel.Children.Add(Row("最长运行秒", _maxSeconds));
        panel.Children.Add(Row("最大世代", _maxGenerations));

        panel.Children.Add(new Separator());
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_start);
        buttons.Children.Add(_stop);
        panel.Children.Add(buttons);

        panel.Children.Add(_progress);
        panel.Children.Add(_results);
        panel.Children.Add(_apply);
        panel.Children.Add(new Separator());
        panel.Children.Add(_status);

        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        ThemePalette.Apply(this);

        _pick.Click += (_, _) => SendPickCommand();
        _start.Click += OnStart;
        _stop.Click += OnStop;
        _apply.Click += OnApply;
        _results.SelectionChanged += (_, _) =>
        {
            _apply.IsEnabled = _results.SelectedIndex >= 0;
            UpdatePreview();
        };

        Current = this;
    }

    /// <summary>当前面板实例（供命令回调）。</summary>
    public static NestPaletteControl? Current { get; private set; }

    /// <summary>CAD 主题切换后重新着色。</summary>
    public void RefreshTheme() => ThemePalette.Apply(this);

    private static void SendPickCommand()
    {
        AcadApp.DocumentManager.MdiActiveDocument?.SendStringToExecute("DEEPNESTPICK ", true, false, false);
    }

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.Bold,
        Margin = new Thickness(0, 6, 0, 4)
    };

    private static TextBox NumberBox(out TextBox box)
    {
        box = new TextBox { Width = 90, HorizontalAlignment = HorizontalAlignment.Right };
        return box;
    }

    private static Grid Row(string label, TextBox box)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(box, 1);
        grid.Children.Add(text);
        grid.Children.Add(box);
        return grid;
    }

    private void LoadSettingsToUi()
    {
        _spacing.Text = _settings.Spacing.ToString("0.###", CultureInfo.InvariantCulture);
        _tolerance.Text = _settings.CurveTolerance.ToString("0.###", CultureInfo.InvariantCulture);
        _joinTolerance.Text = _settings.JoinTolerance.ToString("0.###", CultureInfo.InvariantCulture);
        _rotations.Text = _settings.Rotations.ToString(CultureInfo.InvariantCulture);
        _population.Text = _settings.PopulationSize.ToString(CultureInfo.InvariantCulture);
        _mutation.Text = _settings.MutationRate.ToString(CultureInfo.InvariantCulture);
        _sheetsPerRow.Text = _settings.SheetsPerRow.ToString(CultureInfo.InvariantCulture);
        _columnGap.Text = _settings.ColumnGap.ToString("0.###", CultureInfo.InvariantCulture);
        _rowGap.Text = _settings.RowGap.ToString("0.###", CultureInfo.InvariantCulture);
        _maxSeconds.Text = _settings.MaxSeconds.ToString(CultureInfo.InvariantCulture);
        _maxGenerations.Text = _settings.MaxGenerations.ToString(CultureInfo.InvariantCulture);
        _allowMirror.IsChecked = _settings.AllowMirror;
        _mergeLines.IsChecked = _settings.MergeLines;
    }

    private void SaveUiToSettings()
    {
        _settings.Spacing = ParseDouble(_spacing.Text, _settings.Spacing);
        _settings.CurveTolerance = ParseDouble(_tolerance.Text, _settings.CurveTolerance);
        _settings.JoinTolerance = ParseDouble(_joinTolerance.Text, _settings.JoinTolerance);
        _settings.Rotations = (int)ParseDouble(_rotations.Text, _settings.Rotations);
        _settings.PopulationSize = (int)ParseDouble(_population.Text, _settings.PopulationSize);
        _settings.MutationRate = (int)ParseDouble(_mutation.Text, _settings.MutationRate);
        _settings.SheetsPerRow = (int)ParseDouble(_sheetsPerRow.Text, _settings.SheetsPerRow);
        _settings.ColumnGap = ParseDouble(_columnGap.Text, _settings.ColumnGap);
        _settings.RowGap = ParseDouble(_rowGap.Text, _settings.RowGap);
        _settings.MaxSeconds = (int)ParseDouble(_maxSeconds.Text, _settings.MaxSeconds);
        _settings.MaxGenerations = (int)ParseDouble(_maxGenerations.Text, _settings.MaxGenerations);
        _settings.AllowMirror = _allowMirror.IsChecked == true;
        _settings.MergeLines = _mergeLines.IsChecked == true;
        SettingsStore.Save(_settings);
    }

    private static double ParseDouble(string text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    /// <summary>在命令上下文中执行对象选择（由 DEEPNESTPICK 调用）。</summary>
    public void PickInCommandContext()
    {
        var document = AcadApp.DocumentManager.MdiActiveDocument;
        if (document is null)
        {
            return;
        }

        ColorMarker.TryRestoreAll();
        PreviewRenderer.Clear();
        SaveUiToSettings();

        try
        {
            document.Editor.WriteMessage("\n请选择板料（闭合多段线）与零件（块参照），回车结束。");
            var prompt = document.Editor.GetSelection();
            if (prompt.Status != PromptStatus.OK || prompt.Value is null)
            {
                return;
            }

            var ids = prompt.Value.GetObjectIds().ToList();
            _model = PartCollector.Collect(document, ids, _settings);

            if (_model.OpenDefinitionIds.Count > 0)
            {
                ColorMarker.Apply(_model.OpenDefinitionIds.Distinct().ToList(), MarkerColor.Yellow);
            }
            if (_model.UnusableDefinitionIds.Count > 0)
            {
                ColorMarker.Apply(_model.UnusableDefinitionIds.Distinct().ToList(), MarkerColor.Red);
            }

            _status.Text = _model.Message + (_model.Warnings.Count > 0
                ? "\n" + string.Join("\n", _model.Warnings.Take(6))
                : string.Empty);
            _start.IsEnabled = _model.Success;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            _status.Text = "选择失败：" + ex.Message;
        }
        catch (System.Exception ex)
        {
            _status.Text = "读取失败：" + ex.Message;
        }
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_model is null || !_model.Success)
        {
            return;
        }

        SaveUiToSettings();
        _cts = new CancellationTokenSource();
        _start.IsEnabled = false;
        _stop.IsEnabled = true;
        _apply.IsEnabled = false;
        _results.Items.Clear();
        _nestResults.Clear();
        _progress.Value = 0;

        var model = _model;
        var settings = _settings;
        var token = _cts.Token;

        var progress = new Progress<NestProgress>(p =>
        {
            _progress.Value = Math.Clamp(p.Value, 0, 1);
            _status.Text = p.Phase switch
            {
                "nfp" => $"预计算 NFP… {p.Value:P0}",
                "placing" => $"摆放中… {p.Value:P0}",
                _ => $"第 {p.Generation} 代，最优 {p.BestFitness:0.##}，用板 {p.BestSheetCount}，已排 {p.Placed}/{p.Total}"
            };
        });

        try
        {
            var results = await Task.Run(() => NestController.Run(model, settings, token, progress), token);

            // 按板材利用率从高到低排序（利用率 = 实际材料面积 / 用掉的板料面积）
            var ranked = results
                .Select(r => new { Result = r, Utilization = Utilization(model, r) })
                .OrderByDescending(t => t.Utilization)
                .ThenBy(t => t.Result.SheetCount)
                .ThenBy(t => t.Result.Fitness)
                .ToList();

            _nestResults.Clear();
            _nestResults.AddRange(ranked.Select(t => t.Result));

            _results.Items.Clear();
            for (int i = 0; i < ranked.Count; i++)
            {
                var r = ranked[i].Result;
                var forced = r.ForcedCount > 0 ? $" · 强制 {r.ForcedCount}" : string.Empty;
                _results.Items.Add(
                    $"方案 {i + 1}：利用率 {ranked[i].Utilization:P1} · 用板 {r.SheetCount} · {r.PlacedCount} 件{forced}");
            }

            if (ranked.Count > 0)
            {
                _results.SelectedIndex = 0;
                var best = ranked[0];
                _status.Text = best.Result.ForcedCount > 0
                    ? $"完成：{ranked.Count} 个方案；最高利用率 {best.Utilization:P1}，用板 {best.Result.SheetCount} 张" +
                      $"（其中 {best.Result.ForcedCount} 件强制放置，需人工调整）。"
                    : $"完成：{ranked.Count} 个方案；最高利用率 {best.Utilization:P1}，用板 {best.Result.SheetCount} 张。";
            }
            else
            {
                _status.Text = "没有得到有效结果。";
            }
        }
        catch (OperationCanceledException)
        {
            _status.Text = "已停止。";
        }
        catch (System.Exception ex)
        {
            _status.Text = "排样失败：" + ex.Message;
        }
        finally
        {
            _start.IsEnabled = true;
            _stop.IsEnabled = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnStop(object sender, RoutedEventArgs e) => _cts?.Cancel();

    /// <summary>板材利用率 = 所有零件实际材料面积 /（板料面积 × 用板张数）。</summary>
    private static double Utilization(CollectedModel model, NestResult result)
    {
        if (model.SheetShape is null || result.SheetCount <= 0)
        {
            return 0;
        }

        double sheetArea = Math.Abs(GeometryUtil.SignedArea(model.SheetShape.Points));
        if (sheetArea <= 0)
        {
            return 0;
        }

        double used = 0;
        foreach (var sheet in result.Sheets)
        {
            foreach (var placement in sheet.Placements)
            {
                if (placement.InstanceId >= 0 && placement.InstanceId < model.Parts.Count)
                {
                    used += MaterialArea(model.Parts[placement.InstanceId].Shape);
                }
            }
        }

        return used / (sheetArea * result.SheetCount);
    }

    /// <summary>零件材料面积（外环面积减去孔面积）。</summary>
    private static double MaterialArea(Poly poly)
    {
        double area = Math.Abs(GeometryUtil.SignedArea(poly.Points));
        foreach (var child in poly.Children)
        {
            area -= MaterialArea(child);
        }

        return area;
    }

    private ApplyOptions BuildApplyOptions() => new()
    {
        SheetsPerRow = _settings.SheetsPerRow,
        ColumnGap = _settings.ColumnGap,
        RowGap = _settings.RowGap,
        SheetLayer = _settings.SheetLayer
    };

    private void UpdatePreview()
    {
        var model = _model;
        int index = _results.SelectedIndex;

        if (model is null || index < 0 || index >= _nestResults.Count)
        {
            PreviewRenderer.Clear();
            return;
        }

        try
        {
            PreviewRenderer.Show(model, _nestResults[index], BuildApplyOptions(), model.Parts);
        }
        catch (System.Exception ex)
        {
            _status.Text = "预览失败：" + ex.Message;
        }
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        var document = AcadApp.DocumentManager.MdiActiveDocument;
        if (document is null || _model is null)
        {
            return;
        }

        int index = _results.SelectedIndex;
        if (index < 0 || index >= _nestResults.Count)
        {
            return;
        }

        PreviewRenderer.Clear();

        var options = new ApplyOptions
        {
            SheetsPerRow = _settings.SheetsPerRow,
            ColumnGap = _settings.ColumnGap,
            RowGap = _settings.RowGap,
            SheetLayer = _settings.SheetLayer
        };

        try
        {
            int count = ResultWriter.Apply(
                document, _model, _nestResults[index], _model.Parts, options, out double deviation);
            ColorMarker.TryRestoreAll();
            document.Editor.Regen();
            _status.Text = $"已写入 {count} 个块参照与 {_nestResults[index].Sheets.Count} 个板料框；" +
                           $"最大位置偏差 {deviation:0.###}。";
        }
        catch (System.Exception ex)
        {
            _status.Text = "写入失败：" + ex.Message;
        }
    }
}
