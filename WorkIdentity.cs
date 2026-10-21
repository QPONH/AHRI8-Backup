using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace AHRI8Backup;

/// <summary>
/// 作品文件夹里的唯一识别文件（<c>work-id.json</c>）。
///
/// 文件夹名里的标题/作者可能因为站点加密文本变化、命名方式切换而改变，
/// 但站点作品编号（post.php?ID=…）是稳定的。识别文件把编号与下载当时的
/// 收藏夹位置一起保存下来，之后就能：
/// 1. 重新扫描收藏夹后，按编号找到本地文件夹并更新位置；
/// 2. 按更新后的位置重命名文件夹末尾的 <c>{页,序}</c> / <c>{倒序号}</c>。
/// </summary>
internal sealed class WorkIdentity
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>漫画编号，即详情页 post.php?ID= 的数字编号，跨次扫描稳定。</summary>
    public string WorkId { get; set; } = "";
    public string Title { get; set; } = "";
    public List<string> Authors { get; set; } = new();

    /// <summary>收藏夹页码（从 1 开始）。</summary>
    public int FavoritePage { get; set; }
    /// <summary>该页内第几部（从 1 开始）。</summary>
    public int FavoritePosition { get; set; }
    /// <summary>收藏夹全局序号（页序 → 页内序，从 1 开始），倒序编号用。</summary>
    public int GlobalIndex { get; set; }
    /// <summary>最近一次扫描到的作品总数，倒序编号补零位数用。</summary>
    public int TotalWorks { get; set; }

    public string WorkUrl { get; set; } = "";
    public string ReaderUrl { get; set; } = "";
    public int ImageCount { get; set; }
    public string FolderName { get; set; } = "";
    public string SiteHost { get; set; } = "";
    /// <summary>最近一次对照时是否仍在收藏夹里。</summary>
    public bool InFavorites { get; set; } = true;

    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    /// <summary>位置最后一次被扫描结果更新的时间。</summary>
    public string PositionUpdatedAt { get; set; } = "";
    /// <summary>最后一次在收藏夹列表中看到该编号的时间。</summary>
    public string LastSeenAt { get; set; } = "";
    public string FolderRenamedAt { get; set; } = "";
    public List<string> PreviousFolderNames { get; set; } = new();

    [ScriptIgnore]
    public bool HasPosition => FavoritePage > 0 && FavoritePosition > 0;

    [ScriptIgnore]
    public string PositionText => HasPosition ? $"{FavoritePage},{FavoritePosition}" : "未记录";
}

/// <summary>识别文件的原子读写。文件损坏/缺失都按“没有识别文件”处理，不影响下载。</summary>
internal static class WorkIdentityStore
{
    public const string FileName = "work-id.json";

    private static readonly JavaScriptSerializer Serializer = new() { MaxJsonLength = int.MaxValue };
    private static readonly object SerializerGate = new();
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static string PathFor(string folder) => Path.Combine(folder, FileName);

    public static WorkIdentity? TryRead(string folder)
    {
        var path = PathFor(folder);
        if (!File.Exists(path)) return null;
        try
        {
            var identity = Deserialize<WorkIdentity>(File.ReadAllText(path));
            if (identity is null) return null;
            identity.Authors ??= new List<string>();
            identity.PreviousFolderNames ??= new List<string>();
            if (string.IsNullOrWhiteSpace(identity.CreatedAt)) identity.CreatedAt = identity.UpdatedAt;
            return identity;
        }
        catch
        {
            // 文件损坏、被占用或不是本项目写的 JSON：当作没有识别文件。
            return null;
        }
    }

    /// <summary>写入识别文件（先写临时文件再替换），成功返回 true。</summary>
    public static bool Write(string folder, WorkIdentity identity)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            if (string.IsNullOrWhiteSpace(identity.CreatedAt)) identity.CreatedAt = now;
            identity.UpdatedAt = now;
            identity.SchemaVersion = Math.Max(1, identity.SchemaVersion);
            identity.FolderName = Path.GetFileName(folder);

            var path = PathFor(folder);
            var temp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            File.WriteAllText(temp, JsonFormat.Pretty(Serialize(identity)), Utf8NoBom);
            try
            {
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 下载一部作品时更新识别文件。
    /// <paramref name="positionIsCurrent"/> 表示位置来自本次收藏夹扫描；
    /// 否则（例如从「检查所有文件」单独下载）只补空字段，避免用旧的 manifest 位置覆盖已经同步过的位置。
    /// </summary>
    public static WorkIdentity? WriteDownload(
        string folder,
        string workId,
        string title,
        IReadOnlyList<string> authors,
        int favoritePage,
        int favoritePosition,
        int globalIndex,
        int totalWorks,
        string workUrl,
        string readerUrl,
        int imageCount,
        bool positionIsCurrent,
        out bool created)
    {
        created = false;
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var identity = TryRead(folder);
        if (identity is null)
        {
            identity = new WorkIdentity { CreatedAt = now };
            created = true;
        }

        identity.WorkId = string.IsNullOrWhiteSpace(workId) ? identity.WorkId : workId;
        if (string.IsNullOrWhiteSpace(identity.WorkId)) return null;

        if (positionIsCurrent || !identity.HasPosition)
        {
            if (favoritePage > 0) identity.FavoritePage = favoritePage;
            if (favoritePosition > 0) identity.FavoritePosition = favoritePosition;
        }
        if (positionIsCurrent || identity.GlobalIndex <= 0)
        {
            if (globalIndex > 0) identity.GlobalIndex = globalIndex;
            if (totalWorks > 0) identity.TotalWorks = totalWorks;
        }
        else if (identity.TotalWorks <= 0 && totalWorks > 0)
        {
            identity.TotalWorks = totalWorks;
        }

        if (positionIsCurrent || string.IsNullOrWhiteSpace(identity.Title))
        {
            if (!string.IsNullOrWhiteSpace(title)) identity.Title = title;
            if (authors.Count > 0) identity.Authors = authors.Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
        }
        if (string.IsNullOrWhiteSpace(identity.WorkUrl)) identity.WorkUrl = workUrl ?? "";
        if (string.IsNullOrWhiteSpace(identity.ReaderUrl)) identity.ReaderUrl = readerUrl ?? "";
        if (string.IsNullOrWhiteSpace(identity.SiteHost)) identity.SiteHost = HostOf(identity.WorkUrl);
        if (imageCount > 0) identity.ImageCount = imageCount;

        if (positionIsCurrent)
        {
            identity.LastSeenAt = now;
            identity.PositionUpdatedAt = now;
            identity.InFavorites = true;
        }

        return Write(folder, identity) ? identity : null;
    }

    private static string HostOf(string url)
    {
        try { return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : ""; }
        catch { return ""; }
    }

    private static string Serialize(object value)
    {
        lock (SerializerGate) return Serializer.Serialize(value);
    }

    private static T? Deserialize<T>(string text) where T : class
    {
        lock (SerializerGate) return Serializer.Deserialize<T>(text);
    }
}

/// <summary>主界面扫描结果交给识别文件维护逻辑的轻量快照（不引用 MainForm 的私有 Work 类型）。</summary>
internal sealed class IdentityWorkInfo
{
    public string WorkId { get; set; } = "";
    public string Title { get; set; } = "";
    public List<string> Authors { get; set; } = new();
    public int FavoritePage { get; set; }
    public int FavoritePosition { get; set; }
    public int GlobalIndex { get; set; }
    public int TotalWorks { get; set; }
    public string WorkUrl { get; set; } = "";
    public string SiteHost { get; set; } = "";
}

internal sealed class IdentityCreateResult
{
    public int Created { get; set; }
    public int Existing { get; set; }
    public int Skipped { get; set; }
}

internal sealed class IdentityUpdateResult
{
    public int Created { get; set; }
    public int Matched { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }
    public int NotInFavorites { get; set; }
}

internal sealed class IdentityRenameResult
{
    public int Renamed { get; set; }
    public int Unchanged { get; set; }
    public int Skipped { get; set; }
}

/// <summary>
/// 识别文件的三个维护动作：
/// 1. <see cref="CreateMissing"/>：给已有作品文件夹补写 work-id.json（优先读 manifest.json，必要时用当前扫描列表按目录后缀匹配）；
/// 2. <see cref="UpdatePositions"/>：按漫画编号对照当前收藏夹扫描结果，更新识别文件里的页/页内序号/全局序号；
/// 3. <see cref="RenameFolders"/>：按更新后的识别文件，只替换文件夹末尾的 <c>{页,序}</c> 或 <c>{倒序号}</c>，保留标题与作者前缀。
/// </summary>
internal static class WorkIdentityService
{
    private static readonly Regex SuffixPattern = new(@"^(?<prefix>.*)\{(?<a>\d+)(?:,(?<b>\d+))?\}\s*$", RegexOptions.Compiled);
    private static readonly Regex ReplaceSuffixPattern = new(@"\{\d+(?:,\d+)?\}\s*$", RegexOptions.Compiled);

    // ---------------- 1) 补写识别文件 ----------------
    public static IdentityCreateResult CreateMissing(string outputRoot, IReadOnlyList<IdentityWorkInfo>? currentWorks, Action<string> log)
    {
        var result = new IdentityCreateResult();
        if (!Directory.Exists(outputRoot))
        {
            log("输出目录不存在：" + outputRoot);
            return result;
        }
        var works = currentWorks ?? Array.Empty<IdentityWorkInfo>();
        var logged = 0;

        foreach (var folder in SafeDirectories(outputRoot))
        {
            var name = Path.GetFileName(folder);
            if (name.StartsWith("_", StringComparison.Ordinal)) continue;

            try
            {
                if (WorkIdentityStore.TryRead(folder) is not null)
                {
                    result.Existing++;
                    continue;
                }

                var manifest = ReadManifest(folder);
                var matched = MatchWork(manifest, name, works);
                var workId = manifest?.WorkId ?? "";
                if (string.IsNullOrWhiteSpace(workId) && matched is not null) workId = matched.WorkId;
                if (string.IsNullOrWhiteSpace(workId))
                {
                    result.Skipped++;
                    if (logged < 30)
                    {
                        log($"跳过 {name}：没有 manifest.json 作品编号，且无法从目录名对照当前收藏夹列表。");
                        logged++;
                    }
                    continue;
                }

                var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                int page = matched?.FavoritePage ?? manifest?.FavoritePage ?? 0;
                int position = matched?.FavoritePosition ?? manifest?.FavoritePosition ?? 0;
                var identity = new WorkIdentity
                {
                    WorkId = workId,
                    Title = FirstNonEmpty(matched?.Title, manifest?.Title),
                    Authors = matched?.Authors ?? manifest?.Authors ?? new List<string>(),
                    FavoritePage = page,
                    FavoritePosition = position,
                    GlobalIndex = matched?.GlobalIndex ?? ((page > 0 && position > 0) ? (page - 1) * 20 + position : 0),
                    TotalWorks = matched?.TotalWorks ?? 0,
                    WorkUrl = FirstNonEmpty(matched?.WorkUrl, manifest?.WorkUrl),
                    ReaderUrl = manifest?.ReaderUrl ?? "",
                    ImageCount = manifest?.ImageCount ?? 0,
                    SiteHost = FirstNonEmpty(matched?.SiteHost, HostOf(FirstNonEmpty(matched?.WorkUrl, manifest?.WorkUrl))),
                    InFavorites = true,
                    CreatedAt = now,
                    PositionUpdatedAt = matched is null ? "" : now,
                    LastSeenAt = matched is null ? "" : now,
                };
                if (WorkIdentityStore.Write(folder, identity))
                {
                    result.Created++;
                    if (logged < 40)
                    {
                        log($"补写识别文件：{name} → 漫画编号 {identity.WorkId}，位置 {{{identity.PositionText}}}。");
                        logged++;
                    }
                }
                else
                {
                    result.Skipped++;
                    log($"写入识别文件失败：{name}");
                }
            }
            catch (Exception ex)
            {
                result.Skipped++;
                log($"处理 {name} 的识别文件失败：{ex.Message}");
            }
        }

        log($"识别文件补写结果：新建 {result.Created} 个，已存在 {result.Existing} 个，跳过 {result.Skipped} 个。");
        return result;
    }

    // ---------------- 2) 对照编号更新位置 ----------------
    public static IdentityUpdateResult UpdatePositions(string outputRoot, IReadOnlyList<IdentityWorkInfo> currentWorks, Action<string> log)
    {
        var result = new IdentityUpdateResult();
        if (!Directory.Exists(outputRoot))
        {
            log("输出目录不存在：" + outputRoot);
            return result;
        }
        if (currentWorks.Count == 0)
        {
            log("当前收藏夹列表为空：请先扫描收藏夹，再对照更新位置。");
            return result;
        }

        // 旧作品可能还没有识别文件：先自动补写，再统一对照。
        result.Created = CreateMissing(outputRoot, currentWorks, log).Created;

        var byId = new Dictionary<string, IdentityWorkInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var work in currentWorks)
        {
            if (string.IsNullOrWhiteSpace(work.WorkId)) continue;
            if (!byId.ContainsKey(work.WorkId)) byId[work.WorkId] = work;   // 同一编号重复出现时取第一次
        }
        var logged = 0;
        var missingLogged = 0;

        foreach (var folder in SafeDirectories(outputRoot))
        {
            if (Path.GetFileName(folder).StartsWith("_", StringComparison.Ordinal)) continue;
            try
            {
                var identity = WorkIdentityStore.TryRead(folder);
                if (identity is null || string.IsNullOrWhiteSpace(identity.WorkId)) continue;

                if (byId.TryGetValue(identity.WorkId, out var work))
                {
                    int oldPage = identity.FavoritePage, oldPosition = identity.FavoritePosition;
                    int oldIndex = identity.GlobalIndex;
                    // “位置变化”只看页/页内序号/全局序号；作品总数变化只影响倒序补零位数，不算位置移动。
                    bool positionChanged = oldPage != work.FavoritePage || oldPosition != work.FavoritePosition
                                           || oldIndex != work.GlobalIndex;
                    bool wasMissing = !identity.InFavorites;

                    identity.FavoritePage = work.FavoritePage;
                    identity.FavoritePosition = work.FavoritePosition;
                    identity.GlobalIndex = work.GlobalIndex;
                    identity.TotalWorks = work.TotalWorks;
                    if (string.IsNullOrWhiteSpace(identity.Title)) identity.Title = work.Title;
                    if (identity.Authors.Count == 0) identity.Authors = work.Authors.ToList();
                    if (string.IsNullOrWhiteSpace(identity.WorkUrl)) identity.WorkUrl = work.WorkUrl;
                    if (string.IsNullOrWhiteSpace(identity.SiteHost)) identity.SiteHost = work.SiteHost;
                    var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    identity.InFavorites = true;
                    identity.LastSeenAt = now;
                    identity.PositionUpdatedAt = now;
                    WorkIdentityStore.Write(folder, identity);
                    if (oldPage != identity.FavoritePage || oldPosition != identity.FavoritePosition)
                        UpdateManifestPosition(folder, identity.FavoritePage, identity.FavoritePosition);

                    result.Matched++;
                    if (positionChanged)
                    {
                        result.Updated++;
                        if (logged < 60)
                        {
                            log($"更新位置：{Path.GetFileName(folder)}（漫画编号 {identity.WorkId}）{oldPage},{oldPosition} → {identity.FavoritePage},{identity.FavoritePosition}。");
                            logged++;
                        }
                    }
                    else
                    {
                        result.Unchanged++;
                        if (wasMissing && missingLogged < 20)
                        {
                            log($"重新在收藏夹中找到：{Path.GetFileName(folder)}（漫画编号 {identity.WorkId}）。");
                            missingLogged++;
                        }
                    }
                }
                else
                {
                    result.NotInFavorites++;
                    if (identity.InFavorites)
                    {
                        identity.InFavorites = false;
                        identity.LastSeenAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        WorkIdentityStore.Write(folder, identity);
                        if (missingLogged < 30)
                        {
                            log($"不在当前收藏夹：{Path.GetFileName(folder)}（漫画编号 {identity.WorkId}），位置保留不变。");
                            missingLogged++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log($"对照 {Path.GetFileName(folder)} 失败：{ex.Message}");
            }
        }

        log($"位置对照完成：匹配 {result.Matched} 部，其中位置变化 {result.Updated} 部、未变化 {result.Unchanged} 部；不在收藏夹 {result.NotInFavorites} 部。");
        log("提示：下一步点「更新文件夹序号」，才会真正修改本地文件夹名。");
        return result;
    }

    // ---------------- 3) 按识别文件重命名文件夹序号 ----------------
    public static IdentityRenameResult RenameFolders(string outputRoot, bool reverseNumber, Action<string> log)
    {
        var result = new IdentityRenameResult();
        if (!Directory.Exists(outputRoot))
        {
            log("输出目录不存在：" + outputRoot);
            return result;
        }

        var allFolders = SafeDirectories(outputRoot).ToList();
        var allNames = new HashSet<string>(allFolders.Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
        var plans = new List<RenamePlan>();
        var logged = 0;

        foreach (var folder in allFolders)
        {
            var oldName = Path.GetFileName(folder);
            if (oldName.StartsWith("_", StringComparison.Ordinal)) continue;
            try
            {
                var identity = WorkIdentityStore.TryRead(folder);
                if (identity is null) continue;
                if (!identity.InFavorites)
                {
                    result.Skipped++;
                    continue;
                }

                string suffix;
                if (reverseNumber)
                {
                    if (identity.TotalWorks <= 0 || identity.GlobalIndex <= 0)
                    {
                        result.Skipped++;
                        if (logged < 20)
                        {
                            log($"跳过 {oldName}：识别文件里没有可用的全局序号/总数，请先点「对照更新位置」。");
                            logged++;
                        }
                        continue;
                    }
                    int reverse = identity.TotalWorks - identity.GlobalIndex + 1;
                    if (reverse <= 0)
                    {
                        result.Skipped++;
                        continue;
                    }
                    suffix = "{" + reverse.ToString().PadLeft(identity.TotalWorks.ToString().Length, '0') + "}";
                }
                else
                {
                    if (!identity.HasPosition)
                    {
                        result.Skipped++;
                        if (logged < 20)
                        {
                            log($"跳过 {oldName}：识别文件里没有收藏夹位置，请先点「对照更新位置」。");
                            logged++;
                        }
                        continue;
                    }
                    suffix = "{" + identity.FavoritePage + "," + identity.FavoritePosition + "}";
                }

                var newName = ReplaceSuffix(oldName, suffix);
                if (string.Equals(newName, oldName, StringComparison.Ordinal))
                {
                    result.Unchanged++;
                    continue;
                }
                if (!IsValidFolderName(newName))
                {
                    result.Skipped++;
                    log($"跳过 {oldName}：新文件夹名不合法（{newName}）。");
                    continue;
                }
                plans.Add(new RenamePlan { OldPath = folder, OldName = oldName, NewName = newName, Identity = identity });
            }
            catch (Exception ex)
            {
                result.Skipped++;
                log($"计算 {oldName} 的新序号失败：{ex.Message}");
            }
        }

        // 同名目标只保留第一个（Windows 不区分大小写）；已经被非移动文件夹占用的目标也跳过。
        var accepted = new List<RenamePlan>();
        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plan in plans)
        {
            if (!desired.Add(plan.NewName))
            {
                result.Skipped++;
                log($"跳过 {plan.OldName} → {plan.NewName}：多个作品算出同一个目标目录名。");
                continue;
            }
            accepted.Add(plan);
        }
        while (true)
        {
            var selectedOldNames = new HashSet<string>(accepted.Select(x => x.OldName), StringComparer.OrdinalIgnoreCase);
            var conflicts = accepted
                .Where(plan => allNames.Contains(plan.NewName) && !selectedOldNames.Contains(plan.NewName))
                .ToList();
            if (conflicts.Count == 0) break;
            foreach (var conflict in conflicts)
            {
                accepted.Remove(conflict);
                result.Skipped++;
                log($"跳过 {conflict.OldName} → {conflict.NewName}：目标文件夹已被占用。");
            }
        }

        if (accepted.Count == 0)
        {
            log($"文件夹序号更新结束：没有需要重命名的文件夹（未变化 {result.Unchanged} 个，跳过 {result.Skipped} 个）。");
            return result;
        }

        // 两阶段重命名：先全部移到临时名，再改成目标名，避免 1→2、2→3 这类互换时撞名。
        foreach (var plan in accepted)
        {
            plan.TempPath = Path.Combine(outputRoot, "~ahri8-renaming-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            try
            {
                Directory.Move(plan.OldPath, plan.TempPath);
                plan.MovedToTemp = true;
            }
            catch (Exception ex)
            {
                Rollback(accepted, outputRoot, log);
                log($"重命名中止：{plan.OldName} 无法移动（{ex.Message}），已回滚。");
                return result;
            }
        }

        foreach (var plan in accepted)
        {
            try
            {
                var target = Path.Combine(outputRoot, plan.NewName);
                Directory.Move(plan.TempPath, target);
                plan.Finalized = true;
                plan.MovedToTemp = false;
            }
            catch (Exception ex)
            {
                Rollback(accepted, outputRoot, log);
                log($"重命名中止：{plan.OldName} → {plan.NewName} 失败（{ex.Message}），已尝试回滚。");
                return result;
            }
        }

        var relocated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plan in accepted)
        {
            var newPath = Path.Combine(outputRoot, plan.NewName);
            var oldPath = plan.OldPath;
            relocated[oldPath] = newPath;

            plan.Identity.FolderName = plan.NewName;
            plan.Identity.FolderRenamedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            plan.Identity.PreviousFolderNames.Add(plan.OldName);
            if (plan.Identity.PreviousFolderNames.Count > 20)
                plan.Identity.PreviousFolderNames = plan.Identity.PreviousFolderNames.Skip(plan.Identity.PreviousFolderNames.Count - 20).ToList();
            if (!WorkIdentityStore.Write(newPath, plan.Identity))
                log($"文件夹已重命名，但识别文件写入失败：{plan.NewName}");

            result.Renamed++;
            if (logged < 100) log($"重命名：{plan.OldName} → {plan.NewName}（漫画编号 {plan.Identity.WorkId}）。");
            else if (logged == 100) log("……更多重命名记录见上方汇总。");
            logged++;
        }

        IncompleteScanner.Relocate(outputRoot, relocated);
        log($"文件夹序号更新完成：重命名 {result.Renamed} 个，未变化 {result.Unchanged} 个，跳过 {result.Skipped} 个。");
        return result;
    }

    // ---------------- 内部工具 ----------------
    private sealed class RenamePlan
    {
        public string OldPath { get; set; } = "";
        public string OldName { get; set; } = "";
        public string NewName { get; set; } = "";
        public string TempPath { get; set; } = "";
        public bool MovedToTemp { get; set; }
        public bool Finalized { get; set; }
        public WorkIdentity Identity { get; set; } = new();
    }

    /// <summary>回滚：先把已经改好的目标名移回临时名，再把所有临时名移回原名。</summary>
    private static void Rollback(IReadOnlyList<RenamePlan> plans, string outputRoot, Action<string> log)
    {
        foreach (var plan in plans.Where(p => p.Finalized).Reverse())
        {
            try
            {
                Directory.Move(Path.Combine(outputRoot, plan.NewName), plan.TempPath);
                plan.Finalized = false;
                plan.MovedToTemp = true;
            }
            catch (Exception ex) { log($"回滚失败（{plan.NewName}）：{ex.Message}"); }
        }
        foreach (var plan in plans.Where(p => p.MovedToTemp).Reverse())
        {
            try
            {
                Directory.Move(plan.TempPath, plan.OldPath);
                plan.MovedToTemp = false;
            }
            catch (Exception ex) { log($"回滚失败（{plan.OldName}）：{ex.Message}"); }
        }
    }

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try { return Directory.EnumerateDirectories(root).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    private static IdentityWorkInfo? MatchWork(WorkManifestSnapshot? manifest, string folderName, IReadOnlyList<IdentityWorkInfo> works)
    {
        if (works.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(manifest?.WorkId))
        {
            var exact = works.FirstOrDefault(w => string.Equals(w.WorkId, manifest!.WorkId, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }

        var match = SuffixPattern.Match(folderName);
        if (!match.Success) return null;
        var prefix = match.Groups["prefix"].Value;
        if (match.Groups["b"].Success)
        {
            if (!int.TryParse(match.Groups["a"].Value, out var page) || !int.TryParse(match.Groups["b"].Value, out var position)) return null;
            var candidates = works.Where(w => w.FavoritePage == page && w.FavoritePosition == position).ToList();
            if (candidates.Count == 1) return candidates[0];
            var titleMatches = candidates.Where(w => PrefixMatches(prefix, w)).ToList();
            return titleMatches.Count == 1 ? titleMatches[0] : null;
        }
        if (int.TryParse(match.Groups["a"].Value, out var number))
        {
            int total = works.Count;
            int index = total - number + 1;
            if (index >= 1 && index <= total)
            {
                var candidate = works[index - 1];
                if (PrefixMatches(prefix, candidate) || works.Count == 1) return candidate;
                // 目录名里的倒序编号本身就是全局位置，标题不一致通常只是被截断/改名，仍按编号匹配。
                return candidate;
            }
        }
        return null;
    }

    private static bool PrefixMatches(string prefix, IdentityWorkInfo work)
    {
        var title = Clean(work.Title);
        if (title.Length >= 2 && prefix.IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        foreach (var author in work.Authors)
        {
            var value = Clean(author);
            if (value.Length >= 2 && prefix.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    private static string ReplaceSuffix(string folderName, string suffix)
    {
        if (ReplaceSuffixPattern.IsMatch(folderName))
            return ReplaceSuffixPattern.Replace(folderName, suffix);
        return folderName + suffix;
    }

    private static bool IsValidFolderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        if (name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(" ", StringComparison.Ordinal)) return false;
        return true;
    }

    private sealed class WorkManifestSnapshot
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

    private static WorkManifestSnapshot? ReadManifest(string folder)
    {
        try
        {
            var path = Path.Combine(folder, "manifest.json");
            if (!File.Exists(path)) return null;
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var manifest = serializer.Deserialize<WorkManifestSnapshot>(File.ReadAllText(path));
            if (manifest is not null) manifest.Authors ??= new List<string>();
            return manifest;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 只改 manifest.json 里的收藏夹位置字段；用字典方式读写以保留 Images 等未知字段。
    /// 失败不影响识别文件（识别文件才是位置同步的主数据）。
    /// </summary>
    private static void UpdateManifestPosition(string folder, int page, int position)
    {
        try
        {
            var path = Path.Combine(folder, "manifest.json");
            if (!File.Exists(path)) return;
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var data = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
            if (data is null) return;
            data["FavoritePage"] = page;
            data["FavoritePosition"] = position;
            data["PositionUpdatedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonFormat.Pretty(serializer.Serialize(data)), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }
        catch
        {
            // manifest 不可写时忽略；识别文件仍然保存了正确位置。
        }
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    private static string HostOf(string url)
    {
        try { return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : ""; }
        catch { return ""; }
    }

    private static string Clean(string value) => Regex.Replace(value ?? "", @"\s+", " ").Trim();
}
