using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;

namespace DeepNest.Cad;

public enum MarkerColor
{
    Yellow,
    Red
}

/// <summary>
/// 临时上色（黄 = 开放轮廓件仍参与排样；红 = 无可用轮廓、跳过）。
/// 只改实体 Color，结束/应用/取消/Terminate 时逐条恢复；
/// 残留快照落 %APPDATA%\DeepNestCad\pending_recolor.json，下次启动可自动恢复。
/// </summary>
public static class ColorMarker
{
    private sealed class SnapshotEntry
    {
        public string Handle { get; set; } = string.Empty;
        public int ColorIndex { get; set; }
        public int ColorMethod { get; set; }
        public double Red { get; set; }
        public double Green { get; set; }
        public double Blue { get; set; }
    }

    private static readonly Dictionary<ObjectId, Color> Snapshot = new();
    private static readonly object Gate = new();

    public static string PendingPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DeepNestCad",
        "pending_recolor.json");

    /// <summary>把块定义内的实体临时染成黄/红。</summary>
    public static void Apply(IEnumerable<ObjectId> blockDefinitionIds, MarkerColor color)
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            return;
        }

        var target = color == MarkerColor.Yellow
            ? Color.FromColorIndex(ColorMethod.ByAci, 2)
            : Color.FromColorIndex(ColorMethod.ByAci, 1);

        using var documentLock = doc.LockDocument();
        using var tr = doc.TransactionManager.StartTransaction();
        try
        {
            foreach (var btrId in blockDefinitionIds)
            {
                if (btrId.IsErased)
                {
                    continue;
                }

                var btr = tr.GetObject(btrId, OpenMode.ForRead, false) as BlockTableRecord;
                if (btr is null)
                {
                    continue;
                }

                foreach (var entId in btr)
                {
                    if (entId.IsErased)
                    {
                        continue;
                    }

                    if (tr.GetObject(entId, OpenMode.ForWrite, false) is not Entity entity)
                    {
                        continue;
                    }

                    lock (Gate)
                    {
                        Snapshot[entId] = entity.Color;
                    }

                    entity.Color = target;
                }
            }

            tr.Commit();
        }
        catch
        {
            tr.Abort();
            throw;
        }

        SaveSnapshot(doc);
    }

    /// <summary>恢复所有被临时改色的实体。</summary>
    public static void RestoreAll()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            return;
        }

        Dictionary<ObjectId, Color> snapshot;
        lock (Gate)
        {
            if (Snapshot.Count == 0)
            {
                return;
            }
            snapshot = new Dictionary<ObjectId, Color>(Snapshot);
            Snapshot.Clear();
        }

        using var documentLock = doc.LockDocument();
        using var tr = doc.TransactionManager.StartTransaction();
        try
        {
            foreach (var (id, color) in snapshot)
            {
                if (id.IsErased)
                {
                    continue;
                }

                if (tr.GetObject(id, OpenMode.ForWrite, false) is Entity entity)
                {
                    entity.Color = color;
                }
            }

            tr.Commit();
        }
        catch
        {
            tr.Abort();
        }

        TryDeleteSnapshot();
    }

    public static void TryRestoreAll()
    {
        try
        {
            RestoreAll();
        }
        catch
        {
            // 忽略：卸载或文档切换阶段
        }
    }

    /// <summary>上次运行异常退出时，尝试按 Handle 恢复残留颜色。</summary>
    public static void TryRecoverPending()
    {
        try
        {
            if (!File.Exists(PendingPath))
            {
                return;
            }

            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc is null)
            {
                return;
            }

            var json = File.ReadAllText(PendingPath);
            var entries = System.Text.Json.JsonSerializer.Deserialize<List<SnapshotEntry>>(json);
            if (entries is null || entries.Count == 0)
            {
                TryDeleteSnapshot();
                return;
            }

            var db = doc.Database;
            using var tr = doc.TransactionManager.StartTransaction();
            foreach (var entry in entries)
            {
                if (!long.TryParse(entry.Handle, System.Globalization.NumberStyles.HexNumber, null, out var value))
                {
                    continue;
                }

                var id = db.GetObjectId(false, new Handle(value), 0);
                if (id.IsNull || id.IsErased)
                {
                    continue;
                }

                if (tr.GetObject(id, OpenMode.ForWrite, false) is Entity entity)
                {
                    if (entry.ColorMethod == (int)ColorMethod.ByAci)
                    {
                        entity.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)entry.ColorIndex);
                    }
                    else
                    {
                        entity.Color = Color.FromRgb((byte)entry.Red, (byte)entry.Green, (byte)entry.Blue);
                    }
                }
            }
            tr.Commit();

            TryDeleteSnapshot();
            PluginEntry.WriteMessage("已恢复上次未还原的临时颜色。");
        }
        catch
        {
            // 恢复失败不阻断加载
        }
    }

    private static void SaveSnapshot(Document doc)
    {
        try
        {
            List<SnapshotEntry> entries;
            lock (Gate)
            {
                entries = new List<SnapshotEntry>(Snapshot.Count);
                var db = doc.Database;
                foreach (var (id, color) in Snapshot)
                {
                    var handle = id.Handle;
                    entries.Add(new SnapshotEntry
                    {
                        Handle = handle.Value.ToString("X"),
                        ColorIndex = color.ColorIndex,
                        ColorMethod = (int)color.ColorMethod,
                        Red = color.Red,
                        Green = color.Green,
                        Blue = color.Blue
                    });
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(PendingPath)!);
            File.WriteAllText(PendingPath, System.Text.Json.JsonSerializer.Serialize(entries));
        }
        catch
        {
            // 快照落盘失败不阻断流程
        }
    }

    private static void TryDeleteSnapshot()
    {
        try
        {
            if (File.Exists(PendingPath))
            {
                File.Delete(PendingPath);
            }
        }
        catch
        {
            // 忽略
        }
    }
}
