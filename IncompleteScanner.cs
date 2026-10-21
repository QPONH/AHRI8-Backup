using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace AHRI8Backup;

/// <summary>一个不完整的作品文件夹。字段会原样保存到 <c>incomplete.json</c>，下次打开窗口还能看到。</summary>
internal sealed class IncompleteItem
{
    public string FolderName { get; set; } = "";
    public string FolderPath { get; set; } = "";
    public string WorkId { get; set; } = "";
    public string Title { get; set; } = "";
    public List<string> Authors { get; set; } = new();
    public string WorkUrl { get; set; } = "";
    public string ReaderUrl { get; set; } = "";
    public int FavoritePage { get; set; }
    public int FavoritePosition { get; set; }
    public int Expected { get; set; }
    public int Valid { get; set; }
    public List<int> DamagedPages { get; set; } = new();
    public List<int> MissingPages { get; set; } = new();
    public string CheckedAt { get; set; } = "";
    public string Note { get; set; } = "";

    [ScriptIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? FolderName : Title;

    [ScriptIgnore]
    public string ProblemText
    {
        get
        {
            var parts = new List<string>();
            if (DamagedPages.Count > 0) parts.Add($"损坏 {DamagedPages.Count} 张");
            if (MissingPages.Count > 0) parts.Add($"缺失 {MissingPages.Count} 张");
            if (!string.IsNullOrWhiteSpace(Note) && parts.Count == 0) parts.Add(Note);
            return parts.Count == 0 ? "需要补全" : string.Join("，", parts);
        }
    }

    [ScriptIgnore]
    public bool CanDownload => !string.IsNullOrWhiteSpace(WorkUrl);
}

/// <summary>
/// 「检查所有文件」：扫描输出目录下的**所有作品文件夹**，找出缺页/坏图/未完成的 .part，
/// 结果保存到输出目录的 <c>incomplete.json</c>，下次打开窗口时自动读回。
///
/// 这个检查不联网，也不依赖 download.log.jsonl 或 manifest.json 的“已完成”标记：
/// 每个文件都会重新按格式魔数与结尾标记验证。
/// </summary>
internal static class IncompleteScanner
{
    public const string FileName = "incomplete.json";

    public static string FilePath(string outputRoot) => Path.Combine(outputRoot, FileName);

    public static List<IncompleteItem> Scan(string outputRoot, CancellationToken ct = default)
    {
        var items = new List<IncompleteItem>();
        if (!Directory.Exists(outputRoot)) return items;

        foreach (var folder in Directory.EnumerateDirectories(outputRoot).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(folder);
            if (name.StartsWith("_", StringComparison.Ordinal)) continue;   // _debug 之类的辅助目录

            var manifest = TryReadManifest(folder);
            var history = new DownloadLog(folder);
            var expected = ExpectedPages(manifest, history);
            var report = FolderIntegrity.Check(folder, expected);
            var hasPart = false;
            try { hasPart = Directory.EnumerateFiles(folder, "*.part").Any(); } catch { }

            // 没有任何元数据、目录里也没有图片：无法判断，不列为“不完整”。
            if (report.Expected == 0 && report.ValidPages.Count == 0 && report.DamagedPages.Count == 0 && !hasPart)
                continue;

            var incomplete = report.ProblemCount > 0 || hasPart;
            if (!incomplete) continue;

            items.Add(new IncompleteItem
            {
                FolderName = name,
                FolderPath = folder,
                WorkId = manifest?.WorkId ?? "",
                Title = manifest?.Title ?? "",
                Authors = manifest?.Authors ?? new List<string>(),
                WorkUrl = manifest?.WorkUrl ?? "",
                ReaderUrl = manifest?.ReaderUrl ?? "",
                FavoritePage = manifest?.FavoritePage ?? 0,
                FavoritePosition = manifest?.FavoritePosition ?? 0,
                Expected = report.Expected,
                Valid = report.ValidPages.Count,
                DamagedPages = report.DamagedPages.OrderBy(p => p).ToList(),
                MissingPages = report.MissingPages.OrderBy(p => p).ToList(),
                CheckedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Note = hasPart ? "有未完成的 .part" : "",
            });
        }
        return items;
    }

    public static void Save(string outputRoot, IEnumerable<IncompleteItem> items)
    {
        try
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var payload = new IncompleteFile
            {
                UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Items = items.ToList(),
            };
            var path = FilePath(outputRoot);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonFormat.Pretty(serializer.Serialize(payload)), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }
        catch
        {
            // 保存失败不影响检查结果；窗口仍会显示本次扫描结果。
        }
    }

    public static List<IncompleteItem> Load(string outputRoot)
    {
        try
        {
            var path = FilePath(outputRoot);
            if (!File.Exists(path)) return new List<IncompleteItem>();
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var payload = serializer.Deserialize<IncompleteFile>(File.ReadAllText(path));
            return payload?.Items ?? new List<IncompleteItem>();
        }
        catch
        {
            return new List<IncompleteItem>();
        }
    }

    /// <summary>
    /// 文件夹被「更新文件夹序号」重命名后，同步 incomplete.json 里缓存的路径，
    /// 避免下次打开「检查所有文件」时仍指向旧目录。
    /// </summary>
    public static void Relocate(string outputRoot, IReadOnlyDictionary<string, string> oldToNewPaths)
    {
        if (oldToNewPaths.Count == 0) return;
        try
        {
            var items = Load(outputRoot);
            if (items.Count == 0) return;
            bool changed = false;
            foreach (var item in items)
            {
                if (oldToNewPaths.TryGetValue(item.FolderPath, out var newPath))
                {
                    item.FolderPath = newPath;
                    item.FolderName = Path.GetFileName(newPath);
                    changed = true;
                }
            }
            if (changed) Save(outputRoot, items);
        }
        catch
        {
            // 缓存文件更新失败不影响重命名本身；下次「重新检查」会重建结果。
        }
    }

    private static List<int> ExpectedPages(ScanManifest? manifest, DownloadLog history)
    {
        if (manifest?.ImageCount > 0) return Enumerable.Range(1, manifest.ImageCount).ToList();
        if (history.LoadedCount > 0) return history.Entries.Keys.OrderBy(p => p).ToList();
        return new List<int>();
    }

    private static ScanManifest? TryReadManifest(string folder)
    {
        try
        {
            var path = Path.Combine(folder, "manifest.json");
            if (!File.Exists(path)) return null;
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            return serializer.Deserialize<ScanManifest>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private sealed class IncompleteFile
    {
        public string UpdatedAt { get; set; } = "";
        public List<IncompleteItem> Items { get; set; } = new();
    }

    private sealed class ScanManifest
    {
        public string WorkId { get; set; } = "";
        public string Title { get; set; } = "";
        public List<string> Authors { get; set; } = new();
        public int FavoritePage { get; set; }
        public int FavoritePosition { get; set; }
        public string WorkUrl { get; set; } = "";
        public string ReaderUrl { get; set; } = "";
        public int ImageCount { get; set; }
    }
}
