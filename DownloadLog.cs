using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace AHRI8Backup;

/// <summary>一条下载日志。每张图片下载成功/失败都立即追加一行，进程被结束后下次仍能读到。</summary>
internal sealed class DownloadLogEntry
{
    public string Time { get; set; } = "";
    public int Page { get; set; }
    public string Url { get; set; } = "";
    public string File { get; set; } = "";
    public string Status { get; set; } = "";
    public long Bytes { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// 作品文件夹里的下载日志（<c>download.log.jsonl</c>，一行一条 JSON）。
///
/// 用途有三：
/// 1. 断点续传：下次下载前先扫描这份日志，已是 <c>ok</c> 的页直接跳过，不再重复请求；
/// 2. 诊断：失败页会留下状态与错误摘要，能看出是网络问题还是站点改版；
/// 3. 与 manifest.json 互补：manifest 是汇总（最后写入），日志是逐张追加，随时中断都不丢进度。
///
/// 注意：日志只是“线索”，不能代替实际文件检查。文件被删掉或损坏时由
/// <see cref="FolderIntegrity"/> 重新发现并要求下载，见 MainForm 的下载流程。
/// </summary>
internal sealed class DownloadLog
{
    public const string FileName = "download.log.jsonl";

    private readonly string path;
    private readonly object gate = new();
    private readonly Dictionary<int, DownloadLogEntry> last = new();

    public DownloadLog(string folder)
    {
        path = System.IO.Path.Combine(folder, FileName);
        Load();
    }

    public string Path => path;
    public IReadOnlyDictionary<int, DownloadLogEntry> Entries => last;
    public int LoadedCount => last.Count;
    public int OkCount
    {
        get
        {
            int count = 0;
            foreach (var entry in last.Values) if (entry.Status == "ok") count++;
            return count;
        }
    }

    public DownloadLogEntry? LastOf(int page) => last.TryGetValue(page, out var entry) ? entry : null;

    public bool IsOk(int page) => last.TryGetValue(page, out var entry) && entry.Status == "ok";

    private void Load()
    {
        if (!File.Exists(path)) return;
        try
        {
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<DownloadLogEntry>(line);
                    if (entry is null || entry.Page <= 0) continue;
                    last[entry.Page] = entry;       // 同一页后写覆盖先写：失败之后又成功时以成功为准
                }
                catch
                {
                    // 断电等情况可能留下半行；跳过坏行，不影响其它记录。
                }
            }
        }
        catch
        {
            // 日志不可读时按“没有日志”处理，仍然会走实际文件检查。
        }
    }

    /// <summary>追加一条记录并立即落盘。写日志失败绝不影响下载本身。</summary>
    public void Record(int page, string url, string file, string status, long bytes, string? error = null)
    {
        var entry = new DownloadLogEntry
        {
            Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Page = page,
            Url = url ?? "",
            File = file ?? "",
            Status = status,
            Bytes = bytes,
            Error = string.IsNullOrWhiteSpace(error) ? null : error,
        };
        var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(entry);
        lock (gate)
        {
            last[page] = entry;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.WriteLine(json);
                writer.Flush();
                stream.Flush(true);              // 不依赖缓冲区，进程被结束后也能读到最后一条
            }
            catch
            {
                // 只读目录/磁盘满时忽略：界面日志里仍有记录。
            }
        }
    }

    public void MarkOk(int page, string url, string file, long bytes) => Record(page, url, file, "ok", bytes);

    public void MarkFailed(int page, string url, string file, string error) => Record(page, url, file, "failed", 0, error);
}
