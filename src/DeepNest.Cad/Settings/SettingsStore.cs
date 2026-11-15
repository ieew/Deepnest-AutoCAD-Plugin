using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeepNest.Cad.Settings;

/// <summary>
/// 插件设置。只写入 %APPDATA%\DeepNestCad\settings.json，
/// 不读写注册表、不修改 AutoCAD 配置或系统变量。
/// </summary>
public sealed class NestSettings
{
    // —— 排样参数（图纸单位） ——
    public double Spacing { get; set; } = 2.0;
    public double CurveTolerance { get; set; } = 0.1;
    public int Rotations { get; set; } = 4;
    public int PopulationSize { get; set; } = 10;
    public int MutationRate { get; set; } = 10;
    public bool AllowMirror { get; set; }
    public bool MergeLines { get; set; }
    public string PlacementType { get; set; } = "Gravity";
    public int MaxSheets { get; set; } = 200;

    /// <summary>单次排样最长运行秒数（0 = 不限）。</summary>
    public int MaxSeconds { get; set; } = 60;

    /// <summary>最大世代数（0 = 不限）。</summary>
    public int MaxGenerations { get; set; } = 30;

    // —— 几何提取 ——
    public double JoinTolerance { get; set; } = 0.1;

    // —— 结果布局 ——
    public int SheetsPerRow { get; set; } = 2;
    public double ColumnGap { get; set; } = 50;
    public double RowGap { get; set; } = 50;
    public string SheetLayer { get; set; } = "DEEPNEST_SHEET";

    public NestSettings Clone() => (NestSettings)MemberwiseClone();
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DeepNestCad",
        "settings.json");

    public static NestSettings Load()
    {
        try
        {
            if (File.Exists(DefaultPath))
            {
                var json = File.ReadAllText(DefaultPath);
                var settings = JsonSerializer.Deserialize<NestSettings>(json, Options);
                if (settings is not null)
                {
                    return settings;
                }
            }
        }
        catch
        {
            // 设置损坏时退回默认值，不打断用户
        }

        return new NestSettings();
    }

    public static void Save(NestSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(DefaultPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(DefaultPath, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
            // 保存失败不影响当前会话
        }
    }
}
