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
        if (expected.Count > 0)
        {
            foreach (var page in expected)
            {
                var file = ImageIntegrity.FindPageFile(folder, page);
                if (file is null)
                {
                    report.MissingPages.Add(page);
                    continue;
                }
                recognized.Add(System.IO.Path.GetFileName(file));
                if (ImageIntegrity.Validate(file, out _, out _)) report.ValidPages.Add(page);
                else
                {
                    report.DamagedPages.Add(page);
                    report.DamagedFiles.Add(System.IO.Path.GetFileName(file));
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
                if (name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                {
                    var size = new FileInfo(path).Length;
                    report.StrayFiles.Add(size > 0 ? $"{name}（未完成的临时文件 {size} 字节，下次尝试续传）" : $"{name}（0 字节临时文件，会被清掉）");
                    continue;
                }
                if (recognized.Contains(name)) continue;
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
