using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AHRI8Backup;

/// <summary>一次文件夹检查的结果。页号从 1 开始，文件名是 0001.jpg / 0001.gif 这种四位数 + 扩展名。</summary>
internal sealed class FolderReport
{
    public int Expected { get; set; }
    public List<int> ValidPages { get; } = new();
    public List<int> DamagedPages { get; } = new();
    public List<string> DamagedFiles { get; } = new();
    public List<int> MissingPages { get; } = new();
    public List<string> StrayFiles { get; } = new();
    /// <summary>多余下载图片（页号超出应有张数、同一页重复扩展名的旧文件）；「清理多余图片」会删除它们。</summary>
    public List<string> ExtraImages { get; } = new();

    public bool Complete => Expected > 0 && ValidPages.Count == Expected;
    public int ProblemCount => DamagedPages.Count + MissingPages.Count;
}

/// <summary>
/// 绕过日志的“损坏文件夹检查”：每次都直接读磁盘上的文件，不信任 manifest.json 或下载日志。
///
/// 支持的格式由 <see cref="ImageIntegrity"/> 统一识别（JPEG / PNG / GIF / WebP）。
/// 判定“可用”的条件是：文件存在、大于 100 字节、开头魔数正确、结尾标记完整。
/// 这样，0 字节文件、被截断的图片、把错误页/HTML 存成图片的残留都会被识别出来，
/// 并在补全时重新下载，而不会因为日志写着 ok 就跳过。
/// </summary>
internal static class FolderIntegrity
{
    /// <summary>按预期页码检查文件夹，同时列出不在预期里的残留文件（.part、多余图片等）。</summary>
    public static FolderReport Check(string folder, IEnumerable<int> expectedPages)
    {
        var report = new FolderReport();
        var expected = expectedPages.Distinct().OrderBy(p => p).ToList();
        report.Expected = expected.Count;
        if (!Directory.Exists(folder)) return report;

        var recognized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extra = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (expected.Count > 0)
        {
            var expectedSet = new HashSet<int>(expected);
            var filesByPage = new Dictionary<int, List<string>>();
            // 先按页号归类所有受支持图片：页号超出应有张数的就是多余下载。
            foreach (var path in EnumerateSupportedImages(folder))
            {
                var name = System.IO.Path.GetFileName(path);
                var stem = System.IO.Path.GetFileNameWithoutExtension(path);
                if (!int.TryParse(stem, out var page) || page <= 0)
                {
                    // 不是 0001 这样的页号文件名：不自动删除，只在日志里提示。
                    report.StrayFiles.Add(name + "（文件名不是页号，未自动清理）");
                    continue;
                }
                if (!expectedSet.Contains(page))
                {
                    report.ExtraImages.Add(name);
                    extra.Add(name);
                    continue;
                }
                if (!filesByPage.TryGetValue(page, out var list)) filesByPage[page] = list = new List<string>();
                list.Add(path);
            }

            foreach (var page in expected)
            {
                if (!filesByPage.TryGetValue(page, out var candidates) || candidates.Count == 0)
                {
                    report.MissingPages.Add(page);
                    continue;
                }
                // 同一页有多个扩展名时：优先保留能通过校验的文件，其余都算重复下载的旧文件。
                candidates = candidates
                    .OrderBy(ExtensionPriority)
                    .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var valid = candidates.FirstOrDefault(p => ImageIntegrity.Validate(p, out _, out _));
                var keep = valid ?? candidates[0];
                recognized.Add(System.IO.Path.GetFileName(keep));
                if (valid is not null) report.ValidPages.Add(page);
                else
                {
                    report.DamagedPages.Add(page);
                    report.DamagedFiles.Add(System.IO.Path.GetFileName(keep));
                }
                foreach (var path in candidates)
                {
                    if (string.Equals(path, keep, StringComparison.OrdinalIgnoreCase)) continue;
                    var name = System.IO.Path.GetFileName(path);
                    report.ExtraImages.Add(name);
                    extra.Add(name);
                }
            }
        }
        else
        {
            // 没有 manifest / 日志时不知道应该有几张：把现有图片全部当候选，逐个验证完整性。
            foreach (var path in EnumerateSupportedImages(folder))
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(path);
                if (!int.TryParse(name, out var page) || page <= 0) continue;
                recognized.Add(System.IO.Path.GetFileName(path));
                report.Expected++;
                if (ImageIntegrity.Validate(path, out _, out _)) report.ValidPages.Add(page);
                else
                {
                    report.DamagedPages.Add(page);
                    report.DamagedFiles.Add(System.IO.Path.GetFileName(path));
                }
            }
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                var name = System.IO.Path.GetFileName(path);
                if (name.Equals(DownloadLog.FileName, StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals(WorkIdentityStore.FileName, StringComparison.OrdinalIgnoreCase)) continue;   // 作品识别文件，不是残留图片
                if (name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                {
                    var size = new FileInfo(path).Length;
                    report.StrayFiles.Add(size > 0 ? $"{name}（未完成的临时文件 {size} 字节，下次尝试续传）" : $"{name}（0 字节临时文件，会被清掉）");
                    continue;
                }
                if (recognized.Contains(name)) continue;
                if (extra.Contains(name)) continue;
                report.StrayFiles.Add(name);
            }
        }
        catch
        {
            // 目录读不了时上面的逐页检查已经能给出结论。
        }

        return report;
    }

    private static IEnumerable<string> EnumerateSupportedImages(string folder)
    {
        foreach (var path in Directory.EnumerateFiles(folder))
        {
            if (ImageIntegrity.IsSupportedExtension(System.IO.Path.GetExtension(path))) yield return path;
        }
    }

    /// <summary>同页多扩展名时保留哪个：按 .jpg / .jpeg / .png / .gif / .webp 的优先级。</summary>
    private static int ExtensionPriority(string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        for (int i = 0; i < ImageIntegrity.SupportedExtensions.Length; i++)
            if (string.Equals(ImageIntegrity.SupportedExtensions[i], extension, StringComparison.OrdinalIgnoreCase)) return i;
        return int.MaxValue;
    }

    /// <summary>删除多余下载图片（页号超出应有张数、同页重复扩展名的旧文件）。</summary>
    public static int DeleteExtraImages(string folder, FolderReport report)
    {
        int deleted = 0;
        foreach (var name in report.ExtraImages)
        {
            try
            {
                var path = System.IO.Path.Combine(folder, name);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    deleted++;
                }
            }
            catch
            {
                // 删除失败（被占用 / 只读）就保留文件，下次再清理。
            }
        }
        return deleted;
    }

    /// <summary>删除损坏的正式图片，保留 .part 供下次 Range 续传。</summary>
    public static int DeleteDamaged(string folder, FolderReport report)
    {
        int deleted = 0;
        foreach (var name in report.DamagedFiles)
        {
            try
            {
                File.Delete(System.IO.Path.Combine(folder, name));
                deleted++;
            }
            catch
            {
                // 删不掉就让下载逻辑在写入时覆盖。
            }
        }
        return deleted;
    }

    /// <summary>清掉 0 字节的 .part（无法续传，留着只会干扰判断）。</summary>
    public static int DeleteEmptyParts(string folder)
    {
        int deleted = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*.part"))
            {
                try
                {
                    if (new FileInfo(path).Length == 0) { File.Delete(path); deleted++; }
                }
                catch { }
            }
        }
        catch { }
        return deleted;
    }
}
