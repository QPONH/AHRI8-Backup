using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AHRI8Backup;

/// <summary>
/// 图片地址的备用规则。
///
/// 站点的图片地址里带宽度档位（如 <c>_w1100</c>）；少数作品没有该档位、或者多了一层章节目录
/// （例如 <c>/d-41972/01/xxx_w1100.jpg</c>），直接按脚本拼出来的地址会 404。
/// 这里在主地址 404 时生成一组候选：换宽度、去掉宽度取原图、去掉 <c>/01/</c> 这类两位数字目录，
/// 由下载逻辑逐个尝试，成功的规则会被记住，后续页优先使用。
/// </summary>
internal sealed class ImageUrlFallback
{
    private static readonly Regex WidthPattern = new(@"_w\d+(?=\.[A-Za-z0-9]+$|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StepPattern = new(@"/(\d{2})(?=/)", RegexOptions.Compiled);
    private static readonly string[] Widths = { "_w1600", "_w1400", "_w1200", "_w1000", "_w800", "_w600", "_w400" };

    private readonly ConcurrentDictionary<string, int> hits = new();

    public IReadOnlyList<(string Url, string Rule)> Candidates(string originalUrl)
    {
        var candidates = BuildCandidates(originalUrl);
        var best = hits.OrderByDescending(pair => pair.Value).FirstOrDefault();
        if (best.Value >= 2)
        {
            int index = candidates.FindIndex(c => c.Rule == best.Key);
            if (index > 0)
            {
                var item = candidates[index];
                candidates.RemoveAt(index);
                candidates.Insert(0, item);
            }
        }
        return candidates;
    }

    public void RecordSuccess(string rule)
    {
        if (string.IsNullOrEmpty(rule) || rule == "original") return;
        hits.AddOrUpdate(rule, 1, (_, count) => count + 1);
    }

    /// <summary>公开给测试使用：返回主地址 + 所有备用地址（不含命中排序）。</summary>
    public static List<(string Url, string Rule)> BuildCandidates(string url)
    {
        var result = new List<(string Url, string Rule)>();
        void Add(string candidate, string rule)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return;
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out _)) return;
            if (result.Any(x => string.Equals(x.Url, candidate, StringComparison.OrdinalIgnoreCase))) return;
            result.Add((candidate, rule));
        }

        Add(url, "original");

        var width = WidthPattern.Match(url);
        if (width.Success)
        {
            foreach (var size in Widths)
                Add(url.Substring(0, width.Index) + size + url.Substring(width.Index + width.Length), "width:" + size);

            // 去掉宽度后缀 = 原图
            Add(url.Substring(0, width.Index) + url.Substring(width.Index + width.Length), "strip-width");
        }

        Match? step = null;
        try { step = StepPattern.Match(new Uri(url).AbsolutePath); }
        catch { step = null; }
        if (step is { Success: true })
        {
            var stepBase = RemoveStepFolder(url);
            if (stepBase.Length > 0)
            {
                Add(stepBase, "strip-step");
                var stepWidth = WidthPattern.Match(stepBase);
                if (stepWidth.Success)
                {
                    foreach (var size in Widths)
                        Add(stepBase.Substring(0, stepWidth.Index) + size + stepBase.Substring(stepWidth.Index + stepWidth.Length), "strip-step+width:" + size);
                    Add(stepBase.Substring(0, stepWidth.Index) + stepBase.Substring(stepWidth.Index + stepWidth.Length), "strip-step+strip-width");
                }
            }
        }

        // 候选太多会让 404 作品逐页等待过久；保留主地址 + 最常用的几种。
        return result.Take(16).ToList();
    }

    private static string RemoveStepFolder(string url)
    {
        try
        {
            var uri = new Uri(url);
            var path = uri.AbsolutePath;
            var match = StepPattern.Match(path);
            if (!match.Success) return "";
            var newPath = path.Remove(match.Index, match.Length);
            var builder = new UriBuilder(uri) { Path = newPath };
            return builder.Uri.ToString();
        }
        catch
        {
            return "";
        }
    }
}
