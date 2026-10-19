using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace AHRI8Backup;

/// <summary>
/// 单文件发布的关键：HtmlAgilityPack 不再作为独立 DLL 放在 exe 旁边，
/// 而是嵌进 exe 资源，在第一次用到它之前挂 AssemblyResolve 从内存加载。
/// 这样整个程序只有 AHRI8Backup.exe 一个文件，也不会去写 %TEMP%。
/// </summary>
internal static class EmbeddedAssemblies
{
    private const string HtmlAgilityPackResource = "AHRI8Backup.Embedded.HtmlAgilityPack.dll";

    private static bool installed;

    public static void Install()
    {
        if (installed) return;
        installed = true;
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
    }

    private static Assembly? Resolve(object? sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name;
        if (!string.Equals(name, "HtmlAgilityPack", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var stream = typeof(EmbeddedAssemblies).Assembly.GetManifestResourceStream(HtmlAgilityPackResource);
            if (stream is null) return null;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return Assembly.Load(memory.ToArray());
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>窗口 / 任务栏图标：从 exe 内嵌的 app.ico 读取，运行时不需要额外的图片文件。</summary>
internal static class AppIcon
{
    private static Icon? cached;

    public static Icon Load()
    {
        if (cached is not null) return cached;
        try
        {
            using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("AHRI8Backup.Assets.app.ico");
            if (stream is not null) cached = new Icon(stream);
        }
        catch
        {
            // 图标读不到不影响主功能，退回系统默认图标。
        }
        return cached ??= SystemIcons.Application;
    }
}

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        try
        {
            // 必须在任何会加载 HtmlAgilityPack 的代码之前注册。
            EmbeddedAssemblies.Install();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ThreadException += (_, e) => ShowFatal(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => ShowFatal(e.ExceptionObject as Exception);
            Run();
        }
        catch (Exception ex)
        {
            try { System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AHRI8Backup-startup.log"), ex.ToString()); } catch { }
            // WinExe 没有控制台：把启动失败的原因用对话框显示出来，而不是静默退出。
            ShowFatal(ex);
        }
    }

    /// <summary>单独拆出来，保证 MainForm 的 JIT 发生在嵌入程序集注册之后。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run() => Application.Run(new MainForm());

    private static void ShowFatal(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AHRI8Backup-crash.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + ex + Environment.NewLine + Environment.NewLine);
        }
        catch { }
        try
        {
            MessageBox.Show(
                ex.GetType().Name + ": " + ex.Message + Environment.NewLine + Environment.NewLine +
                "如果程序仍然可以运行，请点「修复启动被拦」查看是否被安全软件拦截；否则把这段文字发给作者。",
                "AHRI8Backup 发生未处理的错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            // 错误处理本身失败时不再递归。
        }
    }
}
