using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AHRI8Backup;

/// <summary>
/// 「检查所有文件」窗口：列出输出目录下所有不完整的作品文件夹（缺页 / 坏图 / 未完成的 .part），
/// 结果保存在 <c>incomplete.json</c>，下次打开自动读回；选中的作品可以单独补下载。
/// </summary>
internal sealed class IncompleteWindow : Form
{
    private readonly string outputRoot;
    private readonly Func<IReadOnlyList<IncompleteItem>, Task> downloadAsync;
    private readonly bool dark;

    private readonly ListView list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = true,
        GridLines = false,
        HideSelection = false,
        BorderStyle = BorderStyle.None,
    };
    private readonly Label statusLabel = new() { Text = "尚未检查", AutoSize = true, Margin = new Padding(10, 10, 0, 0) };
    private readonly Button scanButton = new() { Text = "重新检查", AutoSize = true };
    private readonly Button downloadButton = new() { Text = "下载选中", AutoSize = true };
    private readonly Button folderButton = new() { Text = "打开文件夹", AutoSize = true };
    private readonly Button closeButton = new() { Text = "关闭", AutoSize = true };
    private readonly List<IncompleteItem> items = new();
    private bool scanning;

    public IncompleteWindow(string outputRoot, Func<IReadOnlyList<IncompleteItem>, Task> downloadAsync, bool dark)
    {
        this.outputRoot = outputRoot;
        this.downloadAsync = downloadAsync;
        this.dark = dark;

        Text = "检查所有文件 — AHRI8Backup";
        Width = 1180;
        Height = 640;
        MinimumSize = new Size(900, 480);
        StartPosition = FormStartPosition.CenterParent;
        Icon = AppIcon.Load();

        list.Columns.Add("作品", 280);
        list.Columns.Add("文件夹", 260);
        list.Columns.Add("可用/应有", 80);
        list.Columns.Add("损坏页", 100);
        list.Columns.Add("缺失页", 150);
        list.Columns.Add("检查时间", 140);
        list.Columns.Add("可下载", 70);
        list.DoubleClick += (_, _) => OpenSelectedFolder();
        list.SelectedIndexChanged += (_, _) => UpdateButtons();

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        buttons.Controls.Add(scanButton);
        buttons.Controls.Add(downloadButton);
        buttons.Controls.Add(folderButton);
        buttons.Controls.Add(closeButton);
        buttons.Controls.Add(statusLabel);

        var hint = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 34,
            Text = "扫描输出目录下的所有作品文件夹，重新验证每一张图片；结果保存到输出目录的 incomplete.json。选中一行可单独补下载（只补缺失/损坏的页）。",
            Padding = new Padding(8, 6, 8, 0),
        };

        Controls.Add(list);
        Controls.Add(buttons);
        Controls.Add(hint);

        scanButton.Click += async (_, _) => await ScanAsync();
        downloadButton.Click += async (_, _) => await DownloadSelectedAsync();
        folderButton.Click += (_, _) => OpenSelectedFolder();
        closeButton.Click += (_, _) => Close();

        ApplyTheme();
        LoadSaved();
    }

    /// <summary>扫描所有作品文件夹并保存结果。</summary>
    public async Task ScanAsync()
    {
        if (scanning) return;
        scanning = true;
        SetBusy(true);
        try
        {
            var root = outputRoot;
            var result = await Task.Run(() => IncompleteScanner.Scan(root));
            items.Clear();
            items.AddRange(result);
            IncompleteScanner.Save(root, items);
            FillList();
            statusLabel.Text = $"检查完成：{items.Count} 个作品不完整（结果已保存到 {IncompleteScanner.FileName}）";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "检查失败：" + ex.Message, "检查所有文件", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            statusLabel.Text = "检查失败：" + ex.Message;
        }
        finally
        {
            scanning = false;
            SetBusy(false);
        }
    }

    private void LoadSaved()
    {
        items.Clear();
        items.AddRange(IncompleteScanner.Load(outputRoot));
        FillList();
        if (items.Count > 0) statusLabel.Text = $"上次检查结果：{items.Count} 个作品不完整（点「重新检查」更新）";
    }

    private void FillList()
    {
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var item in items)
        {
            var row = new ListViewItem(item.DisplayTitle) { Tag = item };
            row.SubItems.Add(item.FolderName);
            row.SubItems.Add($"{item.Valid}/{item.Expected}");
            row.SubItems.Add(Describe(item.DamagedPages));
            row.SubItems.Add(Describe(item.MissingPages));
            row.SubItems.Add(item.CheckedAt);
            row.SubItems.Add(item.CanDownload ? "是" : "否（无地址）");
            list.Items.Add(row);
        }
        list.EndUpdate();
        UpdateButtons();
    }

    private static string Describe(List<int> pages)
    {
        if (pages.Count == 0) return "—";
        var shown = string.Join(",", pages.Take(6).Select(p => p.ToString("D4")));
        return pages.Count > 6 ? shown + $" …（共 {pages.Count}）" : shown;
    }

    private async Task DownloadSelectedAsync()
    {
        var selected = SelectedItems();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "请先选中要下载的作品。", "单独下载", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var downloadable = selected.Where(i => i.CanDownload).ToList();
        if (downloadable.Count == 0)
        {
            MessageBox.Show(this, "选中的作品在 manifest.json 里没有作品地址，无法单独下载。\n请在主界面重新扫描收藏夹后下载该作品。", "单独下载", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        await downloadAsync(downloadable);
        await ScanAsync();
    }

    private void OpenSelectedFolder()
    {
        var item = SelectedItems().FirstOrDefault();
        if (item is null) return;
        try
        {
            if (System.IO.Directory.Exists(item.FolderPath)) Process.Start("explorer.exe", "\"" + item.FolderPath + "\"");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "无法打开文件夹：" + ex.Message, "打开文件夹", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private List<IncompleteItem> SelectedItems()
    {
        var result = new List<IncompleteItem>();
        foreach (ListViewItem row in list.SelectedItems)
            if (row.Tag is IncompleteItem item) result.Add(item);
        return result;
    }

    private void UpdateButtons()
    {
        var selected = SelectedItems();
        downloadButton.Enabled = !scanning && selected.Count > 0;
        folderButton.Enabled = selected.Count == 1;
        scanButton.Enabled = !scanning;
    }

    private void SetBusy(bool busy)
    {
        scanButton.Enabled = !busy;
        downloadButton.Enabled = !busy && SelectedItems().Count > 0;
        folderButton.Enabled = !busy && SelectedItems().Count == 1;
        UseWaitCursor = busy;
    }

    private void ApplyTheme()
    {
        var form = dark ? Color.FromArgb(0x1E, 0x1E, 0x1E) : SystemColors.Control;
        var panel = dark ? Color.FromArgb(0x25, 0x25, 0x26) : SystemColors.Control;
        var input = dark ? Color.FromArgb(0x2D, 0x2D, 0x30) : SystemColors.Window;
        var text = dark ? Color.FromArgb(0xE6, 0xE6, 0xE6) : SystemColors.ControlText;
        var button = dark ? Color.FromArgb(0x3A, 0x3D, 0x41) : SystemColors.Control;
        var border = dark ? Color.FromArgb(0x55, 0x55, 0x5A) : SystemColors.ActiveBorder;

        BackColor = form;
        ForeColor = text;
        list.BackColor = input;
        list.ForeColor = text;
        foreach (Control control in Controls)
        {
            control.BackColor = control is FlowLayoutPanel ? panel : form;
            control.ForeColor = text;
            if (control is Button b)
            {
                b.FlatStyle = FlatStyle.Flat;
                b.BackColor = button;
                b.ForeColor = text;
                b.FlatAppearance.BorderColor = border;
                b.FlatAppearance.MouseOverBackColor = dark ? Color.FromArgb(0x09, 0x4E, 0x77) : SystemColors.Highlight;
            }
            else if (control is Label label)
            {
                label.BackColor = dark ? form : SystemColors.Control;
                label.ForeColor = text;
            }
            else if (control is FlowLayoutPanel flow)
            {
                flow.BackColor = panel;
            }
        }
    }
}
