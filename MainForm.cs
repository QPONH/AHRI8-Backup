using HtmlAgilityPack;
using Microsoft.Win32;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Script.Serialization;

namespace AHRI8Backup;

/// <summary>
/// AHRI8 收藏备份工具 v1.4.0（单文件 / 免安装运行时版）
/// 直连并发下载收藏夹作品：登录 → 扫描收藏夹 → 解析阅读页 → 并发下载图片。
/// 站点把标题/作者用逐页随机密钥的 AES 加密，本程序在抓取端直接解密，因此目录名是真实标题。
/// 每部作品文件夹内写入 work-id.json（漫画编号 + 收藏夹位置），用于之后同步目录末尾序号。
/// </summary>
public sealed class MainForm : Form
{
    // ---- 明暗两套配色，由 ApplyTheme 切换 ----
    private Color colForm, colPanel, colInput, colText, colButton, colBorder, colSelect;
    private static readonly Font HintFont = new("Microsoft YaHei UI", 8f);   // 说明文字字体
    private readonly CheckBox darkMode = new() { Text = "深色界面", AutoSize = true, Checked = true, Margin = new Padding(14, 6, 0, 0) };

    private readonly TextBox baseUrl = new() { Text = "https://ahri8-2026-08-10-c.top/", Dock = DockStyle.Fill };
    private readonly TextBox favoritesUrl = new() { Text = "https://ahri8-2026-08-10-c.top/index.php?route=account/favorite", Dock = DockStyle.Fill };
    private readonly TextBox username = new() { Dock = DockStyle.Fill };
    private readonly TextBox password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly TextBox output = new() { Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "AHRI8Backup"), Dock = DockStyle.Fill };
    private readonly NumericUpDown workConcurrency = new() { Minimum = 1, Maximum = 6, Value = 3, Width = 60 };
    private readonly NumericUpDown imageConcurrency = new() { Minimum = 1, Maximum = 16, Value = 8, Width = 60 };
    private readonly Button loginButton = new() { Text = "登录 / 验证会话", AutoSize = true };
    private readonly Button scanButton = new() { Text = "扫描收藏夹", AutoSize = true };
    private readonly Button firstPageButton = new() { Text = "扫描并加入第一页作品", AutoSize = true };
    private readonly Button testButton = new() { Text = "测试下载选中作品", AutoSize = true };
    private readonly Button allButton = new() { Text = "并发下载全部", AutoSize = true };
    private readonly Button stopButton = new() { Text = "停止", AutoSize = true, Enabled = false };
    private readonly Button pauseButton = new() { Text = "暂停", AutoSize = true, Enabled = false };
    private readonly Button folderButton = new() { Text = "选择目录…", AutoSize = true };
    private readonly Button fixButton = new() { Text = "修复启动被拦", AutoSize = true };

    // ---- 命名方式 / 列表翻页 / 按范围下载 ----
    private readonly ComboBox namingMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 252, Margin = new Padding(0, 4, 0, 0) };
    private readonly Button prevPageButton = new() { Text = "◀ 上一页", AutoSize = true };
    private readonly Button nextPageButton = new() { Text = "下一页 ▶", AutoSize = true };
    private readonly Button jumpButton = new() { Text = "跳转", AutoSize = true };
    private readonly NumericUpDown jumpPage = new() { Minimum = 1, Value = 1, Width = 60 };
    private readonly Label pageInfo = new() { Text = "第 0/0 页", AutoSize = true, Margin = new Padding(10, 8, 10, 0) };
    private readonly Button downloadPageButton = new() { Text = "下载本页", AutoSize = true };
    private readonly Button downloadSelectedButton = new() { Text = "下载选中", AutoSize = true };
    private readonly Button downloadRangeButton = new() { Text = "下载该范围", AutoSize = true };
    private readonly Button checkButton = new() { Text = "检查文件夹完整性", AutoSize = true };
    private readonly Button checkAllButton = new() { Text = "检查所有文件", AutoSize = true };
    // ---- 作品识别文件：漫画编号 + 收藏夹位置 ----
    private readonly Button identityButton = new() { Text = "补写识别文件", AutoSize = true };
    private readonly Button updatePositionButton = new() { Text = "对照更新位置", AutoSize = true };
    private readonly Button renameFolderButton = new() { Text = "更新文件夹序号", AutoSize = true };
    private readonly NumericUpDown rangeFrom = new() { Minimum = 1, Value = 1, Width = 60 };
    private readonly NumericUpDown rangeTo = new() { Minimum = 1, Value = 1, Width = 60 };
    private const int PageSize = 20;          // 与站点收藏夹每页数量一致
    private int currentPage = 1;
    private int listHeaderHeight = 24;         // 表头实际高度（DrawColumnHeader 里更新），补画右侧空白用
    private DarkHeaderPaint? darkHeader;
    private readonly List<(Label Cell, ColumnHeader Column)> headerCells = new();
    private static readonly string BuildStamp = DateTime.Now.ToString("MMdd-HHmm");   // 便于确认跑的是哪一次构建

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

    /// <summary>
    /// 表头是独立子窗口，原生会把未被列占满的部分画成白色。
    /// 这里改写它的 WM_ERASEBKGND：先用主题面板色擦一遍，再由它自己画列头，空白处就是暗色了。
    /// </summary>
    private sealed class DarkHeaderPaint : NativeWindow
    {
        private readonly Func<Color> color;
        public DarkHeaderPaint(Func<Color> color) { this.color = color; }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0014 && GetClientRect(Handle, out var r))   // WM_ERASEBKGND
            {
                using var g = Graphics.FromHdc(m.WParam);
                using var brush = new SolidBrush(color());
                g.FillRectangle(brush, 0, 0, r.Right, r.Bottom);
                m.Result = (IntPtr)1;
                return;
            }
            base.WndProc(ref m);
        }
    }
    private int loadedNamingMode;             // 从设置读到的命名方式，等下拉框建好后再套用
    private enum NamingMode { PageIndex, ReverseNumber }

    // ---- 三级进度：单文件 / 单文件夹 / 总进度 ----
    private readonly ProgressBar fileBar = new() { Width = 130, Height = 18, Maximum = 100, Style = ProgressBarStyle.Continuous, Margin = new Padding(0, 5, 4, 0) };
    private readonly ProgressBar workBar = new() { Width = 130, Height = 18, Maximum = 100, Style = ProgressBarStyle.Continuous, Margin = new Padding(0, 5, 4, 0) };
    private readonly ProgressBar totalBar = new() { Width = 130, Height = 18, Maximum = 100, Style = ProgressBarStyle.Continuous, Margin = new Padding(0, 5, 4, 0) };
    private readonly Label fileText = new() { Text = "单文件 —", AutoSize = true, Font = HintFont, Margin = new Padding(0, 7, 16, 0) };
    private readonly Label workText = new() { Text = "单文件夹 —", AutoSize = true, Font = HintFont, Margin = new Padding(0, 7, 16, 0) };
    private readonly Label totalText = new() { Text = "总进度 —", AutoSize = true, Font = HintFont, Margin = new Padding(0, 7, 0, 0) };
    private readonly System.Windows.Forms.Timer progressTimer = new() { Interval = 150 };
    // 下载线程写入、UI 线程读取的进度快照（显示用，容忍偶尔读到不一致的值）
    private volatile string curFileName = "";
    private volatile int curFileDone, curFileTotal;
    private volatile int totalWorksDone, totalWorksAll;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Done, int Total)> activeWorks = new();

    // ---- 已存在文件夹的处理方式 ----
    private readonly ComboBox existingMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, Margin = new Padding(0, 4, 0, 0) };
    private readonly SemaphoreSlim promptLock = new(1, 1);
    private ExistingMode? batchExistingChoice;      // “本次剩下的都按这个来”选过之后不再逐个询问
    private int loadedExistingMode;
    private enum ExistingMode { Complete, Ignore, Ask }
    private readonly CheckBox rememberText = new() { Text = "记住文本", AutoSize = true };
    private readonly CheckBox rememberPassword = new() { Text = "记住密码", AutoSize = true };
    private readonly Label status = new() { Text = "就绪", AutoSize = true };
    private readonly ListView list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = false, MultiSelect = true };
    private readonly TextBox log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly CookieContainer cookies = new();

    // ---- 运行日志文件：输出目录下的 AHRI8Backup.log，方便回溯；写失败不影响下载 ----
    private readonly object logFileLock = new();
    private string sessionLogPath = "";
    private readonly HttpClient http;
    private SemaphoreSlim imageGate = new(8, 8);
    private readonly List<Work> works = new();
    private CancellationTokenSource? activeCts;
    private bool authenticated;

    // ---- 暂停 / 继续 ----
    // 用 ManualResetEventSlim 作为“闸门”：Set = 运行，Reset = 暂停。
    // 每部作品开始、每个 HTTP 请求前都会等待闸门，暂停时不再发出新请求，已发出的请求会自然完成。
    private readonly ManualResetEventSlim pauseGate = new(true);
    private bool sessionInvalid;                 // 暂停原因是否为“会话失效”，继续时先重新验证
    private Task<bool>? sessionRecoveryTask;     // 多个作品同时报会话失效时共用同一次重新登录
    private readonly object sessionRecoveryLock = new();

    // ---- 「检查所有文件」窗口（非模态，可边看边单独下载）----
    private IncompleteWindow? incompleteWindow;

    private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AHRI8Backup", "settings.json");

    public MainForm()
    {
        // 幂等：Program.Main 已经调用过；这里再保一次，保证任何启动方式下
        // 内嵌的 HtmlAgilityPack 都能被解析到。
        EmbeddedAssemblies.Install();
        LoadSettings();
        EnsureSettingsFile();
        Text = $"AHRI8 收藏备份工具 v1.4.0（构建 {BuildStamp}）";
        Icon = AppIcon.Load();
        // .NET Framework 4.8 的 HttpClient 默认用系统 TLS 策略；显式打开 TLS 1.2 更稳。
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        ApplyTheme(darkMode.Checked);      // 先定好配色，后面创建控件时才能直接用上正确颜色
        Width = 1250; Height = 980; MinimumSize = new Size(1000, 740);
        var handler = new HttpClientHandler { CookieContainer = cookies, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate, AllowAutoRedirect = true, UseCookies = true };
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/130.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Padding = new Padding(8) };
        // 设置区每行要放下「输入框 + 说明文字」，高度按当前字体推算，高 DPI 下自动放大。
        int inputH = Font.Height + 8;                 // 单行输入框（含边框）
        int hintH = HintFont.Height + 8;              // 说明行
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, (inputH + hintH + 6) * 5));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));   // 命名方式 + 列表翻页
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));   // 按范围 / 本页 / 选中下载
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));   // 识别文件：补写 / 对照 / 重命名
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));   // 三级进度条
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 44));
        Controls.Add(root);

        var settings = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 5 };
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        // 五行都要显式给样式：缺样式的行会被压成 0 高度，整行控件看不见。
        for (int i = 0; i < 5; i++) settings.RowStyles.Add(new RowStyle(SizeType.Percent, 20f));
        AddRow(settings, 0, "站点根地址", Hinted(baseUrl, "站点根地址。换镜像域名时只改这一项：收藏夹地址与作品详情页都由它推导"), "收藏夹地址", Hinted(favoritesUrl, "登录后点站点里的“我的收藏”，把地址栏完整网址复制到这里"));
        AddRow(settings, 1, "账号 / 邮箱", Hinted(WithToggle(username, rememberText), "站点登录账号或邮箱。只在本次运行用于登录，不会写进日志"), "密码", Hinted(WithToggle(password, rememberPassword), "站点登录密码。勾选右侧“记住密码”才按当前 Windows 用户加密保存在本机"));
        AddRow(settings, 2, "输出目录", Hinted(output, "每部作品一个子文件夹。目录不可写时程序会给出具体处理建议，不会静默失败"), "", new Label());
        // 说明文字要放在宽列里，否则会被 106px 的标签列横向截断。
        settings.Controls.Add(Label("并发作品数"), 0, 3);
        settings.Controls.Add(Hinted(workConcurrency, "同时处理几部作品（1–6）。每部作品是独立任务，调高更快，也更容易被站点限速", false), 1, 3);
        settings.Controls.Add(Label("图片并发数"), 2, 3);
        settings.Controls.Add(Hinted(imageConcurrency, "全部作品合计同时下载几张图（1–16）。图片经常失败时先调低这一项", false), 3, 3);
        settings.Controls.Add(Hinted(folderButton, "设置文件（首次运行自动创建）：%LocalAppData%\\AHRI8Backup\\settings.json", false), 1, 4);
        // 深色/浅色开关放在设置区，按钮行留出空间给“暂停”。
        var themeRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = new Padding(8, 2, 0, 0) };
        themeRow.Controls.Add(darkMode);
        themeRow.Controls.Add(new Label
        {
            Text = "深色 / 浅色，选择会被记住",
            AutoSize = true, Font = HintFont,
            ForeColor = Color.FromArgb(0x8A, 0x8A, 0x8A), Margin = new Padding(4, 6, 0, 0),
        });
        settings.Controls.Add(themeRow, 2, 4); settings.SetColumnSpan(themeRow, 2);
        root.Controls.Add(settings, 0, 0);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        buttons.Controls.Add(loginButton); buttons.Controls.Add(scanButton); buttons.Controls.Add(firstPageButton);
        buttons.Controls.Add(testButton); buttons.Controls.Add(allButton); buttons.Controls.Add(stopButton);
        buttons.Controls.Add(pauseButton); buttons.Controls.Add(fixButton); buttons.Controls.Add(status);
        darkMode.CheckedChanged += (_, _) => { ApplyTheme(darkMode.Checked); SaveSettings(); };
        fixButton.Click += (_, _) => FixLaunchBlock();
        pauseButton.Click += async (_, _) => await TogglePauseAsync();
        checkButton.Click += async (_, _) => await RunAsync(CheckFoldersAsync);
        checkAllButton.Click += (_, _) => OpenIncompleteWindow();
        identityButton.Click += async (_, _) => await RunAsync(CreateIdentityFilesAsync);
        updatePositionButton.Click += async (_, _) => await RunAsync(UpdateIdentityPositionsAsync);
        renameFolderButton.Click += async (_, _) => await RunAsync(RenameIdentityFoldersAsync);
        namingMode.SelectedIndexChanged += (_, _) => { SaveSettings(); ShowPage(currentPage); };
        existingMode.SelectedIndexChanged += (_, _) => SaveSettings();
        prevPageButton.Click += (_, _) => ShowPage(currentPage - 1);
        nextPageButton.Click += (_, _) => ShowPage(currentPage + 1);
        jumpButton.Click += (_, _) => ShowPage((int)jumpPage.Value);
        downloadPageButton.Click += async (_, _) => await RunAsync(ct => DownloadWorksAsync(CurrentPageWorks(), ct));
        downloadSelectedButton.Click += async (_, _) => await RunAsync(ct => DownloadWorksAsync(SelectedWorks(), ct));
        downloadRangeButton.Click += async (_, _) => await RunAsync(ct => DownloadWorksAsync(RangeWorks(), ct));
        root.Controls.Add(buttons, 0, 1);

        // 命名方式 + 列表翻页（每页 20 条，与站点收藏夹每页数量一致）
        var browse = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        namingMode.Items.AddRange(new object[] { "页-序号：标题{作者}{页,序}", "倒序编号：最后一本为 1" });
        namingMode.SelectedIndex = loadedNamingMode;
        browse.Controls.Add(new Label { Text = "命名方式", AutoSize = true, Margin = new Padding(0, 8, 4, 0) });
        browse.Controls.Add(namingMode);
        browse.Controls.Add(new Label { Text = "列表翻页", AutoSize = true, Margin = new Padding(18, 8, 4, 0) });
        browse.Controls.Add(prevPageButton);
        browse.Controls.Add(pageInfo);
        browse.Controls.Add(nextPageButton);
        browse.Controls.Add(new Label { Text = "跳到", AutoSize = true, Margin = new Padding(12, 8, 2, 0) });
        browse.Controls.Add(jumpPage);
        browse.Controls.Add(jumpButton);
        browse.Controls.Add(new Label { Text = "已存在文件夹", AutoSize = true, Margin = new Padding(18, 8, 4, 0) });
        existingMode.Items.AddRange(new object[] { "补全缺失的图片", "忽略整个作品", "逐个询问我" });
        existingMode.SelectedIndex = loadedExistingMode;
        browse.Controls.Add(existingMode);
        root.Controls.Add(browse, 0, 2);

        // 按本页 / 选中 / 序号范围下载
        var batch = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        batch.Controls.Add(downloadPageButton);
        batch.Controls.Add(downloadSelectedButton);
        batch.Controls.Add(new Label { Text = "序号范围", AutoSize = true, Margin = new Padding(18, 8, 4, 0) });
        batch.Controls.Add(rangeFrom);
        batch.Controls.Add(new Label { Text = "→", AutoSize = true, Margin = new Padding(4, 8, 4, 0) });
        batch.Controls.Add(rangeTo);
        batch.Controls.Add(downloadRangeButton);
        batch.Controls.Add(checkButton);
        batch.Controls.Add(checkAllButton);
        batch.Controls.Add(new Label
        {
            Text = "序号见首列；范围默认=当前页",
            AutoSize = true, Font = HintFont, ForeColor = Color.FromArgb(0x8A, 0x8A, 0x8A), Margin = new Padding(14, 10, 0, 0),
        });
        root.Controls.Add(batch, 0, 3);

        // 作品识别文件：补写 → 对照更新位置 → 更新文件夹序号
        var identityRow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        identityRow.Controls.Add(identityButton);
        identityRow.Controls.Add(updatePositionButton);
        identityRow.Controls.Add(renameFolderButton);
        identityRow.Controls.Add(new Label
        {
            Text = "识别文件 work-id.json = 漫画编号 + 收藏夹位置；先补写 → 对照更新位置 → 更新文件夹序号",
            AutoSize = true, Font = HintFont, ForeColor = Color.FromArgb(0x8A, 0x8A, 0x8A), Margin = new Padding(14, 10, 0, 0),
        });
        root.Controls.Add(identityRow, 0, 4);

        // 三级进度条
        var progressRow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        progressRow.Controls.Add(fileBar); progressRow.Controls.Add(fileText);
        progressRow.Controls.Add(workBar); progressRow.Controls.Add(workText);
        progressRow.Controls.Add(totalBar); progressRow.Controls.Add(totalText);
        root.Controls.Add(progressRow, 0, 5);
        progressTimer.Tick += (_, _) => RefreshProgress();

        list.Columns.Add("序号", 70); list.Columns.Add("作品标题", 220); list.Columns.Add("ID", 76); list.Columns.Add("状态", 122); list.Columns.Add("作品地址", 500);
        list.HeaderStyle = ColumnHeaderStyle.None;   // 原生表头会把未被列占满的部分涂白，干脆不用它
        StyleDarkList();
        root.Controls.Add(BuildListHost(), 0, 6);
        root.Controls.Add(log, 0, 7);

        loginButton.Click += async (_, _) => await RunAsync(LoginAsync);
        scanButton.Click += async (_, _) => await RunAsync(ScanFavoritesAsync);
        firstPageButton.Click += async (_, _) => await RunAsync(ScanFirstPageAsync);
        testButton.Click += async (_, _) => await RunAsync(async ct => { var w = SelectedWork(); if (w is null) { Log("请先选择一部作品。"); return; } await DownloadWorkAsync(w, ct); });
        allButton.Click += async (_, _) => await RunAsync(ct => DownloadWorksAsync(works.ToArray(), ct));
        stopButton.Click += (_, _) => activeCts?.Cancel();
        folderButton.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { SelectedPath = Directory.Exists(output.Text) ? output.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) };
            if (d.ShowDialog(this) == DialogResult.OK) output.Text = d.SelectedPath;
        };
        foreach (var box in new[] { baseUrl, favoritesUrl, username, password, output }) box.Validated += (_, _) => SaveSettings();
        rememberText.CheckedChanged += (_, _) => SaveSettings();
        rememberPassword.CheckedChanged += (_, _) => SaveSettings();
        FormClosing += (_, _) => { SaveSettings(); activeCts?.Cancel(); http.Dispose(); imageGate.Dispose(); };

        ApplyTheme(darkMode.Checked);
        Shown += (_, _) => { ThemeProgressBars(); ThemeComboBoxes(); RefreshProgress(); };   // 句柄建好后给进度条/下拉框上色
        Log("AHRI8Backup v1.4.0：单文件、免安装运行时（.NET Framework 4.8 为 Windows 10/11 自带）。");
        // 提前用一次 HTML 解析，确认内嵌的 HtmlAgilityPack 能从 exe 资源里加载；
        // 加载失败时这里就会给出明确日志，而不是等到点“扫描收藏夹”才报错。
        try { Parse("<html><body>ok</body></html>"); Log("内置组件自检：HTML 解析器可用（已从 exe 内嵌资源加载）。"); }
        catch (Exception ex) { Log("内置组件自检：HTML 解析器加载失败：" + FullException(ex)); }
        Log("按页面内嵌密钥解密标题与作者，目录名为 标题{作者}{收藏夹页,页内序号}。");
        Log("图片支持 JPEG / PNG / GIF / WebP；动图（GIF）会按原始扩展名保存，文件名以实际内容为准。");
        Log("下载中可点「暂停」/「继续」；遇到会话失效（401/403 或跳转登录页）会自动暂停并尝试重新登录后继续。");
        Log($"并发作品数 = 同时处理几部作品（当前 {workConcurrency.Value}）；图片并发数 = 全部作品合计同时下载几张图（当前 {imageConcurrency.Value}）。调高更快，但更容易被站点限速。");
        Log("每张图片下载成功后立即追加到该作品文件夹的 download.log.jsonl；下次下载会扫描这份日志继续上次进度。");
        Log("每次下载前还会直接检查已有文件是否损坏（不依赖日志），损坏的会重新下载。按钮「检查文件夹完整性」可手动检查。");
        Log("图片地址 404 时会自动尝试备用宽度/原图/无章节路径；「检查所有文件」可扫描输出目录并单独补下载。");
        Log("每部作品文件夹内会写入 work-id.json（漫画编号 + 收藏夹页/序号），用于收藏夹变动后同步目录末尾序号。");
        Log("收藏夹变动后的顺序：扫描收藏夹 → 「对照更新位置」→「更新文件夹序号」。");
        Log("如果每次启动都被 Windows 拦下、要手动点同意，点「修复启动被拦」按钮即可处理。");
    }

    // ---------------- 界面小工具 ----------------
    // 标签一律用 AutoSize + 右对齐锚点：既保持右对齐外观，又不会在自适应行里形成高度反馈循环。
    private static Label Label(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0, 6, 8, 0) };

    /// <summary>在输入框下方显示一行灰色说明。说明行高度按字体算，高 DPI 下自动变高，不会被输入框挡住。</summary>
    private static Control Hinted(Control input, string hint, bool stretch = true)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 1, 6, 1) };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, HintFont.Height + 8));
        if (stretch) input.Dock = DockStyle.Fill;                  // 数字框/按钮保持原宽度，不拉伸
        panel.Controls.Add(input, 0, 0);
        panel.Controls.Add(new Label
        {
            Text = hint,
            AutoSize = true,
            ForeColor = Color.FromArgb(0x8A, 0x8A, 0x8A),
            Font = HintFont,
            Margin = new Padding(0, 1, 0, 0),
        }, 0, 1);
        return panel;
    }

    private static void AddRow(TableLayoutPanel t, int row, string l1, Control c1, string l2, Control c2)
    {
        t.Controls.Add(Label(l1), 0, row); t.Controls.Add(c1, 1, row);
        t.Controls.Add(Label(l2), 2, row); t.Controls.Add(c2, 3, row);
    }

    private static Control WithToggle(Control input, CheckBox toggle)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        input.Dock = DockStyle.Fill;
        toggle.Dock = DockStyle.Fill;
        toggle.Margin = new Padding(6, 3, 0, 3);
        panel.Controls.Add(input, 0, 0);
        panel.Controls.Add(toggle, 1, 0);
        return panel;
    }

    /// <summary>
    /// 表头在启用视觉样式时无法直接改色，只能自绘；否则暗色界面顶端会留一条系统浅色带。
    /// 自绘后单元格也要自己画，顺便把网格线画成暗色。
    /// </summary>
    private void StyleDarkList()
    {
        list.OwnerDraw = true;
        // OwnerDraw 的 ListView 在鼠标移过时会闪烁、留残影，开启双缓冲后消除。
        typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(list, true, null);
        list.HotTracking = false;         // 不要系统的“热跟踪”下划线
        list.HoverSelection = false;      // 鼠标经过不自动选中
        list.ShowItemToolTips = false;
        list.DrawColumnHeader += (_, e) =>
        {
            listHeaderHeight = e.Bounds.Height;      // 供 Paint 里补画右侧空白用
            using var back = new SolidBrush(colPanel);
            e.Graphics.FillRectangle(back, e.Bounds);
            using var line = new Pen(colBorder);
            e.Graphics.DrawLine(line, e.Bounds.Right - 1, e.Bounds.Top + 3, e.Bounds.Right - 1, e.Bounds.Bottom - 3);
            e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            TextRenderer.DrawText(e.Graphics, e.Header!.Text, list.Font, e.Bounds, colText, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        };

        // 关键：ListView 在“没有焦点”时只触发 DrawItem、不触发 DrawSubItem。
        // 所以整行（背景 + 每一列的文字 + 网格线）必须在这里画完整，
        // 否则未聚焦时所有未选中的行都是空白的（选中后控件获得焦点才由 DrawSubItem 补上文字）。
        list.DrawItem += (_, e) =>
        {
            using var back = new SolidBrush(e.Item!.Selected ? colSelect : colInput);
            e.Graphics.FillRectangle(back, e.Bounds);
            int x = e.Bounds.Left;
            for (int i = 0; i < list.Columns.Count; i++)
            {
                var width = list.Columns[i].Width;
                DrawListCell(e.Graphics, new Rectangle(x, e.Bounds.Top, width, e.Bounds.Height),
                             i < e.Item.SubItems.Count ? e.Item.SubItems[i].Text : "");
                x += width;
            }
        };

        // 聚焦时逐格触发，按单元格矩形重画（内容与上面一致）。
        list.DrawSubItem += (_, e) =>
        {
            using var back = new SolidBrush(e.Item!.Selected ? colSelect : colInput);
            e.Graphics.FillRectangle(back, e.Bounds);
            DrawListCell(e.Graphics, e.Bounds, e.SubItem!.Text);
        };

        // OwnerDraw 只覆盖“真实存在的列”：最后一列右侧那条空白由**表头子窗口**自己画成白色，
        // 父控件的 Paint 覆盖不到它。两手准备：①最后一列自动填满剩余宽度（消除空白）；
        // ②给表头挂一个窗口子类，把它的背景擦除色改成暗色（兜住任何残留缝隙）。
        list.BorderStyle = BorderStyle.None;
        list.HandleCreated += (_, _) => AttachDarkHeader();
        list.Resize += (_, _) => FitLastColumn();
        list.Paint += (_, e) =>
        {
            using var border = new Pen(colBorder);
            var client = list.ClientSize;
            e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, client.Width - 1), Math.Max(0, client.Height - 1));
        };
    }

    /// <summary>
    /// 列表 + 自绘表头。原生表头（SysHeader32）是独立子窗口，会把右侧空白涂成白色、
    /// 且鼠标经过会重绘变色，父控件无法覆盖——所以这里直接不用原生表头，
    /// 改成上方一组跟着列宽走的标签，系统就没有可涂白的区域了。
    /// </summary>
    private Control BuildListHost()
    {
        var host = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = colBorder, Padding = new Padding(1), Margin = Padding.Empty };
        host.RowStyles.Add(new RowStyle(SizeType.Absolute, listHeaderHeight));
        host.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var head = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = colPanel, Margin = Padding.Empty, Padding = Padding.Empty };
        headerCells.Clear();
        foreach (ColumnHeader column in list.Columns)
        {
            var cell = new Label
            {
                Text = column.Text,
                Width = column.Width,
                Height = listHeaderHeight - 1,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = colText,
                BackColor = colPanel,
                Margin = Padding.Empty,
                Padding = new Padding(4, 0, 0, 0),
            };
            headerCells.Add((cell, column));
            head.Controls.Add(cell);
        }
        host.Controls.Add(head, 0, 0);
        host.Controls.Add(list, 0, 1);
        return host;
    }

    /// <summary>让最后一列吃掉剩余宽度，并把自绘表头的列宽同步过去。</summary>
    private void FitLastColumn()
    {
        if (list.Columns.Count == 0) return;
        int others = 0;
        for (int i = 0; i < list.Columns.Count - 1; i++) others += list.Columns[i].Width;
        int reserved = SystemInformation.VerticalScrollBarWidth + 2;   // 预留竖滚动条
        int avail = list.ClientSize.Width - others - reserved;
        if (avail > 80 && list.Columns[list.Columns.Count - 1].Width != avail)
            list.Columns[list.Columns.Count - 1].Width = avail;
        foreach (var (cell, column) in headerCells) cell.Width = column.Width;
    }

    /// <summary>给 ListView 的表头子窗口挂子类：把背景擦除色换成当前主题的面板色。</summary>
    private void AttachDarkHeader()
    {
        var header = SendMessage(list.Handle, 0x101F, IntPtr.Zero, IntPtr.Zero);   // LVM_GETHEADER
        if (header == IntPtr.Zero) return;
        darkHeader ??= new DarkHeaderPaint(() => colPanel);
        darkHeader.AssignHandle(header);
    }

    /// <summary>自绘一个列表单元格：下边框（暗色网格线）+ 左对齐文字。</summary>
    private void DrawListCell(Graphics g, Rectangle cell, string text)
    {
        using var line = new Pen(colBorder);
        g.DrawLine(line, cell.Left, cell.Bottom - 1, cell.Right, cell.Bottom - 1);
        var textRect = new Rectangle(cell.X + 4, cell.Y, Math.Max(0, cell.Width - 6), cell.Height);
        TextRenderer.DrawText(g, text, list.Font, textRect, colText, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    /// <summary>
    /// 一键处理「每次启动都被 Windows 拦下、要手动点同意」：
    /// 1) 清除本程序目录下所有文件的 Internet 来源标记（无需管理员）；
    /// 2) 报告 SmartScreen 状态，并询问是否关闭它的“检查应用和文件”（只影响当前用户，可随时改回）。
    /// 说明：未签名的程序无法彻底免除 SmartScreen 拦截，只能这样处理或购买代码签名证书。
    /// </summary>
    private void FixLaunchBlock()
    {
        var dir = Path.GetDirectoryName(Application.ExecutablePath) ?? AppContext.BaseDirectory;
        int cleared = 0, failed = 0;
        try
        {
            foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { if (File.Exists(file + ":Zone.Identifier")) { File.Delete(file + ":Zone.Identifier"); cleared++; } }
                catch { failed++; }
            }
        }
        catch (Exception ex) { Log("扫描程序目录失败：" + ex.Message); }
        Log($"启动拦截修复：已清除 {cleared} 个文件的 Internet 来源标记{(failed > 0 ? $"（{failed} 个失败）" : "")}。目录：{dir}");

        var smartScreen = ReadSmartScreen();
        Log($"SmartScreen「检查应用和文件」当前状态：{(smartScreen is null ? "未显式设置（系统默认开启）" : smartScreen == 1 ? "已开启" : "已关闭")}");
        if (smartScreen == 0)
        {
            MessageBox.Show(this,
                $"已清除 {cleared} 个文件的“来自 Internet”标记。\n\n" +
                "SmartScreen 的应用检查本来就是关闭的，所以再被拦就不是它造成的。\n" +
                "请把弹窗标题那一行原文发给作者，以便继续定位。",
                "启动被拦修复", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var answer = MessageBox.Show(this,
            $"已清除 {cleared} 个文件的“来自 Internet”标记。\n\n" +
            "如果弹窗标题是「Windows 已保护你的电脑」或「无法识别的应用」，\n" +
            "那是 SmartScreen 在拦未签名的程序，不是病毒查杀。\n\n" +
            "是否现在关闭 SmartScreen 的「检查应用和文件」？\n" +
            "· 只影响当前 Windows 用户，随时可以改回；\n" +
            "· 也可以不改系统设置，每次弹窗时点「更多信息 → 仍要运行」。",
            "启动被拦修复", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) { Log("未修改 SmartScreen 设置（可随时再点本按钮）。"); return; }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\AppHost", true);
            key?.SetValue("EnableWebContentEvaluation", 0, RegistryValueKind.DWord);
            Log("已关闭 SmartScreen 的「检查应用和文件」（仅当前用户）。改回：把该值设为 1。");
            MessageBox.Show(this, "已关闭。请关掉本程序，重新双击启动确认不再被拦。", "启动被拦修复", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log("修改 SmartScreen 设置失败：" + FullException(ex));
            MessageBox.Show(this, "修改失败：" + ex.Message, "启动被拦修复", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>读取 SmartScreen 的“检查应用和文件”开关：1=开、0=关、null=未设置（默认开）。</summary>
    private static int? ReadSmartScreen()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\AppHost");
            var value = key?.GetValue("EnableWebContentEvaluation");
            return value is null ? null : Convert.ToInt32(value);
        }
        catch { return null; }
    }

    /// <summary>切换深色 / 浅色配色并立即重绘。</summary>
    private void ApplyTheme(bool dark)
    {
        if (dark)
        {
            colForm = Color.FromArgb(0x1E, 0x1E, 0x1E); colPanel = Color.FromArgb(0x25, 0x25, 0x26);
            colInput = Color.FromArgb(0x2D, 0x2D, 0x30); colText = Color.FromArgb(0xE6, 0xE6, 0xE6);
            colButton = Color.FromArgb(0x3A, 0x3D, 0x41); colBorder = Color.FromArgb(0x55, 0x55, 0x5A);
            colSelect = Color.FromArgb(0x09, 0x4E, 0x77);
        }
        else
        {
            colForm = Color.FromArgb(0xF0, 0xF0, 0xF0); colPanel = Color.FromArgb(0xFF, 0xFF, 0xFF);
            colInput = Color.FromArgb(0xFF, 0xFF, 0xFF); colText = Color.FromArgb(0x1A, 0x1A, 0x1A);
            colButton = Color.FromArgb(0xE1, 0xE1, 0xE1); colBorder = Color.FromArgb(0xA0, 0xA0, 0xA0);
            colSelect = Color.FromArgb(0xCC, 0xE4, 0xF7);
        }
        BackColor = colForm;
        ForeColor = colText;
        Theme(this);
        ThemeProgressBars();
        ThemeComboBoxes();
        list.Invalidate();
    }

    /// <summary>递归套用配色；容器面板也要设，否则会露出系统默认底色。</summary>
    private void Theme(Control parent)
    {
        parent.ForeColor = colText;
        foreach (Control c in parent.Controls)
        {
            switch (c)
            {
                case TextBox tb: tb.BackColor = colInput; tb.ForeColor = colText; tb.BorderStyle = BorderStyle.FixedSingle; break;
                case ListView lv: lv.BackColor = colInput; lv.ForeColor = colText; break;
                case NumericUpDown num: num.BackColor = colInput; num.ForeColor = colText; num.BorderStyle = BorderStyle.FixedSingle; break;
                case ComboBox combo:
                    // 右侧箭头由系统主题绘制，只改 BackColor 会留下白色块；
                    // 扁平样式 + 关掉控件主题（ThemeComboBoxes）后才跟随配色。
                    combo.FlatStyle = FlatStyle.Flat;
                    combo.BackColor = colInput;
                    combo.ForeColor = colText;
                    break;
                case Button b:
                    b.FlatStyle = FlatStyle.Flat; b.BackColor = colButton; b.ForeColor = colText;
                    b.FlatAppearance.BorderColor = colBorder; b.FlatAppearance.MouseOverBackColor = colSelect;
                    break;
                case CheckBox cb: cb.BackColor = Color.Transparent; cb.ForeColor = colText; break;
                case Label lb: lb.BackColor = Color.Transparent; break;
                default: c.BackColor = c is TableLayoutPanel or FlowLayoutPanel ? colPanel : colForm; break;
            }
            Theme(c);
        }
    }

    // ---------------- 任务编排 ----------------
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (activeCts != null) return;
        SaveSettings();
        activeCts = new CancellationTokenSource();
        imageGate.Dispose();
        imageGate = new SemaphoreSlim((int)imageConcurrency.Value, (int)imageConcurrency.Value);
        sessionInvalid = false;
        sessionRecoveryTask = null;
        ResumeTasks(updateStatus: false);
        SetBusy(true);
        try { await action(activeCts.Token); status.Text = "完成"; }
        catch (OperationCanceledException) { Log("任务已停止。"); status.Text = "已停止"; }
        catch (SessionExpiredException ex) { Log("会话失效：" + ex.Message); status.Text = "会话失效"; }
        catch (Exception ex) { Log("错误：" + FullException(ex)); status.Text = "失败"; }
        finally
        {
            ResumeTasks(updateStatus: false);
            sessionInvalid = false;
            sessionRecoveryTask = null;
            activeCts.Dispose();
            activeCts = null;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        loginButton.Enabled = scanButton.Enabled = firstPageButton.Enabled = testButton.Enabled = allButton.Enabled = folderButton.Enabled = !busy;
        downloadPageButton.Enabled = downloadSelectedButton.Enabled = downloadRangeButton.Enabled = fixButton.Enabled = !busy;
        identityButton.Enabled = updatePositionButton.Enabled = renameFolderButton.Enabled = !busy;
        stopButton.Enabled = busy;
        pauseButton.Enabled = busy;
        pauseButton.Text = "暂停";
    }

    // ---------------- 暂停 / 继续 ----------------
    /// <summary>暂停闸门。下载线程在作品开始、每个请求前调用；暂停时阻塞但不取消，继续后原样接着跑。</summary>
    private void WaitWhilePaused(CancellationToken ct)
    {
        if (pauseGate.IsSet) return;
        Ui(() => { if (!status.Text.StartsWith("已暂停")) status.Text = "已暂停"; });
        while (!pauseGate.IsSet)
        {
            ct.ThrowIfCancellationRequested();
            pauseGate.Wait(200);
        }
    }

    private void PauseTasks(string reason)
    {
        if (!pauseGate.IsSet) return;
        pauseGate.Reset();
        Ui(() =>
        {
            pauseButton.Text = "继续";
            status.Text = "已暂停";
        });
        Log($"任务已暂停（{reason}）。已完成的图片不会丢，点「继续」从当前进度接着下载。");
    }

    private void ResumeTasks(bool updateStatus = true)
    {
        pauseGate.Set();
        Ui(() =>
        {
            pauseButton.Text = "暂停";
            if (updateStatus) status.Text = "运行中";
        });
    }

    /// <summary>「暂停 / 继续」按钮。若暂停原因是会话失效，继续前会先重新验证会话。</summary>
    private async Task TogglePauseAsync()
    {
        if (activeCts is null) return;
        if (pauseGate.IsSet)
        {
            PauseTasks("手动暂停");
            return;
        }

        if (sessionInvalid)
        {
            sessionRecoveryTask = null;         // 手动继续时允许再试一次重新登录
            bool ok = await RequestSessionRecoveryAsync(activeCts.Token);
            if (!ok)
            {
                Log("会话仍未验证成功，任务保持暂停。请检查账号密码/验证码后点「继续」重试。");
                Ui(() => status.Text = "已暂停（会话失效）");
                return;
            }
            sessionInvalid = false;
        }
        ResumeTasks();
        Log("任务已继续。");
    }

    /// <summary>
    /// 会话失效时的统一处理：暂停所有下载任务 → 重新验证会话（重新登录）→ 成功则自动恢复。
    /// 多个作品同时报失效时共用同一次重新登录，避免反复提交账号密码。
    /// </summary>
    private async Task<bool> HandleSessionExpiredAsync(CancellationToken ct)
    {
        sessionInvalid = true;
        PauseTasks("会话失效，等待重新验证");
        Ui(() => status.Text = "已暂停（会话失效）");
        bool ok = await RequestSessionRecoveryAsync(ct);
        if (ok)
        {
            sessionInvalid = false;
            Log("会话已重新验证，任务继续。");
            ResumeTasks();
        }
        else
        {
            Log("会话重新验证失败：任务保持暂停。请检查账号密码/验证码后点「继续」。");
            Ui(() => pauseButton.Text = "继续");
        }
        return ok;
    }

    private Task<bool> RequestSessionRecoveryAsync(CancellationToken ct)
    {
        lock (sessionRecoveryLock)
        {
            if (sessionRecoveryTask is not null) return sessionRecoveryTask;
            sessionRecoveryTask = RecoverSessionAsync(ct);
            return sessionRecoveryTask;
        }
    }

    /// <summary>把重新登录切回 UI 线程执行（LoginAsync 会读写界面上的账号/密码/站点地址）。</summary>
    private async Task<bool> RecoverSessionAsync(CancellationToken ct)
    {
        var completion = new TaskCompletionSource<bool>();
        Ui(() => _ = RecoverSessionCoreAsync(completion, ct));
        return await completion.Task;
    }

    private async Task RecoverSessionCoreAsync(TaskCompletionSource<bool> completion, CancellationToken ct)
    {
        try
        {
            Log("正在重新验证会话（重新登录）…");
            await LoginAsync(ct);
            completion.TrySetResult(authenticated);
        }
        catch (OperationCanceledException)
        {
            completion.TrySetCanceled();
        }
        catch (Exception ex)
        {
            Log("重新验证会话失败：" + FullException(ex));
            completion.TrySetResult(false);
        }
    }

    private static string FullException(Exception ex)
    {
        var sb = new StringBuilder();
        for (Exception? e = ex; e != null; e = e.InnerException) sb.AppendLine(e.GetType().Name + ": " + e.Message);
        return sb.ToString().Trim();
    }

    // ---------------- 输出目录检查 ----------------
    private string ResolveOutputRoot()
    {
        var root = output.Text.Trim();
        if (root.Length == 0) throw new InvalidOperationException("请设置输出目录。");
        EnsureDirectory(root, root);
        EnsureSessionLog(root);
        return root;
    }

    /// <summary>运行日志定位到输出目录根：AHRI8Backup.log。写在启动/任务时确定的目录里，便于事后查看。</summary>
    private void EnsureSessionLog(string outputRoot)
    {
        lock (logFileLock)
        {
            try
            {
                var path = Path.Combine(outputRoot, "AHRI8Backup.log");
                if (!string.Equals(path, sessionLogPath, StringComparison.OrdinalIgnoreCase))
                {
                    sessionLogPath = path;
                    var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ===== AHRI8Backup v1.4.0 运行日志：{path} =====";
                    File.AppendAllText(sessionLogPath, line + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch
            {
                sessionLogPath = "";
            }
        }
    }

    private static void EnsureDirectory(string directory, string outputRoot)
    {
        try { Directory.CreateDirectory(directory); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException or ArgumentException)
        {
            throw new InvalidOperationException($"无法创建目录：{directory}{Environment.NewLine}原因：{ex.GetType().Name} - {ex.Message}{Environment.NewLine}{OutputWriteAdvice(outputRoot)}", ex);
        }
    }

    /// <summary>目录已存在但不可写时 CreateDirectory 不会报错，必须实际写一次才能发现。</summary>
    private static void VerifyWritable(string root)
    {
        var probe = Path.Combine(root, ".ahri8-write-test");
        try { File.WriteAllText(probe, "probe"); File.Delete(probe); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException($"输出目录存在但不可写：{root}{Environment.NewLine}原因：{ex.GetType().Name} - {ex.Message}{Environment.NewLine}{OutputWriteAdvice(root)}", ex);
        }
    }

    private static string OutputWriteAdvice(string root) => string.Join(Environment.NewLine, new[]
    {
        $"当前输出目录：{root}",
        "可以这样处理：",
        "  1) 如果本程序是从别的工具或沙箱环境里打开的，它对输出目录只有读权限。请改为在资源管理器里直接双击 AHRI8Backup.exe 运行。",
        "  2) 改选当前用户可写的目录（例如“图片”或“文档”下新建的空文件夹）后重试。",
        "  3) 确认该目录没有被安全软件或组策略保护，也没有只读属性或被其他程序占用。"
    });

    // ---------------- 设置持久化 ----------------
    // JavaScriptSerializer 属于 .NET Framework 自带的 System.Web.Extensions，
    // 不需要再额外嵌入 JSON 库，单文件里也不会多出一个 DLL。
    // 实例本身不保证线程安全，而 manifest 由多个下载任务并发写入，所以统一走下面的加锁方法。
    private static readonly JavaScriptSerializer SettingsJson = new() { MaxJsonLength = int.MaxValue };
    private static readonly object JsonGate = new();

    private static string JsonSerialize(object value)
    {
        lock (JsonGate) return SettingsJson.Serialize(value);
    }

    private static T? JsonDeserialize<T>(string text) where T : class
    {
        lock (JsonGate) return SettingsJson.Deserialize<T>(text);
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var saved = JsonDeserialize<RememberedSettings>(File.ReadAllText(SettingsPath));
            if (saved is null) return;
            rememberText.Checked = saved.RememberText;
            rememberPassword.Checked = saved.RememberPassword;
            darkMode.Checked = saved.DarkMode;
            loadedNamingMode = saved.NamingMode == 1 ? 1 : 0;
            loadedExistingMode = saved.ExistingMode is >= 0 and <= 2 ? saved.ExistingMode : 0;
            if (saved.RememberText)
            {
                if (!string.IsNullOrWhiteSpace(saved.BaseUrl)) baseUrl.Text = saved.BaseUrl;
                if (!string.IsNullOrWhiteSpace(saved.FavoritesUrl)) favoritesUrl.Text = saved.FavoritesUrl;
                if (saved.OutputDirectory is not null) output.Text = saved.OutputDirectory;
                if (saved.Username is not null) username.Text = saved.Username;
            }
            if (saved.RememberPassword && !string.IsNullOrWhiteSpace(saved.ProtectedPassword))
                password.Text = UnprotectPassword(saved.ProtectedPassword!);
        }
        catch
        {
            // 设置文件损坏或不可读都不应阻止程序启动。
        }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var saved = new RememberedSettings
            {
                RememberText = rememberText.Checked,
                RememberPassword = rememberPassword.Checked,
                DarkMode = darkMode.Checked,
                NamingMode = Math.Max(0, namingMode.SelectedIndex),
                ExistingMode = Math.Max(0, existingMode.SelectedIndex),
                BaseUrl = rememberText.Checked ? baseUrl.Text : null,
                FavoritesUrl = rememberText.Checked ? favoritesUrl.Text : null,
                OutputDirectory = rememberText.Checked ? output.Text : null,
                Username = rememberText.Checked ? username.Text : null,
                ProtectedPassword = rememberPassword.Checked && password.Text.Length > 0 ? ProtectPassword(password.Text) : null
            };
            File.WriteAllText(SettingsPath, Pretty(JsonSerialize(saved)), new UTF8Encoding(false));
        }
        catch (Exception ex) { Log("无法保存输入设置：" + ex.Message); }
    }

    private static string ProtectPassword(string value) => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    private static string UnprotectPassword(string value) => Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));

    /// <summary>
    /// “记住文本 / 记住密码”读写的设置文件在首次运行时就创建出来，
    /// 之后可以直接查看、备份或迁移它，不必等某次勾选后才出现。
    /// </summary>
    private void EnsureSettingsFile()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            if (File.Exists(SettingsPath)) return;
            SaveSettings();
            Log($"首次运行：已创建设置文件 {SettingsPath}");
        }
        catch (Exception ex) { Log("无法创建设置文件：" + ex.Message); }
    }

    // ---------------- 登录 ----------------
    private async Task LoginAsync(CancellationToken ct)
    {
        if (!Uri.TryCreate(favoritesUrl.Text.Trim(), UriKind.Absolute, out var fav) || (fav.Scheme != "https" && fav.Scheme != "http"))
            throw new InvalidOperationException("收藏夹 URL 无效。");
        Log("正在直接请求收藏夹页面（不会打开浏览器）…");
        var result = await GetHtmlAsync(fav.ToString(), null, ct, allowLoginPage: true);
        if (!HasPasswordForm(result.Html))
        {
            authenticated = !LooksLikeLoginPage(result.Html, result.FinalUrl);
            Log(authenticated ? $"页面未显示登录表单，暂按已登录处理。最终地址：{result.FinalUrl}" : $"页面像是登录页，但未找到标准密码表单。最终地址：{result.FinalUrl}");
            return;
        }
        if (string.IsNullOrWhiteSpace(username.Text) || string.IsNullOrEmpty(password.Text))
            throw new InvalidOperationException("页面要求登录，请填写账号和密码。凭据只用于当前运行，不会写入日志。");
        var doc = Parse(result.Html);
        var form = doc.DocumentNode.SelectSingleNode("//form[.//input[@type='password']]") ?? throw new InvalidOperationException("找不到包含密码框的登录表单。");
        var pw = form.SelectSingleNode(".//input[@type='password']")!;
        var user = form.SelectSingleNode(".//input[not(@type='password') and (@type='email' or contains(translate(@name,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'email') or contains(translate(@name,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'user') or contains(translate(@name,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'account') or contains(translate(@id,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'email') or contains(translate(@id,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'user'))]");
        user ??= form.SelectSingleNode(".//input[@type='text' or not(@type)]");
        if (user is null || string.IsNullOrWhiteSpace(user.GetAttributeValue("name", "")) || string.IsNullOrWhiteSpace(pw.GetAttributeValue("name", "")))
            throw new InvalidOperationException("无法可靠识别登录表单的账号/密码字段；为避免猜错字段，已停止。需要检查表单的 input name（不要提供输入值）。");

        var values = new List<KeyValuePair<string, string>>();
        foreach (var input in form.SelectNodes(".//input[@name]") ?? Enumerable.Empty<HtmlNode>())
        {
            var n = input.GetAttributeValue("name", "");
            if (n.Length == 0) continue;
            var type = input.GetAttributeValue("type", "text").ToLowerInvariant();
            if (type is "submit" or "button" or "image" or "file" or "reset") continue;
            if (input == user) values.Add(new(n, username.Text.Trim()));
            else if (input == pw) values.Add(new(n, password.Text));
            else values.Add(new(n, input.GetAttributeValue("value", "")));
        }
        foreach (var select in form.SelectNodes(".//select[@name]") ?? Enumerable.Empty<HtmlNode>())
        {
            var opt = select.SelectSingleNode(".//option[@selected]") ?? select.SelectSingleNode(".//option");
            if (opt != null) values.Add(new(select.GetAttributeValue("name", ""), opt.GetAttributeValue("value", opt.InnerText)));
        }

        var method = form.GetAttributeValue("method", "post").ToUpperInvariant();
        var actionUri = new Uri(new Uri(result.FinalUrl), HtmlEntity.DeEntitize(form.GetAttributeValue("action", result.FinalUrl)));
        Log($"检测到登录表单：账号字段 '{user.GetAttributeValue("name", "")}'，密码字段 '{pw.GetAttributeValue("name", "")}'，提交方式 {method}。正在提交…");
        HttpResponseMessage response;
        if (method == "GET")
        {
            var ub = new UriBuilder(actionUri);
            var qs = HttpUtility.ParseQueryString(ub.Query);
            foreach (var kv in values) qs[kv.Key] = kv.Value;
            ub.Query = qs.ToString();
            response = await SendAsync(HttpMethod.Get, ub.Uri.ToString(), result.FinalUrl, null, ct);
        }
        else response = await SendAsync(HttpMethod.Post, actionUri.ToString(), result.FinalUrl, new FormUrlEncodedContent(values), ct);

        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? actionUri.ToString();
        var html = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();
        authenticated = !HasPasswordForm(html) && !LooksLikeLoginPage(html, finalUrl);
        Log(authenticated ? "登录请求看起来成功。" : "提交后仍检测到登录页，可能是账号密码错误、验证码或网站使用了非标准登录流程。没有保存凭据。");
        if (!authenticated) throw new InvalidOperationException("登录未确认成功。请检查账号密码、验证码/安全验证要求，或提供表单字段名供适配（不要提供凭据值）。");
    }

    // ---------------- 扫描 ----------------
    private async Task ScanFavoritesAsync(CancellationToken ct)
    {
        var start = favoritesUrl.Text.Trim();
        if (!Uri.TryCreate(start, UriKind.Absolute, out var startUri)) throw new InvalidOperationException("收藏夹地址无效。");

        var baseUri = new UriBuilder(startUri);
        var baseQuery = HttpUtility.ParseQueryString(baseUri.Query);
        baseQuery.Remove("page");
        baseUri.Query = baseQuery.ToString();

        var all = new Dictionary<string, Work>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<int>();
        int pageNo = int.TryParse(HttpUtility.ParseQueryString(startUri.Query)["page"], out var n) && n > 0 ? n : 1;
        int pages = 0, emptyPages = 0;
        Log("开始扫描收藏夹 HTML；按页码顺序扫描，不下载图片文件。");

        while (pages < 500)
        {
            ct.ThrowIfCancellationRequested();
            if (!visited.Add(pageNo)) { Log($"检测到重复收藏页码 {pageNo}，停止扫描以避免循环。"); break; }

            var builder = new UriBuilder(baseUri.Uri);
            var query = HttpUtility.ParseQueryString(builder.Query);
            query["page"] = pageNo.ToString();
            builder.Query = query.ToString();

            var page = await GetHtmlAsync(builder.Uri.ToString(), pages == 0 ? null : baseUri.Uri.ToString(), ct);
            pages++;
            var cipher = PageCipher.FromHtml(page.Html);
            var pageWorks = ParseWorkList(page.Html, page.FinalUrl, pageNo, cipher, out var candidateLinks);
            int before = all.Count;
            foreach (var w in pageWorks) if (!all.ContainsKey(w.Id)) all[w.Id] = w;
            int added = all.Count - before;
            emptyPages = added == 0 ? emptyPages + 1 : 0;
            Log($"收藏页 {pageNo}：本页新增 {added} 部，识别到作品链接 {candidateLinks} 个，本页去重 {pageWorks.Count} 部，累计 {all.Count} 部。");

            if (added == 0 && candidateLinks == 0)
            {
                var doc = Parse(page.Html);
                var title = Clean(doc.DocumentNode.SelectSingleNode("//title")?.InnerText ?? "");
                var samples = (doc.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
                    .Select(a => HtmlEntity.DeEntitize(a.GetAttributeValue("href", "")))
                    .Where(h => h.Contains("post", StringComparison.OrdinalIgnoreCase) || h.Contains("favorite", StringComparison.OrdinalIgnoreCase))
                    .Take(8);
                Log($"收藏页诊断：页面标题='{title}'；最终地址={page.FinalUrl}；包含登录表单={HasPasswordForm(page.Html)}；疑似链接样本={string.Join(" | ", samples)}");
                break;
            }
            // 末页判定看“本页实际有几部作品”，而不是“新增几部”：
            // 若翻页期间有作品被移出收藏夹或重复出现，用新增数会误判末页而漏扫后续页。
            if (pageWorks.Count < 20) break;
            // 站点若忽略 page 参数会一直返回同一页，连续两页没有新增即停止。
            if (emptyPages >= 2) { Log("连续 2 页没有新增作品，可能是站点忽略了 page 参数；已停止扫描以避免重复请求。"); break; }
            pageNo++;
            if (pages % 20 == 0) await Task.Delay(200, ct);
        }

        ReplaceWorkList(all.Values);
        status.Text = $"发现 {all.Count} 部";
        Log($"扫描完成：{pages} 个收藏页，发现 {all.Count} 部作品。请先核对数量和标题。");
        if (all.Count == 0) Log("未找到作品。若诊断显示页面标题是登录页，说明登录 Cookie 未生效；否则请把“收藏页诊断”一行发来继续适配 DOM。");
    }

    private async Task ScanFirstPageAsync(CancellationToken ct)
    {
        var start = favoritesUrl.Text.Trim();
        if (!Uri.TryCreate(start, UriKind.Absolute, out var startUri) || startUri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("收藏夹地址无效。");

        var builder = new UriBuilder(startUri);
        var query = HttpUtility.ParseQueryString(builder.Query);
        query["page"] = "1";
        builder.Query = query.ToString();

        Log("开始扫描收藏夹第 1 页；只请求网页 HTML，不下载图片。识别到的作品将加入可下载列表。");
        var page = await GetHtmlAsync(builder.Uri.ToString(), null, ct);
        var cipher = PageCipher.FromHtml(page.Html);
        var pageWorks = ParseWorkList(page.Html, page.FinalUrl, 1, cipher, out var candidateLinks);
        var doc = Parse(page.Html);
        var pageTitle = Clean(doc.DocumentNode.SelectSingleNode("//title")?.InnerText ?? "");
        Log($"第一页扫描结果：识别到 {candidateLinks} 个作品链接，去重后 {pageWorks.Count} 部。页面标题='{pageTitle}'；最终地址={page.FinalUrl}；包含登录表单={HasPasswordForm(page.Html)}。");
        foreach (var item in pageWorks.Take(10)) Log($"样本：{item.Title} (ID={item.Id})");

        if (pageWorks.Count == 0)
        {
            var samples = (doc.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
                .Select(a => HtmlEntity.DeEntitize(a.GetAttributeValue("href", "")))
                .Where(h => h.Contains("post", StringComparison.OrdinalIgnoreCase) || h.Contains("favorite", StringComparison.OrdinalIgnoreCase))
                .Take(8);
            Log("第一页没有识别到作品；疑似链接样本=" + string.Join(" | ", samples));
            return;
        }
        ReplaceWorkList(pageWorks);
        status.Text = $"第一页发现 {pageWorks.Count} 部";
        Log("第一页作品已加入可下载列表；可选择一部测试下载，或直接并发下载全部。");
    }

    /// <summary>解析一页 HTML，挑出作品链接并提取标题/作者；页内序号按本页去重后的顺序计。</summary>
    private List<Work> ParseWorkList(string html, string finalUrl, int pageNo, PageCipher cipher, out int candidateLinks)
    {
        var doc = Parse(html);
        var found = new Dictionary<string, Work>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        candidateLinks = 0;
        foreach (var a in doc.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
        {
            var href = HtmlEntity.DeEntitize(a.GetAttributeValue("href", "")).Trim();
            if (href.Length == 0 || !href.Contains("post.php", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Uri.TryCreate(new Uri(finalUrl), href, out var workUri)) continue;
            var q = HttpUtility.ParseQueryString(workUri.Query);
            var id = q["ID"] ?? q["id"];
            if (string.IsNullOrWhiteSpace(id)) id = Regex.Match(href, @"(?:\?|&)ID=(\d+)", RegexOptions.IgnoreCase).Groups[1].Value;
            if (string.IsNullOrWhiteSpace(id) || !Regex.IsMatch(id, @"^\d+$")) continue;
            candidateLinks++;
            if (!seen.Add(id) || found.ContainsKey(id)) continue;
            var (title, authors) = SplitTitleAndAuthors(ExtractWorkTitle(a, id, cipher));
            found[id] = new Work(id, title, authors, workUri.ToString(), pageNo, seen.Count);
        }
        return found.Values.ToList();
    }

    private void ReplaceWorkList(IEnumerable<Work> newWorks)
    {
        works.Clear();
        works.AddRange(newWorks);
        currentPage = 1;
        ShowPage(1);
    }

    private int TotalPages => works.Count == 0 ? 0 : (works.Count + PageSize - 1) / PageSize;
    private NamingMode Mode => namingMode.SelectedIndex == 1 ? NamingMode.ReverseNumber : NamingMode.PageIndex;

    /// <summary>列表只显示当前页的 20 条（与站点收藏夹每页数量一致），首列是全局序号。</summary>
    private void ShowPage(int page)
    {
        int total = TotalPages;
        currentPage = total == 0 ? 1 : Math.Max(1, Math.Min(page, total));
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var w in CurrentPageWorks())
        {
            int index = works.IndexOf(w) + 1;
            var item = new ListViewItem(Mode == NamingMode.ReverseNumber ? $"{index}（倒序 {works.Count - index + 1}）" : index.ToString());
            item.SubItems.Add(w.Title);
            item.SubItems.Add(w.Id);
            item.SubItems.Add(StatusOf(w));
            item.SubItems.Add(w.Url);
            item.Tag = w;
            list.Items.Add(item);
        }
        list.EndUpdate();
        FitLastColumn();            // 最后一列填满，避免表头右侧留白
        pageInfo.Text = total == 0 ? "第 0/0 页" : $"第 {currentPage}/{total} 页 · 共 {works.Count} 部";
        jumpPage.Maximum = Math.Max(1, total);
        jumpPage.Value = currentPage;
        rangeFrom.Maximum = rangeTo.Maximum = Math.Max(1, works.Count);
        rangeFrom.Value = Math.Max(1, (currentPage - 1) * PageSize + 1);
        rangeTo.Value = Math.Max(1, Math.Min(works.Count, currentPage * PageSize));
        prevPageButton.Enabled = currentPage > 1;
        nextPageButton.Enabled = total > 0 && currentPage < total;
    }

    private IEnumerable<Work> CurrentPageWorks() => works.Skip((currentPage - 1) * PageSize).Take(PageSize);

    private IEnumerable<Work> SelectedWorks() => list.SelectedItems.Cast<ListViewItem>().Select(i => (Work)i.Tag!).ToList();

    private IEnumerable<Work> RangeWorks()
    {
        int from = (int)rangeFrom.Value, to = (int)rangeTo.Value;
        if (from > to) (from, to) = (to, from);
        return works.Skip(from - 1).Take(to - from + 1).ToList();
    }

    /// <summary>目标目录已存在时给出已有张数（有 manifest 还能算出完整度），方便接着上次的进度继续。</summary>
    private string StatusOf(Work w)
    {
        try
        {
            var root = output.Text.Trim();
            if (root.Length == 0) return "待处理";
            var folder = Path.Combine(root, BuildFolderName(w));
            if (!Directory.Exists(folder)) return "待处理";
            int count = Directory.EnumerateFiles(folder)
                .Count(f => ImageIntegrity.IsSupportedExtension(Path.GetExtension(f)));
            if (count == 0) return "待处理";
            try
            {
                var mf = Path.Combine(folder, "manifest.json");
                if (File.Exists(mf))
                {
                    var m = JsonDeserialize<Manifest>(File.ReadAllText(mf));
                    if (m?.ImageCount > 0) return count >= m.ImageCount ? $"已完整 {count} 张" : $"已存在 {count}/{m.ImageCount} 张";
                }
                // 没有 manifest 时用下载日志兜底：日志里 ok 的张数就是上次已完成的页数。
                var history = new DownloadLog(folder);
                if (history.OkCount > 0) return $"已存在 {count} 张（日志 {history.OkCount}）";
            }
            catch { }
            return $"已存在 {count} 张";
        }
        catch { }
        return "待处理";
    }

    // ---------------- 标题与作者 ----------------
    private string ExtractWorkTitle(HtmlNode link, string id, PageCipher cipher)
    {
        // 站点把标题/作者加密成 Base64 密文（每页随机 aek/aei，前端 decrypt() 还原），这里直接解密。
        var candidates = new List<string>();
        var trusted = new HashSet<string>(StringComparer.Ordinal);   // 解密成功的值不再按“混淆 token”过滤
        void Add(string? value)
        {
            foreach (var (text, fromCipher) in cipher.Decode(value))
            {
                var cleaned = Clean(text);
                if (cleaned.Length == 0 || cleaned.Length > 180) continue;
                if (!candidates.Contains(cleaned, StringComparer.Ordinal)) candidates.Add(cleaned);
                if (fromCipher) trusted.Add(cleaned);
            }
        }

        Add(link.InnerText);
        Add(link.GetAttributeValue("aria-label", ""));
        Add(link.GetAttributeValue("data-title", ""));
        foreach (var img in link.SelectNodes(".//img") ?? Enumerable.Empty<HtmlNode>())
        {
            Add(img.GetAttributeValue("alt", ""));
            Add(img.GetAttributeValue("title", ""));
            Add(img.GetAttributeValue("data-title", ""));
        }

        // 由近及远在作品卡片里找标题节点、data-title，以及非作品链接的可见文字。
        var depth = 0;
        for (HtmlNode? ancestor = link.ParentNode; ancestor != null && depth < 6; ancestor = ancestor.ParentNode, depth++)
        {
            foreach (var node in ancestor.SelectNodes(
                ".//h1|.//h2|.//h3|.//h4|.//h5|.//h6|.//*[@data-title]|.//*[contains(translate(@class,'TITLE','title'),'title')]|.//*[contains(translate(@class,'NAME','name'),'name')]") ?? Enumerable.Empty<HtmlNode>())
            {
                Add(node.GetAttributeValue("data-title", ""));
                Add(node.GetAttributeValue("aria-label", ""));
                Add(node.GetAttributeValue("title", ""));
                Add(node.InnerText);
            }
            foreach (var sibling in ancestor.SelectNodes(".//a[@href]") ?? Enumerable.Empty<HtmlNode>())
            {
                if (ReferenceEquals(sibling, link)) continue;
                if (sibling.GetAttributeValue("href", "").Contains("post.php", StringComparison.OrdinalIgnoreCase)) continue;
                Add(sibling.GetAttributeValue("data-title", ""));
                Add(sibling.GetAttributeValue("title", ""));
                Add(sibling.InnerText);
                foreach (var img in sibling.SelectNodes(".//img") ?? Enumerable.Empty<HtmlNode>()) Add(img.GetAttributeValue("alt", ""));
            }
            // 卡片说明节点里是纯标题（不含作者），仅在标题链接不可用时作兜底。
            foreach (var desc in ancestor.SelectNodes(".//*[@class='d']|.//*[@class='d_t']|.//*[contains(concat(' ',normalize-space(@class),' '),' desc ')]") ?? Enumerable.Empty<HtmlNode>())
                Add(desc.InnerText);
            // 有些模板把标题直接写在卡片容器里，只接受长度适中且不像导航/按钮文案的文本。
            Add(ancestor.GetAttributeValue("data-title", ""));
            var containerText = Clean(ancestor.InnerText);
            if (containerText.Length is >= 4 and <= 100 &&
                !Regex.IsMatch(containerText, @"^(登录|登入|收藏夹|收藏|删除|阅读|閱讀|详情|詳細|首页|首頁|上一页|下一页)\b", RegexOptions.IgnoreCase))
                Add(containerText);
        }

        Add(link.GetAttributeValue("title", ""));   // title 属性只作最后候选，且必须通过混淆检查
        var title = candidates.FirstOrDefault(x => !IsGenericLinkLabel(x) && (trusted.Contains(x) || !IsOpaqueTitleToken(x)));
        if (title is null)
        {
            Log($"作品 {id} 未提取到可用标题，候选字段={string.Join(" | ", candidates.Take(8))}；已回退为作品编号。请将此行日志发来以继续适配收藏页结构。");
            return "作品 " + id;
        }
        return title;
    }

    /// <summary>典型混淆值：很长、几乎只有 Base64 字符，且不含中日韩文字。</summary>
    private static bool IsOpaqueTitleToken(string value) =>
        value.Length >= 40 && !Regex.IsMatch(value, @"[\u3400-\u9FFF\u3040-\u30FF\uAC00-\uD7AF]") && Regex.IsMatch(value, @"^[A-Za-z0-9_+/=-]+$");

    private static bool IsGenericLinkLabel(string value) => Regex.IsMatch(value, @"^(阅读|閱讀|查看|详情|詳細|点击|點擊|read more|more|view|open)$", RegexOptions.IgnoreCase);

    /// <summary>把标题开头的 [作者] / 【作者】 摘出来；方括号内用圆括号分隔的多个名字各算一位作者。</summary>
    private static (string Title, List<string> Authors) SplitTitleAndAuthors(string source)
    {
        var authors = new List<string>();
        var remaining = Clean(source);
        while (remaining.Length > 2 && (remaining[0] == '[' || remaining[0] == '【'))
        {
            char close = remaining[0] == '[' ? ']' : '】';
            int end = remaining.IndexOf(close, 1);
            if (end <= 1) break;
            foreach (Match part in Regex.Matches(Clean(remaining.Substring(1, end - 1)), @"[^()（）]+"))
            {
                var author = Clean(part.Value);
                if (author.Length > 0) authors.Add(author);
            }
            remaining = remaining.Substring(end + 1).TrimStart();
        }
        return (remaining.Length == 0 ? Clean(source) : remaining, authors);
    }

    /// <summary>把三级进度刷到界面上（由 150ms 的计时器调用，避免下载线程频繁 Invoke）。</summary>
    private void RefreshProgress()
    {
        // 单文件：最近一次在传的那张图
        var fileTotal = curFileTotal;
        SetBar(fileBar, curFileDone, fileTotal);
        fileText.Text = fileTotal > 0
            ? $"单文件 {Pct(curFileDone, fileTotal)}（{curFileDone / 1048576.0:0.0}/{fileTotal / 1048576.0:0.0} MB）"
            : (curFileName.Length > 0 ? $"单文件 {curFileName}" : "单文件 —");

        // 单文件夹：所有正在下载的作品合计
        int workDone = 0, workTotal = 0;
        foreach (var kv in activeWorks) { workDone += kv.Value.Done; workTotal += kv.Value.Total; }
        SetBar(workBar, workDone, workTotal);
        workText.Text = workTotal > 0
            ? $"单文件夹 {Pct(workDone, workTotal)}（{workDone}/{workTotal} 张 · {activeWorks.Count} 部在下载）"
            : "单文件夹 —";

        // 总进度：整批作品
        SetBar(totalBar, totalWorksDone, totalWorksAll);
        totalText.Text = totalWorksAll > 0
            ? $"总进度 {Pct(totalWorksDone, totalWorksAll)}（{totalWorksDone}/{totalWorksAll} 部）"
            : "总进度 —";
    }

    private static void SetBar(ProgressBar bar, int done, int total) =>
        bar.Value = total <= 0 ? 0 : Math.Max(0, Math.Min(100, (int)(done * 100L / total)));

    private static string Pct(int done, int total) => total <= 0 ? "—" : $"{done * 100L / total}%";

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string? appName, string? idList);

    /// <summary>
    /// 进度条默认是系统浅色外观，在暗色界面里很扎眼。
    /// 关掉控件主题后 PBM_SETBKCOLOR / PBM_SETBARCOLOR 才生效，于是能跟随明暗配色。
    /// </summary>
    private void ThemeProgressBars()
    {
        foreach (var bar in new[] { fileBar, workBar, totalBar })
        {
            if (!bar.IsHandleCreated) continue;
            SetWindowTheme(bar.Handle, "", "");
            SendMessage(bar.Handle, 0x2001, IntPtr.Zero, (IntPtr)ColorRef(colInput));   // 背景
            SendMessage(bar.Handle, 0x0409, IntPtr.Zero, (IntPtr)ColorRef(colSelect));  // 进度色
            bar.Invalidate();
        }
    }

    /// <summary>下拉框要等句柄建好后再关主题，否则右侧箭头区域仍是系统浅色。</summary>
    private void ThemeComboBoxes()
    {
        foreach (var combo in new[] { namingMode, existingMode })
        {
            if (!combo.IsHandleCreated) continue;
            SetWindowTheme(combo.Handle, "", "");
            combo.Invalidate();
        }
    }

    private static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    /// <summary>任务开始/结束时统一重置三级进度。</summary>
    private void ResetProgress(int totalWorks)
    {
        curFileName = ""; curFileDone = 0; curFileTotal = 0;
        activeWorks.Clear();
        totalWorksDone = 0; totalWorksAll = totalWorks;
        RefreshProgress();
    }

    // ---------------- 下载 ----------------
    /// <summary>下载给定的一批作品（全部 / 本页 / 选中 / 序号范围都走这里）。</summary>
    private async Task DownloadWorksAsync(IEnumerable<Work> subset, CancellationToken ct)
    {
        var targets = subset.ToList();
        if (targets.Count == 0) throw new InvalidOperationException("没有可下载的作品：请先扫描收藏夹，或检查“序号范围 / 选中项”。");
        var outputRoot = ResolveOutputRoot();
        VerifyWritable(outputRoot);
        ResetProgress(targets.Count);
        progressTimer.Start();
        Log($"输出目录已确认可写：{outputRoot}");
        Log($"开始下载 {targets.Count} 部作品；命名方式 = {(Mode == NamingMode.ReverseNumber ? "倒序编号（最后一本为 1）" : "页-序号")}，作品并发 {(int)workConcurrency.Value}，图片全局并发上限 {(int)imageConcurrency.Value}。");

        using var slots = new SemaphoreSlim((int)workConcurrency.Value, (int)workConcurrency.Value);
        int done = 0;
        var tasks = targets.Select(async w =>
        {
            await slots.WaitAsync(ct);
            try
            {
                WaitWhilePaused(ct);
                await DownloadWorkAsync(w, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (SessionExpiredException ex)
            {
                SetStatus(w, "会话失效");
                Log($"作品 {w.Id} 失败：{ex.Message}");
                bool recovered = await HandleSessionExpiredAsync(ct);
                if (!recovered) { SetStatus(w, "会话失效"); return; }
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                // 会话恢复后自动把这部作品重试一次；仍失败就留给下次批量任务补。
                try
                {
                    WaitWhilePaused(ct);
                    SetStatus(w, "重试");
                    Log($"作品 {w.Id}：会话已恢复，重新开始这部作品。");
                    await DownloadWorkAsync(w, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (SessionExpiredException again)
                {
                    SetStatus(w, "会话失效");
                    Log($"作品 {w.Id} 再次遇到会话失效：{again.Message}（本次不再自动重试，请检查账号后手动继续）");
                }
                catch (Exception retryEx)
                {
                    SetStatus(w, "失败");
                    Log($"作品 {w.Id} 重试失败：{FullException(retryEx)}");
                }
            }
            catch (Exception ex) { SetStatus(w, "失败"); Log($"作品 {w.Id} 失败：{FullException(ex)}"); }
            finally
            {
                Interlocked.Increment(ref done);
                totalWorksDone = done;
                if (!ct.IsCancellationRequested) Ui(() => status.Text = $"作品 {done}/{targets.Count}");
                slots.Release();
            }
        }).ToArray();
        await Task.WhenAll(tasks);
        progressTimer.Stop();
        activeWorks.Clear();
        curFileName = ""; curFileDone = 0; curFileTotal = 0;
        RefreshProgress();
        Log($"批量任务结束：已处理 {done}/{targets.Count} 部。");
        ShowPage(currentPage);      // 刷新“已下载”标记
    }

    private async Task DownloadWorkAsync(Work w, CancellationToken ct, string? folderOverride = null)
    {
        var outputRoot = ResolveOutputRoot();
        SetStatus(w, "解析分页");
        Log($"解析作品：{w.Title} (ID={w.Id})");

        WaitWhilePaused(ct);
        var detail = await GetHtmlAsync(w.Url, null, ct);
        var detailDoc = Parse(detail.Html);
        var reader = detailDoc.DocumentNode.SelectNodes("//a[@href]")?
            .Select(a => Uri.TryCreate(new Uri(detail.FinalUrl), HtmlEntity.DeEntitize(a.GetAttributeValue("href", "")), out var u) ? u : null)
            .FirstOrDefault(u => u != null && (u.AbsolutePath.Contains("readOnline2.php", StringComparison.OrdinalIgnoreCase) || u.AbsolutePath.Contains("readOnline.php", StringComparison.OrdinalIgnoreCase)));
        if (reader == null) throw new InvalidOperationException("详情页中找不到 readOnline2.php/readOnline.php 链接。可能是登录失效或镜像结构不同。");

        // 详情页链接通常只是 readOnline2.php?ID=...&host_id=0；阅读器真正用 page 参数翻页，这里明确从 page=1 开始。
        var readerBuilder = new UriBuilder(reader);
        var readerQuery = HttpUtility.ParseQueryString(readerBuilder.Query);
        if (string.IsNullOrWhiteSpace(readerQuery["page"])) readerQuery["page"] = "1";
        if (string.IsNullOrWhiteSpace(readerQuery["gallery_brightness"])) readerQuery["gallery_brightness"] = "100";
        if (string.IsNullOrWhiteSpace(readerQuery["gallery_contrast"])) readerQuery["gallery_contrast"] = "100";
        readerBuilder.Query = readerQuery.ToString();

        var images = new SortedDictionary<int, (string Url, string Referer)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? current = readerBuilder.Uri.ToString();
        int fallback = 1;
        string lastHtml = "", lastUrl = current;

        while (!string.IsNullOrWhiteSpace(current) && visited.Count < 1000)
        {
            ct.ThrowIfCancellationRequested();
            if (!visited.Add(Normalize(current))) break;
            WaitWhilePaused(ct);
            var page = await GetHtmlAsync(current, detail.FinalUrl, ct);
            lastHtml = page.Html; lastUrl = page.FinalUrl;
            var doc = Parse(page.Html);
            int pageImages = 0;

            foreach (var img in doc.DocumentNode.SelectNodes("//img[contains(concat(' ',normalize-space(@class),' '),' online_read_image ')]") ?? Enumerable.Empty<HtmlNode>())
            {
                // 部分镜像把真实地址放在懒加载属性里，src 可能只是占位符甚至回指阅读器页面。
                Uri? imageUri = null;
                foreach (var attr in new[] { "data-src", "data-original", "data-lazy-src", "data-echo", "data-url", "data-zoom-image", "file", "original", "src" })
                {
                    var candidate = HtmlEntity.DeEntitize(img.GetAttributeValue(attr, "")).Trim();
                    if (candidate.Length == 0) continue;
                    if (Uri.TryCreate(new Uri(page.FinalUrl), candidate, out var u) && u.Scheme is "http" or "https"
                        && !u.AbsolutePath.Contains("readOnline", StringComparison.OrdinalIgnoreCase)) { imageUri = u; break; }
                }
                if (imageUri == null)
                {
                    var srcset = img.GetAttributeValue("srcset", "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.Trim().Split(' ')[0]).FirstOrDefault(x => x.Length > 0);
                    if (srcset != null && Uri.TryCreate(new Uri(page.FinalUrl), HtmlEntity.DeEntitize(srcset), out var setUri)
                        && setUri.Scheme is "http" or "https" && !setUri.AbsolutePath.Contains("readOnline", StringComparison.OrdinalIgnoreCase)) imageUri = setUri;
                }
                if (imageUri == null) continue;
                int pageNo = int.TryParse(img.GetAttributeValue("alt", ""), out int alt) && alt > 0 ? alt : fallback;
                fallback = Math.Max(fallback + 1, pageNo + 1);
                if (!images.ContainsKey(pageNo)) images[pageNo] = (imageUri.ToString(), page.FinalUrl);
                pageImages++;
            }

            // 该镜像把图片清单留在脚本变量里，运行 JavaScript 后才填进 img；直连下载按同样规则构造 CDN 地址。
            foreach (var scriptImage in ExtractScriptImages(page.Html, page.FinalUrl, fallback))
            {
                fallback = Math.Max(fallback + 1, scriptImage.Page + 1);
                if (!images.ContainsKey(scriptImage.Page)) { images[scriptImage.Page] = (scriptImage.Url, page.FinalUrl); pageImages++; }
            }
            Log($"作品 {w.Id}：阅读分页 {visited.Count}（page={HttpUtility.ParseQueryString(new Uri(page.FinalUrl).Query)["page"] ?? "?"}），本页 {pageImages} 张，累计 {images.Count} 张。");

            var anchors = doc.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>();
            var next = anchors.FirstOrDefault(a => a.GetAttributeValue("onclick", "").Contains("goNextPage", StringComparison.OrdinalIgnoreCase))
                       ?? anchors.FirstOrDefault(a => Regex.IsMatch(Clean(a.InnerText), @"下一页|下一頁|下頁|下页|next", RegexOptions.IgnoreCase));
            if (next is null) break;

            var currentUri = new Uri(page.FinalUrl);
            var currentQuery = HttpUtility.ParseQueryString(currentUri.Query);
            var href = HtmlEntity.DeEntitize(next.GetAttributeValue("href", "")).Trim();
            Uri? nextUri = null;
            if (href.Length > 0 && !href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate(currentUri, href, out var linked) && linked.Scheme is "http" or "https")
            {
                var linkedQuery = HttpUtility.ParseQueryString(linked.Query);
                if (!string.IsNullOrWhiteSpace(linkedQuery["page"]) && linkedQuery["page"] != currentQuery["page"]) nextUri = linked;
            }
            // 常见阅读器的 href 只是 # 或同一个 URL，实际翻页由 goNextPage() 改 page 查询参数。
            if (nextUri == null)
            {
                int currentPage = int.TryParse(currentQuery["page"], out var cp) ? cp : visited.Count;
                var b = new UriBuilder(currentUri);
                currentQuery["page"] = (currentPage + 1).ToString();
                b.Query = currentQuery.ToString();
                nextUri = b.Uri;
            }
            current = nextUri.ToString();
        }

        if (images.Count == 0) throw NoImagesException(lastHtml, lastUrl);
        await SaveWorkAsync(w, outputRoot, reader.ToString(), images, ct, folderOverride);
    }

    private static InvalidOperationException NoImagesException(string lastHtml, string lastUrl)
    {
        var doc = Parse(lastHtml);
        var allImages = (doc.DocumentNode.SelectNodes("//img") ?? Enumerable.Empty<HtmlNode>()).ToList();
        var onlineImages = allImages.Where(i => (" " + i.GetAttributeValue("class", "") + " ").Contains(" online_read_image ", StringComparison.OrdinalIgnoreCase)).ToList();
        var title = Clean(doc.DocumentNode.SelectSingleNode("//title")?.InnerText ?? "");
        var scripts = doc.DocumentNode.SelectNodes("//script") ?? Enumerable.Empty<HtmlNode>();
        var samples = string.Join(" | ", allImages.Take(8).Select(i => "class=" + i.GetAttributeValue("class", "") + "; src=" + i.GetAttributeValue("src", "") + "; data-src=" + i.GetAttributeValue("data-src", "")));
        var scriptSrcs = string.Join(" | ", scripts.Select(x => x.GetAttributeValue("src", "")).Where(x => !string.IsNullOrWhiteSpace(x)).Take(8));
        var hasRoot = Regex.IsMatch(lastHtml, @"\bHTTP_IMAGE\s*=", RegexOptions.IgnoreCase);
        var hasList = Regex.IsMatch(lastHtml, @"\bOriginal_Image_List\s*=", RegexOptions.IgnoreCase);
        var likelyChallenge = title.Length > 80 && !Regex.IsMatch(title, @"作品|阅读|閱讀|漫画|漫畫|gallery|read", RegexOptions.IgnoreCase);
        var diagnosis = likelyChallenge
            ? $"页面标题是异常长的随机字符串。脚本清单检测：HTTP_IMAGE={hasRoot}，Original_Image_List={hasList}。"
            : "阅读图片节点没有可用地址；可能是图片地址由 JavaScript 生成、外部脚本加载失败，或站点结构已变化。";
        return new InvalidOperationException($"阅读页中没有解析到有效图片 URL。诊断={diagnosis} 最终阅读 URL={lastUrl}; 页面标题={title}; img 总数={allImages.Count}; online_read_image 数={onlineImages.Count}; 图片样本={samples}; 外部脚本={scriptSrcs}");
    }

    private async Task SaveWorkAsync(Work w, string outputRoot, string readerUrl, SortedDictionary<int, (string Url, string Referer)> images, CancellationToken ct, string? folderOverride = null)
    {
        // folderOverride：从「检查所有文件」单独下载时沿用已有文件夹，避免命名方式不同导致新开目录。
        var folderName = string.IsNullOrWhiteSpace(folderOverride) ? BuildFolderName(w) : Path.GetFileName(folderOverride);
        string folder = Path.Combine(outputRoot, folderName);
        bool folderExisted = Directory.Exists(folder);
        EnsureDirectory(folder, outputRoot);
        activeWorks[w.Id] = (0, images.Count);      // 登记到“单文件夹”进度里
        var manifestPath = Path.Combine(folder, "manifest.json");

        // 识别文件要在“全部已存在，本次跳过”提前返回之前写，保证老作品也能补上。
        WriteWorkIdentity(folder, w, readerUrl, images.Count);

        // —— 1) 绕过日志的损坏文件夹检查：每次都直接读文件，不信任 manifest / 下载日志 ——
        FolderIntegrity.DeleteEmptyParts(folder);
        var report = FolderIntegrity.Check(folder, images.Keys);
        var history = new DownloadLog(folder);      // 断点续传的线索：逐张追加的下载日志

        if (folderExisted)
        {
            Log($"作品 {w.Id} 文件夹检查（直接读文件，不依赖日志）：{report.ValidPages.Count}/{images.Count} 张可用" +
                (report.DamagedPages.Count > 0 ? $"，损坏 {report.DamagedPages.Count} 张（{DescribePages(report.DamagedPages)}）" : "") +
                (report.MissingPages.Count > 0 ? $"，缺失 {report.MissingPages.Count} 张（{DescribePages(report.MissingPages)}）" : "") + "。");
            if (report.DamagedPages.Count > 0)
            {
                int removed = FolderIntegrity.DeleteDamaged(folder, report);
                Log($"作品 {w.Id}：已删除 {removed} 个损坏/不完整的图片文件，这些页会重新下载。");
            }
            foreach (var stray in report.StrayFiles.Take(6))
                Log($"作品 {w.Id} 文件夹内的额外文件：{stray}");
        }
        if (history.LoadedCount > 0)
            Log($"作品 {w.Id} 下载日志：读取 {history.LoadedCount} 条记录（成功 {history.OkCount} 条）→ 日志作为断点续传线索；文件是否可用仍以上面的实际检查为准。");

        var present = new List<int>(report.ValidPages);
        var missing = report.MissingPages.Concat(report.DamagedPages).OrderBy(p => p).ToList();

        if (folderExisted && present.Count > 0)
        {
            if (present.Count == images.Count) { SetStatus(w, "已完成"); Log($"作品 {w.Id}：{images.Count} 张全部可用，本次跳过。"); return; }
            var decided = await DecideExistingAsync(w, present.Count, images.Count, missing, ct);
            if (decided == ExistingMode.Ignore)
            {
                SetStatus(w, "已存在，忽略");
                Log($"作品 {w.Id}：按选择忽略，本次不下载（本地已有 {present.Count} 张）。");
                return;
            }
        }

        var manifest = new Manifest
        {
            WorkId = w.Id, Title = w.Title, Authors = w.Authors,
            FavoritePage = w.FavoritePage, FavoritePosition = w.FavoritePosition,
            WorkUrl = w.Url, ReaderUrl = readerUrl, ImageCount = images.Count, UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        };
        // 已存在且可用的先记进 manifest，本次不重复下载
        foreach (var page in present)
        {
            var local = ImageIntegrity.FindPageFile(folder, page);
            if (local is null) continue;            // 理论上不会发生：present 来自上面的文件检查
            manifest.Images.Add(new Entry { Page = page, Url = images[page].Url, File = Path.GetFileName(local), Status = history.IsOk(page) ? "skipped-existing(log-ok)" : "skipped-existing", Bytes = new FileInfo(local).Length });
        }
        int complete = present.Count, failed = 0, downloaded = 0;
        var pending = images.Where(kv => missing.Contains(kv.Key)).ToList();
        if (pending.Count > 0)
            Log($"作品 {w.Id}：日志/文件检查后仍需下载 {pending.Count} 张（{DescribePages(pending.Select(kv => kv.Key).ToList())}）。");

        // 同一作品共用一份“备用地址规则记忆”：某页主地址 404 后找到的可用规则，后续页优先使用。
        var urlFallback = new ImageUrlFallback();

        var tasks = pending.Select(async pair =>
        {
            ct.ThrowIfCancellationRequested();
            WaitWhilePaused(ct);
            Exception? last = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                await imageGate.WaitAsync(ct);
                try
                {
                    var (fileName, bytes, usedUrl) = await DownloadImageAsync(pair.Value.Url, pair.Value.Referer, folder, pair.Key, urlFallback, ct);
                    lock (manifest) manifest.Images.Add(new Entry { Page = pair.Key, Url = usedUrl, File = fileName, Status = "downloaded", Bytes = bytes });
                    history.MarkOk(pair.Key, usedUrl, fileName, bytes);   // 每成功一张就落盘，进程随时结束都能续
                    Interlocked.Increment(ref downloaded);
                    last = null;
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    last = ex;
                    if (attempt < 3)
                    {
                        Log($"作品 {w.Id} 第 {pair.Key} 页第 {attempt} 次失败，稍后重试：{ex.Message}");
                        await Task.Delay(500 * attempt, ct);
                    }
                }
                finally { imageGate.Release(); }
            }
            if (last != null)
            {
                Interlocked.Increment(ref failed);
                var failedName = Path.GetFileName(ImageIntegrity.PageFile(folder, pair.Key, ImageIntegrity.ExtensionFromUrl(pair.Value.Url)));
                lock (manifest) manifest.Images.Add(new Entry { Page = pair.Key, Url = pair.Value.Url, File = failedName, Status = "failed: " + FullException(last) });
                history.MarkFailed(pair.Key, pair.Value.Url, failedName, FullException(last));
                Log($"作品 {w.Id} 第 {pair.Key} 页失败：{FullException(last)}");
            }
            Interlocked.Increment(ref complete);
            activeWorks[w.Id] = (complete, images.Count);
            Ui(() => status.Text = $"{w.Id}: {complete}/{images.Count} 张");
        }).ToArray();

        await Task.WhenAll(tasks);
        activeWorks.TryRemove(w.Id, out _);         // 这部作品下完，从“单文件夹”进度里移除
        manifest.Images = manifest.Images.OrderBy(x => x.Page).ToList();
        manifest.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var manifestTemp = manifestPath + ".tmp";
        File.WriteAllText(manifestTemp, Pretty(JsonSerialize(manifest)), new UTF8Encoding(false));
        if (File.Exists(manifestPath)) File.Delete(manifestPath);
        File.Move(manifestTemp, manifestPath);
        SetStatus(w, failed > 0 ? $"失败 {failed} 张" : present.Count > 0 ? $"补全 {downloaded} 张" : "完成");
        Log($"作品完成：{w.Title}；图片 {images.Count} 张（本次下载 {downloaded} 张，已有 {present.Count} 张），失败 {failed} 张；{folder}");
    }

    /// <summary>
    /// 写入/刷新作品文件夹里的 work-id.json。
    /// 位置优先取本次收藏夹扫描列表（<see cref="works"/>）；从「检查所有文件」单独下载时列表里可能没有该作品，
    /// 这时只补空字段，避免旧的 manifest 位置覆盖已经同步过的识别文件。
    /// </summary>
    private void WriteWorkIdentity(string folder, Work w, string readerUrl, int imageCount)
    {
        var scanned = works.FirstOrDefault(x => string.Equals(x.Id, w.Id, StringComparison.OrdinalIgnoreCase));
        var current = scanned ?? w;
        int globalIndex = scanned is null ? 0 : works.IndexOf(scanned) + 1;
        int totalWorks = scanned is null ? 0 : works.Count;

        var identity = WorkIdentityStore.WriteDownload(
            folder, w.Id, current.Title, current.Authors,
            current.FavoritePage, current.FavoritePosition,
            globalIndex, totalWorks, w.Url, readerUrl, imageCount,
            positionIsCurrent: scanned is not null, out bool created);
        if (identity is null)
        {
            Log($"作品 {w.Id}：识别文件写入失败（{WorkIdentityStore.FileName}）。");
            return;
        }
        if (created)
            Log($"作品 {w.Id}：已写入识别文件 {WorkIdentityStore.FileName}（漫画编号 {identity.WorkId}，位置 {identity.PositionText}）。");
    }

    /// <summary>
    /// 下载单张图片。主地址 404 时会按 <see cref="ImageUrlFallback"/> 尝试备用地址
    /// （换宽度档 / 去尺寸取原图 / 去掉 /01/ 章节目录），成功后记住该规则供后续页优先使用。
    /// 返回最终文件名、字节数与实际使用的地址。
    /// </summary>
    private async Task<(string FileName, long Bytes, string UsedUrl)> DownloadImageAsync(string url, string referer, string folder, int page, ImageUrlFallback fallback, CancellationToken ct)
    {
        var candidates = fallback.Candidates(url);
        ImageNotFoundException? lastNotFound = null;
        foreach (var (candidateUrl, rule) in candidates)
        {
            try
            {
                var result = await DownloadImageOnceAsync(candidateUrl, referer, folder, page, ct);
                fallback.RecordSuccess(rule);
                if (!string.Equals(candidateUrl, url, StringComparison.OrdinalIgnoreCase))
                    Log($"第 {page:D4} 页：主地址不可用，已改用备用地址成功（{rule}）。");
                return (result.FileName, result.Bytes, candidateUrl);
            }
            catch (ImageNotFoundException ex)
            {
                lastNotFound = ex;
            }
        }
        throw lastNotFound ?? new HttpRequestException("没有可用的图片地址。");
    }

    /// <summary>
    /// 从单个 URL 下载：写 <c>.part</c>、支持 Range 续传、校验图片格式并改名。404/410 抛
    /// <see cref="ImageNotFoundException"/>，让上层尝试备用地址；其它错误原样抛出。
    /// </summary>
    private async Task<(string FileName, long Bytes)> DownloadImageOnceAsync(string url, string referer, string folder, int page, CancellationToken ct)
    {
        var expectedExtension = ImageIntegrity.ExtensionFromUrl(url);
        string part = ImageIntegrity.FindPartFile(folder, page) ?? ImageIntegrity.PartFile(folder, page, expectedExtension);
        long offset = 0;
        try { if (File.Exists(part)) offset = new FileInfo(part).Length; }
        catch { offset = 0; }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (Uri.TryCreate(referer, UriKind.Absolute, out var refererUri)) req.Headers.Referrer = refererUri;
        req.Headers.Accept.ParseAdd("image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");
        if (offset > 0) req.Headers.Range = new RangeHeaderValue(offset, null);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            throw new ImageNotFoundException($"图片 HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}; 地址={url}");
        bool append = offset > 0 && resp.StatusCode == HttpStatusCode.PartialContent;
        if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.PartialContent)
            throw new HttpRequestException($"图片 HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}; Content-Type={resp.Content.Headers.ContentType?.MediaType ?? "(none)"}; final={resp.RequestMessage?.RequestUri}");
        // 206 时核对服务器实际返回的起点：与本地 .part 长度不一致就不能拼接，否则会拼出坏图。
        if (append && resp.Content.Headers.ContentRange?.From is long from && from != offset) append = false;
        if (!append) offset = 0;

        var declared = resp.Content.Headers.ContentLength;
        long total = declared is > 0 ? declared.Value + offset : 0;
        curFileName = page.ToString("D4") + expectedExtension + "（" + (resp.Content.Headers.ContentType?.MediaType ?? "image") + "）";
        curFileTotal = total is > 0 and < int.MaxValue ? (int)total : 0;
        curFileDone = offset > int.MaxValue ? int.MaxValue : (int)offset;
        if (offset > 0) Log($"第 {page:D4} 页：从 {offset} 字节处续传（HTTP 206）。");

        using (var stream = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
        using (var source = await resp.Content.ReadAsStreamAsync())
        {
            var buffer = new byte[81920];
            int read;
            long received = offset;
            while ((read = await source.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                stream.Write(buffer, 0, read);
                received += read;
                curFileDone = received > int.MaxValue ? int.MaxValue : (int)received;
            }
            stream.Flush(true);
        }

        if (!ImageIntegrity.Validate(part, out var kind, out var length))
        {
            string hex = "";
            try
            {
                using var probe = File.OpenRead(part);
                var head = new byte[12];
                int n = probe.Read(head, 0, head.Length);
                hex = BitConverter.ToString(head, 0, n).Replace("-", " ");
            }
            catch { }
            try { File.Delete(part); } catch { }       // 内容无效，删掉，避免下次误续传
            throw new InvalidDataException($"下载内容不是完整的图片（支持 JPEG / PNG / GIF / WebP）；HTTP {(int)resp.StatusCode}; Content-Type={resp.Content.Headers.ContentType?.MediaType ?? "(none)"}; bytes={length}; 前12字节HEX={hex}; 最终URL={resp.RequestMessage?.RequestUri}");
        }

        // 实际格式与 URL 后缀不一致时（常见：URL 写 .jpg，内容其实是 GIF），以实际内容为准改名。
        // 扩展名本身一致时保留原后缀（例如 .jpeg 不会被改成 .jpg）。
        var finalExtension = ImageIntegrity.KindOfExtension(expectedExtension) == kind
            ? expectedExtension
            : ImageIntegrity.ExtensionOfKind(kind);
        var file = ImageIntegrity.PageFile(folder, page, finalExtension);
        if (File.Exists(file)) File.Delete(file);
        File.Move(part, file);
        if (!string.Equals(finalExtension, expectedExtension, StringComparison.OrdinalIgnoreCase))
            Log($"第 {page:D4} 页实际是 {ImageIntegrity.NameOfKind(kind)}，保存为 {Path.GetFileName(file)}。");
        return (Path.GetFileName(file), length);
    }

    // ---------------- HTTP 与解析 ----------------
    /// <summary>
    /// 取网页 HTML。默认会把「401/403 / 页面变成登录页」视为会话失效并抛
    /// <see cref="SessionExpiredException"/>，由下载任务统一暂停并重新验证会话；
    /// 登录流程本身需要看到登录页，所以 LoginAsync 调用时传 allowLoginPage: true。
    /// </summary>
    private async Task<(string Html, string FinalUrl)> GetHtmlAsync(string url, string? referer, CancellationToken ct, bool allowLoginPage = false)
    {
        using var response = await SendAsync(HttpMethod.Get, url, referer, null, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new SessionExpiredException($"请求 {(int)response.StatusCode} {response.ReasonPhrase}，可能是登录会话已过期。地址：{url}");
        var html = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();
        var final = response.RequestMessage?.RequestUri?.ToString() ?? url;
        if (response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == false && !html.Contains("<html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("服务器返回的内容不是 HTML。最终地址：" + final);
        if (!allowLoginPage && LooksLikeLoginPage(html, final))
            throw new SessionExpiredException("页面变成了登录页，登录会话可能已过期。最终地址：" + final);
        return (html, final);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? referer, HttpContent? content, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url) { Content = content };
        if (Uri.TryCreate(referer, UriKind.Absolute, out var r)) req.Headers.Referrer = r;
        try { return await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException ex) { throw new HttpRequestException($"请求失败：{url}{Environment.NewLine}底层异常：{FullException(ex)}。未禁用证书验证。", ex); }
    }

    /// <summary>脚本清单里的 CDN 根地址 + 文件名 + _w1100 + 扩展名，就是阅读器实际请求的图片地址。</summary>
    private static List<ScriptImage> ExtractScriptImages(string html, string readerUrl, int fallbackStart)
    {
        var result = new List<ScriptImage>();
        var rootMatch = Regex.Match(html, "\\bHTTP_IMAGE\\s*=\\s*['\\\"](?<url>[^'\\\"]+)['\\\"]", RegexOptions.IgnoreCase);
        if (!rootMatch.Success || !Uri.TryCreate(HtmlEntity.DeEntitize(rootMatch.Groups["url"].Value), UriKind.Absolute, out var imageRoot)) return result;
        var marker = Regex.Match(html, @"\bOriginal_Image_List\s*=\s*", RegexOptions.IgnoreCase);
        if (!marker.Success) return result;
        var json = ExtractJsonArray(html, marker.Index + marker.Length);
        if (json is null) return result;
        try
        {
            // 站点这里就是标准的 JSON 数组；用 .NET Framework 自带的 JavaScriptSerializer 解析，
            // 不再需要 System.Text.Json 或额外 JSON 库。
            var array = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as object[];
            if (array is null) return result;
            int fallback = fallbackStart;
            foreach (var raw in array)
            {
                if (raw is not IDictionary<string, object> item) continue;
                var filename = JsonText(item, "new_filename");
                var extension = JsonText(item, "extension");
                if (!Regex.IsMatch(filename, @"^[A-Za-z0-9._-]+$") || !Regex.IsMatch(extension, @"^[A-Za-z0-9]+$")) continue;
                var relativeFolder = JsonText(item, "ext_path_folder").Replace('\\', '/').Trim('/');
                if (relativeFolder.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).Any(x => x is "." or "..")) continue;
                var relative = (relativeFolder.Length == 0 ? "" : relativeFolder + "/") + filename;
                if (!filename.EndsWith("_w1100." + extension, StringComparison.OrdinalIgnoreCase)) relative += "_w1100." + extension;
                if (!Uri.TryCreate(imageRoot, relative, out var imageUri) || imageUri.Scheme is not ("http" or "https")) continue;
                int page = int.TryParse(JsonText(item, "sort"), out var sort) && sort >= 0 ? sort + 1 : fallback;
                fallback = Math.Max(fallback + 1, page + 1);
                result.Add(new ScriptImage(page, imageUri.ToString()));
            }
        }
        catch { }
        return result;
    }

    private static string JsonText(IDictionary<string, object> item, string name) =>
        item.TryGetValue(name, out var value) && value is string text ? text : (value is null ? "" : value.ToString() ?? "");

    /// <summary>从 text[start] 起取出一个完整的 JSON 数组（按括号配平，跳过字符串内的括号）。</summary>
    private static string? ExtractJsonArray(string text, int start)
    {
        int open = text.IndexOf('[', start);
        if (open < 0) return null;
        int depth = 0; bool quoted = false, escaped = false;
        for (int i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') quoted = false;
                continue;
            }
            if (c == '"') { quoted = true; continue; }
            if (c == '[') depth++;
            else if (c == ']' && --depth == 0) return text.Substring(open, i + 1 - open);
        }
        return null;
    }

    /// <summary>站点用逐页随机密钥的 AES-128-CBC(PKCS7) 加密标题与作者，密钥写在页面内联脚本里。</summary>
    private sealed class PageCipher
    {
        private readonly byte[]? key;
        private readonly byte[]? iv;
        public bool Enabled => key is not null && iv is not null;

        private PageCipher(byte[]? key, byte[]? iv) { this.key = key; this.iv = iv; }

        /// <summary>页面形如：var aek = '密钥'; var aei = '向量'; var enc = true;</summary>
        public static PageCipher FromHtml(string html)
        {
            var enc = Regex.Match(html, @"\benc\s*=\s*(?<v>true|false)", RegexOptions.IgnoreCase);
            if (enc.Success && enc.Groups["v"].Value.Equals("false", StringComparison.OrdinalIgnoreCase)) return new PageCipher(null, null);
            var keyText = Regex.Match(html, @"\baek\s*=\s*'(?<v>[^']*)'").Groups["v"].Value;
            var ivText = Regex.Match(html, @"\baei\s*=\s*'(?<v>[^']*)'").Groups["v"].Value;
            if (Encoding.UTF8.GetByteCount(ivText) != 16) return new PageCipher(null, null);
            if (Encoding.UTF8.GetByteCount(keyText) is not (16 or 24 or 32)) return new PageCipher(null, null);
            return new PageCipher(Encoding.UTF8.GetBytes(keyText), Encoding.UTF8.GetBytes(ivText));
        }

        /// <summary>解密成功先给明文，再给原文；解不开就只给原文。</summary>
        public IEnumerable<(string Text, bool FromCipher)> Decode(string? raw)
        {
            var value = (raw ?? "").Trim();
            if (value.Length == 0) yield break;
            if (Enabled)
            {
                var plain = TryDecrypt(value);
                if (plain is { Length: > 0 } && !string.Equals(plain, value, StringComparison.Ordinal)) yield return (plain, true);
            }
            yield return (value, false);
        }

        private string? TryDecrypt(string token)
        {
            if (token.Length < 24 || token.Length % 4 != 0 || !Regex.IsMatch(token, @"^[A-Za-z0-9+/]+={0,2}$")) return null;
            try
            {
                var data = Convert.FromBase64String(token);
                if (data.Length == 0 || data.Length % 16 != 0) return null;
                using var aes = Aes.Create();
                aes.Key = key!; aes.IV = iv!; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                using var decryptor = aes.CreateDecryptor();
                var text = Encoding.UTF8.GetString(decryptor.TransformFinalBlock(data, 0, data.Length));
                // 密钥不匹配时 PKCS7 去填充通常会抛异常；这里再挡一次「填充碰巧合法」的乱码。
                if (text.Length == 0 || text.Any(ch => ch == '\uFFFD' || (char.IsControl(ch) && ch is not ('\t' or '\n' or '\r')))) return null;
                return text;
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException) { return null; }
        }
    }

    // ---------------- 杂项 ----------------
    private static HtmlAgilityPack.HtmlDocument Parse(string html) { var d = new HtmlAgilityPack.HtmlDocument(); d.LoadHtml(html); return d; }
    private static bool HasPasswordForm(string html) => Regex.IsMatch(html, @"<input\b[^>]*type\s*=\s*(?:'password'|""password""|password)", RegexOptions.IgnoreCase);
    private static bool LooksLikeLoginPage(string html, string url) => url.Contains("route=account/login", StringComparison.OrdinalIgnoreCase) || (HasPasswordForm(html) && Regex.IsMatch(html, "login|登录|登入", RegexOptions.IgnoreCase));
    private static string Normalize(string s) { if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) return s; var b = new UriBuilder(u) { Fragment = "" }; return b.Uri.ToString().TrimEnd('?'); }
    private static string Clean(string s) => Regex.Replace(HtmlEntity.DeEntitize(s ?? ""), @"\s+", " ").Trim();

    private static string SafeName(string s, int maximumLength = 90)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        s = Regex.Replace(s, @"[\x00-\x1F]", "_").Trim().TrimEnd('.');
        if (s.Length > maximumLength) s = s.Substring(0, maximumLength);
        return string.IsNullOrWhiteSpace(s) ? "未命名作品" : s;
    }

    /// <summary>
    /// 目录名，两种命名方式：
    /// 页-序号：标题{作者}{收藏夹页,页内序号}，例如 a{b}{1,3}；
    /// 倒序编号：标题{作者}{N}，按扫描顺序（页序→页内序）倒过来数，最后一本为 1、第一本为总数量，数字按总位数补零便于排序。
    /// </summary>
    private string BuildFolderName(Work work)
    {
        var authors = string.Concat(work.Authors.Select(author => "{" + author + "}"));
        string suffix;
        if (Mode == NamingMode.ReverseNumber && works.Count > 0)
        {
            int index = works.IndexOf(work) + 1;
            var reverse = (works.Count - index + 1).ToString().PadLeft(works.Count.ToString().Length, '0');
            suffix = "{" + reverse + "}";
        }
        else suffix = $"{{{work.FavoritePage},{work.FavoritePosition}}}";
        var prefix = SafeName(work.Title + authors, Math.Max(1, 120 - suffix.Length));
        return prefix + suffix;
    }

    /// <summary>
    /// 「检查文件夹完整性」按钮：只读本地文件，不联网。用于确认已有文件夹是否真的完整，
    /// 面向的场景是：日志/manifest 说下完了，但文件被同步工具截断、被清理软件删掉，或者上次
    /// 下载失败留下了错误页。结果会逐页列在日志里；下次下载时会自动重下损坏/缺失的页。
    /// </summary>
    private async Task CheckFoldersAsync(CancellationToken ct)
    {
        var targets = SelectedWorks().ToList();
        if (targets.Count == 0) targets = CurrentPageWorks().ToList();
        if (targets.Count == 0) throw new InvalidOperationException("没有可检查的作品：请先扫描收藏夹，或选中要检查的作品。");
        var root = ResolveOutputRoot();
        Log($"开始检查 {targets.Count} 个作品文件夹的完整性（只读本地文件，不联网）。输出目录：{root}");

        int complete = 0, withProblems = 0;
        foreach (var w in targets)
        {
            ct.ThrowIfCancellationRequested();
            var folder = Path.Combine(root, BuildFolderName(w));
            if (!Directory.Exists(folder))
            {
                Log($"作品 {w.Id}：文件夹不存在（{folder}），视为尚未下载。");
                SetStatus(w, "待处理");
                continue;
            }

            var report = FolderIntegrity.Check(folder, ReadExpectedPages(folder, out var source));
            var history = new DownloadLog(folder);
            if (report.Expected == 0 && report.ValidPages.Count == 0 && report.DamagedPages.Count == 0)
            {
                Log($"作品 {w.Id}：文件夹里没有图片（{folder}），视为尚未下载。");
                SetStatus(w, "待处理");
                continue;
            }
            if (report.ProblemCount == 0 && report.StrayFiles.Count == 0)
            {
                complete++;
                SetStatus(w, "已完整");
                Log($"作品 {w.Id}：OK，{report.ValidPages.Count}/{report.Expected} 张通过校验（页码依据：{source}；日志 {history.OkCount} 条成功记录）。");
                continue;
            }

            withProblems++;
            SetStatus(w, $"异常 {report.ValidPages.Count}/{report.Expected}");
            Log($"作品 {w.Id}：需要补全——可用 {report.ValidPages.Count}/{report.Expected} 张（页码依据：{source}）" +
                (report.DamagedPages.Count > 0 ? $"，损坏 {report.DamagedPages.Count} 张（{DescribePages(report.DamagedPages)}）" : "") +
                (report.MissingPages.Count > 0 ? $"，缺失 {report.MissingPages.Count} 张（{DescribePages(report.MissingPages)}）" : "") + "。");
            foreach (var stray in report.StrayFiles.Take(8)) Log($"  额外文件：{stray}");
            if (report.DamagedPages.Count > 0)
                Log("  损坏文件不会被当作已完成；下次下载时会先删除它们再重新下载（本次仅检查，未改动文件）。");
            await Task.Yield();
        }

        Log($"文件夹完整性检查结束：{complete} 个作品完整，{withProblems} 个需要补全。下次对它们执行下载时会自动跳过可用页、只补下载损坏和缺失的页。");
    }

    // ---------------- 作品识别文件（work-id.json） ----------------
    /// <summary>把当前扫描列表复制成识别文件服务能用的快照（UI 线程调用，避免后台线程直接读 works）。</summary>
    private List<IdentityWorkInfo> SnapshotWorks()
    {
        var snapshot = new List<IdentityWorkInfo>(works.Count);
        for (int i = 0; i < works.Count; i++)
        {
            var w = works[i];
            string host = "";
            try { if (Uri.TryCreate(w.Url, UriKind.Absolute, out var uri)) host = uri.Host; } catch { }
            snapshot.Add(new IdentityWorkInfo
            {
                WorkId = w.Id,
                Title = w.Title,
                Authors = w.Authors,
                FavoritePage = w.FavoritePage,
                FavoritePosition = w.FavoritePosition,
                GlobalIndex = i + 1,
                TotalWorks = works.Count,
                WorkUrl = w.Url,
                SiteHost = host,
            });
        }
        return snapshot;
    }

    /// <summary>给已有作品文件夹补写 work-id.json（优先 manifest.json，必要时按目录后缀对照当前列表）。</summary>
    private async Task CreateIdentityFilesAsync(CancellationToken ct)
    {
        var root = ResolveOutputRoot();
        var snapshot = SnapshotWorks();
        Log($"开始补写作品识别文件（{WorkIdentityStore.FileName}）：扫描 {root}");
        var result = await Task.Run(() => WorkIdentityService.CreateMissing(root, snapshot, Log), ct);
        Log($"识别文件补写完成：新建 {result.Created} 个，已存在 {result.Existing} 个，跳过 {result.Skipped} 个。");
        status.Text = $"识别文件：新建 {result.Created} 个";
    }

    /// <summary>按漫画编号对照当前收藏夹扫描结果，更新识别文件里的页/页内序号/全局序号。</summary>
    private async Task UpdateIdentityPositionsAsync(CancellationToken ct)
    {
        var root = ResolveOutputRoot();
        if (works.Count == 0) throw new InvalidOperationException("请先扫描收藏夹，再对照更新识别文件里的收藏夹位置。");
        var snapshot = SnapshotWorks();
        Log($"开始对照识别文件位置：当前扫描列表共 {works.Count} 部作品（位置以此列表为准）。");
        var result = await Task.Run(() => WorkIdentityService.UpdatePositions(root, snapshot, Log), ct);
        Log($"识别文件位置对照完成：匹配 {result.Matched} 部（位置变化 {result.Updated} 部），不在收藏夹 {result.NotInFavorites} 部。");
        status.Text = $"位置对照：更新 {result.Updated} 部";
    }

    /// <summary>按更新后的识别文件，只替换作品文件夹末尾的 {页,序} / {倒序号}，保留标题与作者前缀。</summary>
    private async Task RenameIdentityFoldersAsync(CancellationToken ct)
    {
        var root = ResolveOutputRoot();
        bool reverse = Mode == NamingMode.ReverseNumber;
        var modeText = reverse ? "倒序编号（最后一本为 1）" : "页-序号（标题{作者}{页,序}）";
        var answer = MessageBox.Show(this,
            "将按当前命名方式「" + modeText + "」和每部作品文件夹内 work-id.json 记录的收藏夹位置，\n" +
            "重命名输出目录下作品文件夹末尾的序号。\n\n" +
            "· 建议先点「对照更新位置」；没有更新过位置的识别文件会被跳过。\n" +
            "· 只改末尾序号，标题与作者前缀保持不变。\n" +
            "· 同一次操作会先用临时名过渡，避免 1→2、2→3 这类互换时撞名。\n\n" +
            "是否继续？",
            "更新文件夹序号", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes)
        {
            Log("已取消更新文件夹序号。");
            return;
        }

        Log($"开始按识别文件更新文件夹序号：命名方式 = {modeText}；输出目录 = {root}");
        var result = await Task.Run(() => WorkIdentityService.RenameFolders(root, reverse, Log), ct);
        Log($"文件夹序号更新完成：重命名 {result.Renamed} 个，未变化 {result.Unchanged} 个，跳过 {result.Skipped} 个。");
        status.Text = $"文件夹序号：重命名 {result.Renamed} 个";
        ShowPage(currentPage);       // 按新目录名重新计算“已存在/已完整”状态
        if (incompleteWindow is { IsDisposed: false }) _ = incompleteWindow.ScanAsync();
    }

    /// <summary>「检查所有文件」：打开不完整文件夹窗口（非模态），并立即重新扫描。</summary>
    private void OpenIncompleteWindow()
    {
        try
        {
            var root = ResolveOutputRoot();
            if (incompleteWindow is { IsDisposed: false })
            {
                incompleteWindow.Activate();
                _ = incompleteWindow.ScanAsync();
                return;
            }
            incompleteWindow = new IncompleteWindow(root, DownloadIncompleteAsync, darkMode.Checked);
            incompleteWindow.FormClosed += (_, _) => incompleteWindow = null;
            incompleteWindow.Show(this);
            _ = incompleteWindow.ScanAsync();
        }
        catch (Exception ex)
        {
            Log("无法打开“检查所有文件”：" + FullException(ex));
        }
    }

    /// <summary>
    /// 从「检查所有文件」窗口单独下载：每项都写回原文件夹（folderOverride），只补缺失/损坏页；
    /// 这样即使当时用的是“倒序编号”，也不会因为重新计算目录名而新建错误的文件夹。
    /// </summary>
    private async Task DownloadIncompleteAsync(IReadOnlyList<IncompleteItem> selected)
    {
        if (selected.Count == 0) return;
        if (activeCts is not null)
        {
            Log("已有任务在运行：请先等待完成或点「停止」，再单独下载。");
            return;
        }
        await RunAsync(async ct =>
        {
            int done = 0, skipped = 0;
            foreach (var item in selected)
            {
                ct.ThrowIfCancellationRequested();
                WaitWhilePaused(ct);
                if (!item.CanDownload)
                {
                    skipped++;
                    Log($"跳过「{item.DisplayTitle}」：manifest.json 里没有作品地址，无法单独下载。");
                    continue;
                }
                var work = new Work(item.WorkId, item.Title, item.Authors, item.WorkUrl, item.FavoritePage, item.FavoritePosition);
                Log($"单独下载：{item.DisplayTitle}（文件夹 {item.FolderName}，{item.ProblemText}）");
                try
                {
                    await DownloadWorkAsync(work, ct, item.FolderName);
                }
                catch (SessionExpiredException ex)
                {
                    Log($"单独下载遇到会话失效：{ex.Message}");
                    if (!await HandleSessionExpiredAsync(ct)) throw;
                    await DownloadWorkAsync(work, ct, item.FolderName);
                }
                catch (Exception ex)
                {
                    Log($"单独下载「{item.DisplayTitle}」失败：{FullException(ex)}");
                }
                done++;
            }
            Log($"单独下载结束：处理 {done}/{selected.Count} 个作品，跳过 {skipped} 个。");
        });
    }

    /// <summary>
    /// 检查按钮不联网，无法解析阅读页，所以预期页数只能来自本地：
    /// manifest.json 的 ImageCount，或下载日志里出现过的页号；两者都没有时返回空集合，
    /// 由 FolderIntegrity 直接扫目录里的 jpg。
    /// </summary>
    private static List<int> ReadExpectedPages(string folder, out string source)
    {
        try
        {
            var manifest = Path.Combine(folder, "manifest.json");
            if (File.Exists(manifest))
            {
                var m = JsonDeserialize<Manifest>(File.ReadAllText(manifest));
                if (m?.ImageCount > 0)
                {
                    source = "manifest.json";
                    return Enumerable.Range(1, m.ImageCount).ToList();
                }
            }
        }
        catch { }

        var log = new DownloadLog(folder);
        if (log.LoadedCount > 0)
        {
            source = DownloadLog.FileName;
            return log.Entries.Keys.OrderBy(p => p).ToList();
        }

        source = "目录内现有 jpg";
        return new List<int>();
    }

    private static string DescribePages(List<int> pages)
    {
        var shown = string.Join(",", pages.Take(6).Select(p => p.ToString("D4")));
        return pages.Count > 6 ? shown + $" …（共 {pages.Count} 张）" : shown;
    }

    /// <summary>按设置决定“已存在的作品”是补全还是忽略；选「逐个询问我」时弹窗，可一次性应用到本次任务剩余作品。</summary>
    private async Task<ExistingMode> DecideExistingAsync(Work w, int have, int total, List<int> missing, CancellationToken ct)
    {
        var configured = (ExistingMode)Math.Max(0, Math.Min(2, existingMode.SelectedIndex));
        if (configured != ExistingMode.Ask) return configured;
        if (batchExistingChoice is { } decided) return decided;

        await promptLock.WaitAsync(ct);
        try
        {
            if (batchExistingChoice is { } already) return already;
            var answer = DialogResult.No;
            UiSync(() => answer = MessageBox.Show(this,
                $"作品「{w.Title}」的目标文件夹已存在：{have}/{total} 张可用，缺失 {missing.Count} 张（{DescribePages(missing)}）。\n\n" +
                "是(Y)：只补全缺失的图片\n" +
                "否(N)：忽略这个作品，本次不下载\n" +
                "取消：本次任务剩下的作品都按“补全”处理，不再询问",
                "已有文件夹检查", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question));
            var result = answer == DialogResult.No ? ExistingMode.Ignore : ExistingMode.Complete;
            if (answer == DialogResult.Cancel) batchExistingChoice = ExistingMode.Complete;
            Log($"作品 {w.Id}：手动选择「{(result == ExistingMode.Complete ? "补全缺失" : "忽略")}」。");
            return result;
        }
        finally { promptLock.Release(); }
    }

    private void UiSync(Action a) { if (IsDisposed) return; if (InvokeRequired) Invoke(a); else a(); }

    private Work? SelectedWork() => list.SelectedItems.Count == 0 ? null : list.SelectedItems[0].Tag as Work;

    private void SetStatus(Work w, string s) => Ui(() =>
    {
        foreach (ListViewItem i in list.Items)
            if (ReferenceEquals(i.Tag, w)) { i.SubItems[3].Text = s; break; }
    });

    private void Ui(Action a) { if (IsDisposed) return; if (InvokeRequired) BeginInvoke(a); else a(); }

    /// <summary>界面日志 + 输出目录下的 AHRI8Backup.log 双写；文件写失败绝不影响下载。</summary>
    private void Log(string s)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {s}";
        var path = sessionLogPath;
        if (path.Length > 0)
        {
            lock (logFileLock)
            {
                try { File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false)); }
                catch { }
            }
        }
        Ui(() => log.AppendText(line + Environment.NewLine));
    }

    /// <summary>给 JSON 加缩进：JavaScriptSerializer 只会输出紧凑格式，而 manifest/settings 希望人能直接读。</summary>
    private static string Pretty(string json) => JsonFormat.Pretty(json);

    private sealed record ScriptImage(int Page, string Url);
    private sealed record Work(string Id, string Title, List<string> Authors, string Url, int FavoritePage, int FavoritePosition);
    private sealed class RememberedSettings { public bool RememberText { get; set; } public bool RememberPassword { get; set; } public bool DarkMode { get; set; } = true; public int NamingMode { get; set; } public int ExistingMode { get; set; } public string? BaseUrl { get; set; } public string? FavoritesUrl { get; set; } public string? OutputDirectory { get; set; } public string? Username { get; set; } public string? ProtectedPassword { get; set; } }
    private sealed class Manifest { public string WorkId { get; set; } = ""; public string Title { get; set; } = ""; public List<string> Authors { get; set; } = new(); public int FavoritePage { get; set; } public int FavoritePosition { get; set; } public string WorkUrl { get; set; } = ""; public string ReaderUrl { get; set; } = ""; public int ImageCount { get; set; } public string UpdatedAt { get; set; } = ""; public List<Entry> Images { get; set; } = new(); }
    private sealed class Entry { public int Page { get; set; } public string Url { get; set; } = ""; public string File { get; set; } = ""; public string Status { get; set; } = ""; public long? Bytes { get; set; } }

    /// <summary>登录会话失效（HTTP 401/403，或请求被重定向到登录页）。下载任务捕获它后统一暂停并重新验证会话。</summary>
    private sealed class SessionExpiredException : Exception
    {
        public SessionExpiredException(string message) : base(message) { }
    }

    /// <summary>图片地址 404/410：主地址失效，DownloadImageAsync 会尝试 ImageUrlFallback 的备用地址。</summary>
    private sealed class ImageNotFoundException : HttpRequestException
    {
        public ImageNotFoundException(string message) : base(message) { }
    }
}
