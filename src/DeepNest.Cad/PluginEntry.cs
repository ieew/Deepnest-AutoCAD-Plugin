using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;

[assembly: ExtensionApplication(typeof(DeepNest.Cad.PluginEntry))]
[assembly: CommandClass(typeof(DeepNest.Cad.Commands))]

namespace DeepNest.Cad;

/// <summary>插件入口。NETLOAD 后 AutoCAD 调用 Initialize。</summary>
public sealed class PluginEntry : IExtensionApplication
{
    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

    public void Initialize()
    {
        // 上次异常退出可能残留临时颜色，这里尝试恢复（不修改任何系统变量/配置）
        ColorMarker.TryRecoverPending();

        WriteMessage($"DeepNest for AutoCAD {Version} 已加载。命令：DEEPNEST（面板）/ DEEPNESTINFO / DEEPNESTDUMP。");
    }

    public void Terminate()
    {
        // 确保临时上色被恢复（异常退出时由下次启动的残留检查兜底）
        try
        {
            ColorMarker.TryRestoreAll();
        }
        catch
        {
            // 卸载阶段不抛异常
        }
    }

    internal static void WriteMessage(string message)
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        doc?.Editor?.WriteMessage($"\n[DeepNest] {message}");
    }
}
