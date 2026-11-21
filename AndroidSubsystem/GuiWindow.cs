using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace AndroidSubsystem;

/// <summary>
/// 图形界面主窗口（android.exe -gui）。
/// 外观按 Assets/skin2.png 复刻：浅色标题栏 + 左侧导航（版本 / 下载 / 删除 / 设置）+ 主区卡片列表 + 底部启动条。
/// </summary>
internal sealed class GuiWindow : Form
{
    // ---------------------------------------------------------------- 配色
    private static readonly Color ChromeColor = Color.FromArgb(247, 248, 249);
    private static readonly Color SideColor = Color.FromArgb(244, 246, 248);
    private static readonly Color PageColor = Color.White;
    private static readonly Color LineColor = Color.FromArgb(231, 233, 237);
    private static readonly Color InkColor = Color.FromArgb(28, 31, 35);
    private static readonly Color MutedColor = Color.FromArgb(133, 139, 148);
    private static readonly Color AccentColor = Color.FromArgb(37, 99, 235);
    private static readonly Color ChipColor = Color.FromArgb(242, 244, 247);
    private static readonly Color GreenColor = Color.FromArgb(34, 160, 96);
    private static readonly Color GreenBg = Color.FromArgb(233, 247, 238);
    private static readonly Color BlueBg = Color.FromArgb(233, 240, 254);
    private static readonly Color SoftBlue = Color.FromArgb(240, 245, 255);

    private readonly Settings _settings;
    private readonly AndroidSdk? _sdk;

    private readonly ChromeBar _chrome = new();
    private readonly Panel _side = new();
    private readonly Panel _page = new();
    private readonly Panel _head = new();
    private readonly Panel _foot = new();
    private readonly FlowLayoutPanel _list = new();
    private readonly List<SidebarItem> _nav = new();
    private readonly List<Card> _cards = new();
    private readonly TextBox _search = new();
    private readonly RoundedPanel _searchBox = new();
    private readonly RoundedPanel _filterBox = new();
    private readonly Label _filterText = new();
    private readonly RoundedPanel _pickBox = new();
    private readonly Label _pickCaption = new();
    private readonly Label _pickValue = new();
    private readonly RoundedButton _bigStart = new();
    private readonly TabLabel _tabInstalled = new();
    private readonly TabLabel _tabAvailable = new();
    private readonly Label _hint = new();

    private string _tab = "installed";   // installed / available
    private string _filter = "all";      // all / installed / available
    private AndroidVersion? _selected;
    private bool _dragging;
    private Point _dragStart;
    private bool _maximized;

    // 日志在旧版是窗口里的文本框；这里改成写文件 + 最近几行缓存
    private readonly List<string> _log = new();

    // 系统 DPI 越高，pt 字号在物理像素上越大；按 96/DPI 缩小字号，让窗口比例跟设计图一致
    private static float _typeScale = 1f;
    internal static Font F(string family, float size, FontStyle style = FontStyle.Regular)
        => new Font(family, Math.Max(6f, size * _typeScale), style);

    public GuiWindow(Settings settings)
    {
        _settings = settings;
        L.Current = L.Parse(settings.Language);
        _sdk = OpenSdk();

        var dpiScale = ScreenScale();
        _typeScale = 1f / dpiScale;
        var work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        var w = (int)((work.Width - 64) / dpiScale);
        var h = (int)Math.Min((work.Height - 64) / dpiScale, w * 0.58);
        ClientSize = new Size(Math.Max(1000, w), Math.Max(620, h));
        MinimumSize = new Size((int)(760 / dpiScale), (int)(520 / dpiScale));
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = PageColor;
        DoubleBuffered = true;
        Font = F("Microsoft YaHei UI", 9.5f);
        Text = "安卓虚拟机";
        KeyPreview = true;

        BuildChrome();
        BuildSide();
        BuildPage();
        BuildFoot();

        Resize += (_, _) => LayoutAll();
        Shown += (_, _) =>
        {
            ApplyRoundRegion();
            LayoutAll();
            RebuildCards();
            AutoCheckUpdate();
        };
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int index);

    /// <summary>屏幕缩放倍数（96 DPI 为 1.0）。</summary>
    private static float ScreenScale()
    {
        var hdc = GetDC(IntPtr.Zero);
        var dpi = GetDeviceCaps(hdc, 88); // LOGPIXELSX
        ReleaseDC(IntPtr.Zero, hdc);
        return (dpi > 0 ? dpi : 96) / 96f;
    }

    private AndroidSdk? OpenSdk()
    {
        var root = _settings.SdkRoot;
        return !string.IsNullOrWhiteSpace(root) && Directory.Exists(root) ? new AndroidSdk(root!) : null;
    }

    // ---------------------------------------------------------------- 标题栏

    private void BuildChrome()
    {
        _chrome.Height = 48;
        _chrome.BackColor = ChromeColor;
        Controls.Add(_chrome);
        _chrome.MouseDown += DragStart;
        _chrome.MouseMove += DragMove;
        _chrome.MouseUp += (_, _) => _dragging = false;
        _chrome.Resize += (_, _) => PlaceChrome();

        _hint.AutoSize = false;
        _hint.Text = "";
        _hint.Visible = false;
    }

    private void PlaceChrome()
    {
        foreach (Control c in _chrome.Controls) if (c is WindowButton) c.Top = 8;
        var right = _chrome.Width - 10;
        foreach (var button in _chrome.Controls.OfType<WindowButton>().OrderBy(b => b.Order))
        {
            button.Left = right - button.Width;
            right = button.Left - 2;
        }
    }

    // ---------------------------------------------------------------- 左侧栏

    private void BuildSide()
    {
        _side.BackColor = SideColor;
        Controls.Add(_side);
        _side.Paint += (_, e) => PaintSide(e.Graphics);

        var entries = new (string key, string glyph, string label)[]
        {
            ("versions", "\uE71D", L.T("nav.versions")),
            ("download", "\uE896", L.T("gui.download")),
            ("delete", "\uE74D", L.T("gui.delete")),
            ("settings", "\uE713", L.T("gui.settings")),
        };
        foreach (var entry in entries)
        {
            var item = new SidebarItem(entry.glyph, entry.label);
            item.Kind = entry.key;
            item.Click += (_, _) => OnNav(entry.key);
            _nav.Add(item);
            _side.Controls.Add(item);
        }
        _nav[0].Selected = true;
    }

    private void PaintSide(Graphics g)
    {
        // 底部「更好的安卓体验」卡片
        var card = new Rectangle(12, _side.Height - 96, _side.Width - 24, 76);
        using var path = Round(card, 10);
        using var fill = new SolidBrush(Color.FromArgb(236, 239, 243));
        g.FillPath(fill, path);

        using var small = F("Microsoft YaHei UI", 8.5f);
        using var bold = F("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
        g.DrawString(L.T("gui.better1"), small, new SolidBrush(MutedColor), card.X + 14, card.Y + 16);
        g.DrawString(L.T("gui.better2"), bold, new SolidBrush(InkColor), card.X + 14, card.Y + 38);
        using var linePen = new Pen(AccentColor, 2f);
        g.DrawLine(linePen, card.X + 14, card.Y + 62, card.X + 76, card.Y + 62);
    }

    private void OnNav(string kind)
    {
        foreach (var item in _nav) item.Selected = item.Kind == kind;
        _side.Invalidate();
        switch (kind)
        {
            case "settings":
                OpenSettings();
                break;
            case "download":
                _tab = "available";
                _filter = "all";
                RebuildCards();
                break;
            case "delete":
                _tab = "installed";
                _filter = "all";
                RebuildCards();
                break;
            default:
                _tab = "installed";
                _filter = "all";
                _filterText.Text = L.T("gui.filter.all");
                RebuildCards();
                break;
        }
        _tabInstalled.Active = _tab == "installed";
        _tabAvailable.Active = _tab == "available";
    }

    // ---------------------------------------------------------------- 主区

    private void BuildPage()
    {
        _page.BackColor = PageColor;
        _page.Paint += (_, e) => PaintHead(e.Graphics);
        Controls.Add(_page);

        _searchBox.Radius = 12;
        _searchBox.FillColor = Color.FromArgb(243, 245, 247);
        _searchBox.BorderColor = Color.Transparent;
        _search.Text = "";
        _search.BorderStyle = BorderStyle.None;
        _search.BackColor = Color.FromArgb(243, 245, 247);
        _search.ForeColor = InkColor;
        _search.Font = F("Microsoft YaHei UI", 10f);
        _search.PlaceholderText = L.T("gui.search");
        _search.TextChanged += (_, _) => RebuildCards();
        var searchIcon = new Label
        {
            Text = "\uE721",
            Font = F("Segoe MDL2 Assets", 11f),
            ForeColor = MutedColor,
            BackColor = Color.FromArgb(243, 245, 247),
            Location = new Point(16, 11),
            AutoSize = true,
        };
        _searchBox.Controls.Add(searchIcon);
        _searchBox.Controls.Add(_search);
        _page.Controls.Add(_searchBox);

        _filterBox.Radius = 12;
        _filterBox.FillColor = Color.White;
        _filterBox.BorderColor = LineColor;
        _filterBox.Cursor = Cursors.Hand;
        _filterText.Text = L.T("gui.filter.all");
        _filterText.AutoSize = false;
        _filterText.ForeColor = InkColor;
        _filterText.BackColor = Color.Transparent;
        _filterText.TextAlign = ContentAlignment.MiddleLeft;
        _filterBox.Controls.Add(_filterText);
        var filterChevron = new Label
        {
            Text = "\uE70D",
            Font = F("Segoe MDL2 Assets", 9f),
            ForeColor = MutedColor,
            BackColor = Color.White,
            Location = new Point(118, 12),
            AutoSize = true,
        };
        _filterBox.Controls.Add(filterChevron);
        _filterBox.Click += (_, _) => ShowFilterMenu();
        _filterText.Click += (_, _) => ShowFilterMenu();
        _page.Controls.Add(_filterBox);

        _tabInstalled.TextValue = L.T("gui.installed");
        _tabAvailable.TextValue = L.T("gui.available");
        _tabInstalled.Active = true;
        _tabInstalled.Click += (_, _) => SwitchTab("installed");
        _tabAvailable.Click += (_, _) => SwitchTab("available");
        _page.Controls.Add(_tabInstalled);
        _page.Controls.Add(_tabAvailable);

        _list.BackColor = PageColor;
        _list.FlowDirection = FlowDirection.TopDown;
        _list.WrapContents = false;
        _list.AutoScroll = true;
        _list.Padding = new Padding(0, 0, 0, 8);
        _list.Resize += (_, _) => ResizeCards();
        _page.Controls.Add(_list);

        AllowDrop = false;
        try
        {
            AllowDrop = true;
            DragEnter += (_, e) =>
            {
                if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
            };
            DragDrop += (_, e) =>
            {
                if (e.Data?.GetData(DataFormats.FileDrop) is string[] files)
                    foreach (var file in files) if (ApkInstaller.IsApk(file)) InstallApk(file);
            };
        }
        catch { }
    }

    private void SwitchTab(string tab)
    {
        _tab = tab;
        _filter = "all";
        _filterText.Text = L.T("gui.filter.all");
        _filterBox.Invalidate();
        _tabInstalled.Active = tab == "installed";
        _tabAvailable.Active = tab == "available";
        RebuildCards();
    }

    private void ShowFilterMenu()
    {
        var menu = new ContextMenuStrip { Font = F("Microsoft YaHei UI", 9.5f) };
        foreach (var (key, label) in new[]
        {
            ("all", L.T("gui.filter.all")),
            ("installed", L.T("gui.installed")),
            ("available", L.T("gui.available")),
        })
        {
            var item = new ToolStripMenuItem(label) { Checked = _filter == key };
            item.Click += (_, _) =>
            {
                _filter = key;
                _filterText.Text = label;
                _filterBox.Invalidate();
                RebuildCards();
            };
            menu.Items.Add(item);
        }
        menu.Show(_filterBox, new Point(0, _filterBox.Height + 4));
    }

    /// <summary>主区头部：标题、副标题、搜索框、下拉、推荐卡、两个 tab。搜索框和下拉是真实控件，其余自绘。</summary>
    private void PaintHead(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using var titleFont = F("Microsoft YaHei UI", 19f, FontStyle.Bold);
        using var subFont = F("Microsoft YaHei UI", 9f);
        using var titleBrush = new SolidBrush(InkColor);
        using var subBrush = new SolidBrush(MutedColor);

        g.DrawString("安卓虚拟机", titleFont, titleBrush, 28, 22);
        var subRect = new RectangleF(30, 58, Math.Max(120, _searchBox.Left - 52), 22);
        g.DrawString(L.T("gui.subtitle"), subFont, subBrush, subRect);

        // 推荐卡
        var rec = HeadRecommendRect();
        using var recPath = Round(rec, 12);
        using var recFill = new SolidBrush(SoftBlue);
        g.FillPath(recFill, recPath);
        using var bulbFont = F("Segoe MDL2 Assets", 14f);
        using var bulbBrush = new SolidBrush(AccentColor);
        g.DrawString("\uE82F", bulbFont, bulbBrush, rec.X + 16, rec.Y + 22);
        using var recTitle = F("Microsoft YaHei UI", 9f, FontStyle.Bold);
        using var recSub = F("Microsoft YaHei UI", 8.5f);
        g.DrawString(L.T("gui.recommend1"), recTitle, new SolidBrush(AccentColor), rec.X + 52, rec.Y + 14);
        g.DrawString(L.T("gui.recommend2"), recSub, subBrush, rec.X + 52, rec.Y + 38);
        using var chev = F("Segoe MDL2 Assets", 9f);
        g.DrawString("\uE76C", chev, new SolidBrush(MutedColor), rec.Right - 26, rec.Y + 24);

        // tab 下划线
        var underlineY = _tabInstalled.Top + _tabInstalled.Height - 4;
        var active = _tab == "installed" ? _tabInstalled : _tabAvailable;
        using var pen = new Pen(AccentColor, 2.4f);
        g.DrawLine(pen, active.Left, underlineY, active.Right, underlineY);
    }

    private Rectangle HeadRecommendRect()
    {
        var w = 320;
        return new Rectangle(_page.Width - 28 - w, 18, w, 66);
    }

    // ---------------------------------------------------------------- 底部条

    private void BuildFoot()
    {
        _foot.BackColor = PageColor;
        _foot.Paint += (_, e) => PaintFoot(e.Graphics);
        Controls.Add(_foot);

        _pickBox.Radius = 12;
        _pickBox.FillColor = Color.FromArgb(246, 247, 249);
        _pickBox.BorderColor = LineColor;
        _pickBox.Cursor = Cursors.Hand;
        _pickBox.Click += (_, _) => StartSelected();

        _pickCaption.Text = L.T("gui.pick");
        _pickCaption.ForeColor = MutedColor;
        _pickCaption.Font = F("Microsoft YaHei UI", 8.5f);
        _pickCaption.AutoSize = false;
        _pickCaption.BackColor = Color.Transparent;
        _pickCaption.TextAlign = ContentAlignment.MiddleLeft;
        _pickCaption.Click += (_, _) => StartSelected();

        _pickValue.Text = L.T("gui.pick2");
        _pickValue.ForeColor = InkColor;
        _pickValue.Font = F("Microsoft YaHei UI", 10f, FontStyle.Bold);
        _pickValue.AutoSize = false;
        _pickValue.BackColor = Color.Transparent;
        _pickValue.TextAlign = ContentAlignment.MiddleLeft;
        _pickValue.Click += (_, _) => StartSelected();

        _pickBox.Controls.Add(_pickCaption);
        _pickBox.Controls.Add(_pickValue);
        _foot.Controls.Add(_pickBox);

        _bigStart.Text = L.T("gui.start");
        _bigStart.Radius = 12;
        _bigStart.Fill = AccentColor;
        _bigStart.Hover = Color.FromArgb(30, 86, 210);
        _bigStart.Fore = Color.White;
        _bigStart.Bold = true;
        _bigStart.Glyph = "\uE768";
        _bigStart.Click += (_, _) => StartSelected();
        _foot.Controls.Add(_bigStart);
    }

    private void PaintFoot(Graphics g)
    {
        using var pen = new Pen(LineColor, 1f);
        g.DrawLine(pen, 0, 0, _foot.Width, 0);

        using var glyphFont = F("Segoe MDL2 Assets", 12f);
        using var textFont = F("Microsoft YaHei UI", 10f);
        using var muted = new SolidBrush(MutedColor);
        g.DrawString("\uE8B7", glyphFont, muted, 30, _foot.Height / 2 - 12);
        g.DrawString(L.T("gui.files"), textFont, muted, 58, _foot.Height / 2 - 11);

        var x = 58 + (int)g.MeasureString(L.T("gui.files"), textFont).Width + 24;
        g.DrawString(L.T("gui.manage"), textFont, muted, x, _foot.Height / 2 - 11);
        using var chev = F("Segoe MDL2 Assets", 9f);
        g.DrawString("\uE76C", chev, muted, x + (int)g.MeasureString(L.T("gui.manage"), textFont).Width + 4, _foot.Height / 2 - 9);
    }

    // ---------------------------------------------------------------- 卡片列表

    private bool IsInstalled(AndroidVersion version)
    {
        if (_sdk == null) return false;
        try
        {
            var image = _sdk.FindLocalImage(version.ApiLevel);
            return image != null && _sdk.IsImageInstalled(image);
        }
        catch { return false; }
    }

    private List<AndroidVersion> VisibleVersions()
    {
        var query = ( _search.Text ?? "").Trim().ToLowerInvariant();
        return Catalog.Versions
            .Where(v => _filter switch
            {
                "installed" => IsInstalled(v),
                "available" => !IsInstalled(v),
                _ => true,
            })
            .Where(v => query.Length == 0 || v.Label.ToLowerInvariant().Contains(query) || v.Key.Contains(query))
            .OrderByDescending(v => v.ApiLevel)
            .ToList();
    }

    private void RebuildCards()
    {
        _tabInstalled.Count = Catalog.Versions.Count(IsInstalled);
        _tabAvailable.Count = Catalog.Versions.Count(v => !IsInstalled(v));
        _tabInstalled.Number = true;
        _tabAvailable.Number = true;
        _tabInstalled.Invalidate();
        _tabAvailable.Invalidate();

        _list.SuspendLayout();
        foreach (var card in _cards) card.Dispose();
        _cards.Clear();
        _list.Controls.Clear();

        foreach (var version in VisibleVersions())
        {
            var card = new Card(version, IsInstalled(version), _list.ClientSize.Width - 26);
            card.Handlers(
                start: () => StartVersion(version),
                download: () =>
                {
                    var tag = AskMirrorTag(version);
                    if (tag != null) RunHidden("-download " + version.Key + " " + tag);
                },
                more: () => ShowMoreMenu(version, card));
            card.BodyClick += (_, _) => Select(version, card);
            _cards.Add(card);
            _list.Controls.Add(card);
        }
        _list.ResumeLayout();
        if (_cards.Count == 0) AppendLog("列表为空：当前筛选下没有版本。");
    }

    private void Select(AndroidVersion version, Card card)
    {
        _selected = version;
        foreach (var c in _cards) c.Selected = c == card;
        _pickCaption.Text = L.T("gui.pick");
        _pickValue.Text = version.Label;
    }

    private void StartSelected()
    {
        if (_selected == null)
        {
            MessageBox.Show(L.T("gui.pickfirst"), "安卓虚拟机", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        StartVersion(_selected);
    }

    private void ShowMoreMenu(AndroidVersion version, Card card)
    {
        var menu = new ContextMenuStrip { Font = F("Microsoft YaHei UI", 9.5f) };
        var start = new ToolStripMenuItem(L.T("gui.start"), null, (_, _) => StartVersion(version));
        var del = new ToolStripMenuItem(L.T("gui.delete"), null, (_, _) => ConfirmDelete(version));
        var folder = new ToolStripMenuItem("打开模拟器目录", null, (_, _) => OpenAvdFolder());
        var log = new ToolStripMenuItem("查看日志", null, (_, _) => ShowLog());
        menu.Items.AddRange(new ToolStripItem[] { start, del, folder, log });
        menu.Show(card, new Point(card.Width - 210, 36));
    }

    private void OpenAvdFolder()
    {
        var dir = Path.Combine(_settings.SdkRoot ?? "", "avd");
        if (!Directory.Exists(dir)) dir = _settings.SdkRoot ?? "";
        try { Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "\"" + dir + "\"", UseShellExecute = true }); }
        catch (Exception ex) { AppendLog(ex.Message); }
    }

    private void ShowLog()
    {
        var text = string.Join(Environment.NewLine, _log.TakeLast(60));
        MessageBox.Show(text.Length == 0 ? "（暂无日志）" : text, "运行日志", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ResizeCards()
    {
        var width = Math.Max(600, _list.ClientSize.Width - 26 - SystemInformation.VerticalScrollBarWidth);
        foreach (var card in _cards) card.Width = width;
    }

    // ---------------------------------------------------------------- 布局

    private void LayoutAll()
    {
        var width = ClientSize.Width;
        var height = ClientSize.Height;
        var sideWidth = Math.Max(140, Math.Min(168, width / 6));
        var footHeight = 84;

        _chrome.Bounds = new Rectangle(0, 0, width, 48);
        _side.Bounds = new Rectangle(0, 48, sideWidth, height - 48);
        _page.Bounds = new Rectangle(sideWidth, 48, width - sideWidth, height - 48 - footHeight);
        _foot.Bounds = new Rectangle(sideWidth, height - footHeight, width - sideWidth, footHeight);

        var pad = 12;
        var top = 14;
        foreach (var item in _nav)
        {
            item.Bounds = new Rectangle(pad, top, sideWidth - pad * 2, 44);
            top += 52;
        }

        var pageWidth = _page.Width;

        // 搜索框 / 下拉 / 推荐卡 一行
        var rec = HeadRecommendRect();
        var boxTop = 28;
        var boxHeight = 40;
        var filterW = 150;
        var searchW = Math.Max(220, Math.Min(330, pageWidth / 3));
        _searchBox.Bounds = new Rectangle(rec.X - 12 - filterW - 12 - searchW, boxTop, searchW, boxHeight);
        _filterBox.Bounds = new Rectangle(rec.X - 12 - filterW, boxTop, filterW, boxHeight);
        _search.Bounds = new Rectangle(42, 9, searchW - 56, 24);
        _filterText.Bounds = new Rectangle(16, 0, filterW - 46, boxHeight);
        if (pageWidth < 900)
        {
            // 窄窗时推荐卡占位，搜索框仍贴右
            _filterBox.Bounds = new Rectangle(pageWidth - 28 - filterW, boxTop, filterW, boxHeight);
            _searchBox.Bounds = new Rectangle(_filterBox.Left - 12 - searchW, boxTop, searchW, boxHeight);
        }

        var tabTop = 124;
        _tabInstalled.Bounds = new Rectangle(28, tabTop, 116, 34);
        _tabAvailable.Bounds = new Rectangle(28 + 116 + 26, tabTop, 116, 34);

        _list.Bounds = new Rectangle(16, tabTop + 44, pageWidth - 32, Math.Max(80, _page.Height - tabTop - 44 - 12));

        // 底部条
        var footH = _foot.Height;
        var buttonW = 190;
        var buttonH = 56;
        var pickW = 200;
        _bigStart.Bounds = new Rectangle(_foot.Width - 28 - buttonW, (footH - buttonH) / 2 + 2, buttonW, buttonH);
        _pickBox.Bounds = new Rectangle(_bigStart.Left - 12 - pickW, (footH - buttonH) / 2 + 2, pickW, buttonH);
        _pickCaption.Bounds = new Rectangle(16, 8, pickW - 32, 20);
        _pickValue.Bounds = new Rectangle(16, 30, pickW - 32, 20);
    }

    private void ApplyRoundRegion()
    {
        if (_maximized) { Region = null; return; }
        using var path = Round(new Rectangle(0, 0, Width, Height), 12);
        Region = new Region(path);
    }

    // ---------------------------------------------------------------- 窗口拖动与按钮

    private void DragStart(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        _dragStart = e.Location;
    }

    private void DragMove(object? sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        Location = new Point(Location.X + e.X - _dragStart.X, Location.Y + e.Y - _dragStart.Y);
    }

    internal void ToggleMaximizeFromChrome()
    {
        ToggleMaximize();
    }

    private void ToggleMaximize()
    {
        _maximized = !_maximized;
        if (_maximized)
        {
            WindowState = FormWindowState.Maximized;
        }
        else
        {
            WindowState = FormWindowState.Normal;
        }
        ApplyRoundRegion();
        LayoutAll();
    }

    // ---------------------------------------------------------------- 动作（与旧版一致）

    private void RunHidden(string arguments, string? stdIn = null)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;

        AppendLog("> android.exe " + arguments);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (e.Data != null) AppendLog(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) AppendLog("[err] " + e.Data); };
            process.Exited += (_, _) =>
            {
                AppendLog("— 结束：" + arguments);
                BeginInvoke(new Action(RebuildCards));
            };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (stdIn != null)
            {
                try { process.StandardInput.Write(stdIn); process.StandardInput.Flush(); } catch { }
            }
            try { process.StandardInput.Close(); } catch { }
        }
        catch (Exception ex)
        {
            AppendLog("启动失败：" + ex.Message);
        }
    }

    /// <summary>启动某个版本：直接调用官方 emulator，窗口由 Android Studio 原生 emulator 自己弹出。</summary>
    private void StartVersion(AndroidVersion version)
    {
        RunHidden("-start " + version.Key);
    }

    private string? AskMirrorTag(AndroidVersion version)
    {
        using var dialog = new Form
        {
            Text = L.T("gui.download") + " · " + version.Label,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ClientSize = new Size(440, 210),
            BackColor = Color.FromArgb(248, 249, 252),
            Font = F("Microsoft YaHei UI", 9.5f),
        };
        var title = new Label
        {
            Text = "选择镜像类型（均来自 Android Studio 官方仓库）",
            Font = F("Microsoft YaHei UI", 10.5f, FontStyle.Bold),
            Location = new Point(20, 16),
            AutoSize = true,
        };
        var r1 = new RadioButton
        {
            Text = "1. google_apis（无 Play 商店，可自由装 APK）",
            Location = new Point(22, 54),
            AutoSize = true,
            Checked = true,
        };
        var r2 = new RadioButton
        {
            Text = "2. google_apis_playstore（内置 Google Play 商店）",
            Location = new Point(22, 86),
            AutoSize = true,
        };
        var ok = new Button
        {
            Text = L.T("common.ok"),
            DialogResult = DialogResult.OK,
            Size = new Size(88, 32),
            Location = new Point(240, 156),
            FlatStyle = FlatStyle.Flat,
            BackColor = AccentColor,
            ForeColor = Color.White,
            FlatAppearance = { BorderSize = 0 },
        };
        var cancel = new Button
        {
            Text = L.T("common.cancel"),
            DialogResult = DialogResult.Cancel,
            Size = new Size(88, 32),
            Location = new Point(336, 156),
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0 },
        };
        dialog.Controls.Add(title);
        dialog.Controls.Add(r1);
        dialog.Controls.Add(r2);
        dialog.Controls.Add(ok);
        dialog.Controls.Add(cancel);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;

        return dialog.ShowDialog(this) == DialogResult.OK ? (r2.Checked ? "2" : "1") : null;
    }

    private void ConfirmDelete(AndroidVersion version)
    {
        var answer = MessageBox.Show(
            $"删除 {version.Label} 的镜像与虚拟机数据？删除后需要重新下载（约 1–2 GB）。",
            "安卓虚拟机", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (answer == DialogResult.OK) RunHidden("-del " + version.Key, "y\n");
    }

    private void OpenSettings()
    {
        var langBefore = _settings.Language;
        using (var window = new SettingsWindow(_settings))
            window.ShowDialog(this);

        L.Current = L.Parse(_settings.Language);
        if (_settings.Language != langBefore)
        {
            var next = new GuiWindow(_settings) { StartPosition = FormStartPosition.Manual, Location = Location };
            next.Show();
            Close();
        }
        else
        {
            RebuildCards();
        }
    }

    private string? DeviceSerial()
    {
        var adb = ApkInstaller.AdbExe(_settings);
        if (adb == null) return null;
        var result = ProcessRunner.Run(adb, new[] { "devices" });
        foreach (var line in result.Combined.Split('\n'))
        {
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith("List of devices")) continue;
            return text.Split('\t', ' ')[0];
        }
        return null;
    }

    private void SendKey(string keyCode)
    {
        var adb = ApkInstaller.AdbExe(_settings);
        var serial = DeviceSerial();
        if (adb == null || serial == null)
        {
            AppendLog("没有可用的模拟器，先启动一个版本。");
            return;
        }
        var result = ProcessRunner.Run(adb, new[] { "-s", serial, "shell", "input", "keyevent", keyCode });
        AppendLog("keyevent " + keyCode + "：" + (result.Success ? "已发送" : result.Combined));
    }

    private void TakeScreenshot()
    {
        var adb = ApkInstaller.AdbExe(_settings);
        var serial = DeviceSerial();
        if (adb == null || serial == null)
        {
            AppendLog("没有可用的模拟器，先启动一个版本。");
            return;
        }
        var dir = Path.Combine(Path.GetTempPath(), "AndroidSubsystem");
        Directory.CreateDirectory(dir);
        var remote = "/sdcard/as_shot.png";
        var local = Path.Combine(dir, $"shot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        ProcessRunner.Run(adb, new[] { "-s", serial, "shell", "screencap", "-p", remote });
        var pull = ProcessRunner.Run(adb, new[] { "-s", serial, "pull", remote, local });
        AppendLog(File.Exists(local) ? "截图已保存：" + local : "截图失败：" + pull.Combined);
    }

    private void PickAndInstallApk()
    {
        using var dialog = new OpenFileDialog { Filter = "APK (*.apk)|*.apk", Title = L.T("gui.install") };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        InstallApk(dialog.FileName);
    }

    private void InstallApk(string path)
    {
        _pickValue.Text = Path.GetFileName(path);
        AppendLog(L.T("skin.installing") + " " + Path.GetFileName(path));
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            var result = await ApkInstaller.InstallAsync(path, _settings);
            if (!string.IsNullOrWhiteSpace(result.log)) AppendLog(result.log.Trim());
            AppendLog(result.message);
            try { BeginInvoke(new Action(() => _pickValue.Text = result.message)); } catch { }
        });
    }

    private void AutoCheckUpdate()
    {
        if (!_settings.AutoUpdate) return;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var (cmp, tag, _) = await Updater.PeekAsync();
                if (cmp <= 0) return;
                BeginInvoke(new Action(() =>
                {
                    var answer = MessageBox.Show(
                        $"发现新版本 {tag}，是否现在更新？",
                        "安卓虚拟机", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                    if (answer == DialogResult.Yes) LaunchConsole("-update");
                }));
            }
            catch { /* 检查失败不打扰 */ }
        });
    }

    private static void LaunchConsole(string arguments)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = exe, Arguments = arguments, UseShellExecute = true });
        }
        catch { }
    }

    private void AppendLog(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        lock (_log)
        {
            _log.Add(line);
            if (_log.Count > 400) _log.RemoveRange(0, _log.Count - 400);
        }
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "android_gui.log"),
                line + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }

    // ---------------------------------------------------------------- 自绘小部件

    internal static GraphicsPath Round(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        if (radius <= 0) { path.AddRectangle(rect); return path; }
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>自绘圆角面板。</summary>
    internal class RoundedPanel : Panel
    {
        public int Radius = 12;
        public Color FillColor = Color.White;
        public Color BorderColor = Color.Transparent;
        public int BorderWidth = 1;

        public RoundedPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = Round(rect, Radius);
            using var fill = new SolidBrush(FillColor);
            g.FillPath(fill, path);
            if (BorderColor.A > 0)
            {
                using var pen = new Pen(BorderColor, BorderWidth);
                g.DrawPath(pen, path);
            }
        }
    }

    /// <summary>标题栏（圆点 + 机器人 + 标题 + 三个窗口按钮）。</summary>
    internal sealed class ChromeBar : Panel
    {
        private readonly WindowButton _min = new("\uE921", 0, () => { });
        private readonly WindowButton _max = new("\uE922", 1, () => { });
        private readonly WindowButton _close = new("\uE8BB", 2, () => { });

        public ChromeBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            _close.Danger = true;
            _min.Click += (_, _) => FindForm()!.WindowState = FormWindowState.Minimized;
            _max.Click += (_, _) => (FindForm() as GuiWindow)?.ToggleMaximizeFromChrome();
            _close.Click += (_, _) => FindForm()!.Close();
            Controls.Add(_min);
            Controls.Add(_max);
            Controls.Add(_close);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            foreach (var (x, color) in new[]
            {
                (20, Color.FromArgb(255, 95, 87)),
                (38, Color.FromArgb(254, 188, 46)),
                (56, Color.FromArgb(40, 200, 64)),
            })
            {
                using var brush = new SolidBrush(color);
                g.FillEllipse(brush, x - 6, 18, 12, 12);
            }

            DrawRobot(g, 78, 12, 22, Color.FromArgb(52, 191, 118));

            using var font = F("Microsoft YaHei UI", 11f, FontStyle.Bold);
            g.DrawString("安卓虚拟机", font, new SolidBrush(InkColor), 106, 15);
        }

        internal static void DrawRobot(Graphics g, int x, int y, int size, Color color)
        {
            using var brush = new SolidBrush(color);
            var head = new Rectangle(x, y + size / 4, size, size / 2);
            using var path = Round(head, size / 4);
            g.FillPath(brush, path);
            // 天线
            using var pen = new Pen(color, 1.6f);
            g.DrawLine(pen, x + size / 4, y + size / 4, x + size / 4, y + 2);
            g.DrawLine(pen, x + size * 3 / 4, y + size / 4, x + size * 3 / 4, y + 2);
            // 眼睛
            using var eye = new SolidBrush(Color.White);
            g.FillEllipse(eye, x + size / 4 - 2, y + size / 3, 4, 4);
            g.FillEllipse(eye, x + size * 3 / 4 - 2, y + size / 3, 4, 4);
        }
    }

    internal sealed class WindowButton : Button
    {
        public int Order;
        public bool Danger;

        public WindowButton(string glyph, int order, Action click)
        {
            Order = order;
            Size = new Size(46, 32);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = ChromeColor;
            ForeColor = Color.FromArgb(90, 96, 105);
            Font = F("Segoe MDL2 Assets", 10f);
            Text = glyph;
            Cursor = Cursors.Hand;
            TabStop = false;
            Click += (_, _) => click();
            MouseEnter += (_, _) => { BackColor = Danger ? Color.FromArgb(232, 74, 74) : Color.FromArgb(233, 235, 239); ForeColor = Danger ? Color.White : Color.FromArgb(90, 96, 105); };
            MouseLeave += (_, _) => { BackColor = ChromeColor; ForeColor = Color.FromArgb(90, 96, 105); };
        }
    }

    /// <summary>左侧导航项：选中蓝底白字，未选中灰字。</summary>
    internal sealed class SidebarItem : Panel
    {
        public string Kind = "";
        private bool _selected;
        private readonly string _glyph;
        private readonly string _label;

        public SidebarItem(string glyph, string label)
        {
            _glyph = glyph;
            _label = label;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = SideColor;
            Cursor = Cursors.Hand;
        }

        public bool Selected
        {
            get => _selected;
            set { _selected = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            if (_selected)
            {
                using var path = Round(new Rectangle(0, 0, Width - 1, Height - 1), 10);
                using var fill = new SolidBrush(AccentColor);
                g.FillPath(fill, path);
            }

            var fore = _selected ? Color.White : Color.FromArgb(72, 78, 88);
            using var glyphFont = F("Segoe MDL2 Assets", 13f);
            using var textFont = F("Microsoft YaHei UI", 10.5f, _selected ? FontStyle.Bold : FontStyle.Regular);
            g.DrawString(_glyph, glyphFont, new SolidBrush(fore), 18, Height / 2 - 13);
            g.DrawString(_label, textFont, new SolidBrush(fore), 50, Height / 2 - 12);
        }
    }

    /// <summary>tab：文字 + 计数胶囊；下划线由父面板绘制。</summary>
    internal sealed class TabLabel : Panel
    {
        public string TextValue = "";
        public int Count;
        public bool Number;
        public bool Active;

        public TabLabel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = PageColor;
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var color = Active ? AccentColor : MutedColor;
            using var font = F("Microsoft YaHei UI", 11.5f, Active ? FontStyle.Bold : FontStyle.Regular);
            g.DrawString(TextValue, font, new SolidBrush(color), 0, Height / 2 - 14);
            if (!Number || Count <= 0) return;
            var x = (int)g.MeasureString(TextValue, font).Width + 8;
            var pill = new Rectangle(x, Height / 2 - 11, 26, 22);
            using var path = Round(pill, 11);
            using var fill = new SolidBrush(ChipColor);
            g.FillPath(fill, path);
            using var countFont = F("Microsoft YaHei UI", 8.5f, FontStyle.Bold);
            var text = Count.ToString();
            var w = (int)g.MeasureString(text, countFont).Width;
            g.DrawString(text, countFont, new SolidBrush(MutedColor), pill.X + (pill.Width - w) / 2, pill.Y + 3);
        }
    }

    /// <summary>自绘圆角按钮（大启动按钮等）。</summary>
    internal sealed class RoundedButton : Panel
    {
        public Color Fill = AccentColor;
        public Color Hover = Color.FromArgb(30, 86, 210);
        public Color Fore = Color.White;
        public string TextValue = "";
        public string Glyph = "";
        public bool Bold;
        public int Radius = 12;
        private bool _hover;

        public RoundedButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = PageColor;
            Cursor = Cursors.Hand;
        }

        public string Text { get => TextValue; set { TextValue = value; Invalidate(); } }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using var path = Round(new Rectangle(0, 0, Width - 1, Height - 1), Radius);
            using var fill = new SolidBrush(_hover ? Hover : Fill);
            g.FillPath(fill, path);

            using var font = F("Microsoft YaHei UI", Bold ? 12f : 10f, Bold ? FontStyle.Bold : FontStyle.Regular);
            var textWidth = (int)g.MeasureString(TextValue, font).Width;
            var glyphWidth = Glyph.Length > 0 ? 26 : 0;
            var x = (Width - textWidth - glyphWidth) / 2;
            if (Glyph.Length > 0)
            {
                using var glyphFont = F("Segoe MDL2 Assets", 14f);
                g.DrawString(Glyph, glyphFont, new SolidBrush(Fore), x, Height / 2 - 15);
                x += glyphWidth;
            }
            g.DrawString(TextValue, font, new SolidBrush(Fore), x, Height / 2 - 14);
        }
    }

    /// <summary>版本卡片：圆角边框 + 大号版本图标 + 标题/描述/状态胶囊 + 更多/启动或下载。</summary>
    internal sealed class Card : Panel
    {
        public readonly AndroidVersion Version;
        private readonly bool _installed;
        private bool _selected;
        private bool _hover;

        private readonly Label _title = new();
        private readonly Label _desc = new();
        private readonly StatusPill _pill;
        private readonly Label _more = new();
        private readonly Label _action = new();
        private readonly IconBadge _badge;
        private readonly RoundedButton _square;

        public event EventHandler? BodyClick;

        private Action _start = () => { };
        private Action _download = () => { };
        private Action _moreMenu = () => { };

        public Card(AndroidVersion version, bool installed, int width)
        {
            Version = version;
            _installed = installed;
            BackColor = PageColor;
            Margin = new Padding(0, 0, 0, 14);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;

            _badge = new IconBadge(version);
            _title.Text = version.Label;
            _title.ForeColor = InkColor;
            _title.Font = F("Microsoft YaHei UI", 13f, FontStyle.Bold);
            _title.AutoSize = false;
            _title.BackColor = Color.Transparent;
            _title.TextAlign = ContentAlignment.MiddleLeft;

            _desc.Text = Describe(version);
            _desc.ForeColor = MutedColor;
            _desc.Font = F("Microsoft YaHei UI", 9f);
            _desc.AutoSize = false;
            _desc.BackColor = Color.Transparent;
            _desc.TextAlign = ContentAlignment.MiddleLeft;

            _pill = new StatusPill(installed);

            _more.Text = L.T("gui.more") + "  \uE70D";
            _more.ForeColor = Color.FromArgb(96, 102, 112);
            _more.Font = F("Microsoft YaHei UI", 9f);
            _more.BackColor = ChipColor;
            _more.TextAlign = ContentAlignment.MiddleCenter;
            _more.Cursor = Cursors.Hand;
            _more.Font = F("Microsoft YaHei UI", 9f);

            _action.Text = installed ? L.T("gui.start") : L.T("gui.download");
            _action.ForeColor = AccentColor;
            _action.Font = F("Microsoft YaHei UI", 10.5f);
            _action.BackColor = Color.Transparent;
            _action.TextAlign = ContentAlignment.MiddleCenter;
            _action.Cursor = Cursors.Hand;

            Controls.Add(_badge);
            Controls.Add(_title);
            Controls.Add(_desc);
            Controls.Add(_pill);
            Controls.Add(_more);
            Controls.Add(_action);

            _square = new RoundedButton
            {
                Radius = 10,
                Fill = Color.FromArgb(232, 238, 252),
                Hover = Color.FromArgb(218, 229, 250),
                Fore = AccentColor,
                Glyph = installed ? "\uE768" : "\uE896",
                Size = new Size(44, 44),
            };
            Controls.Add(_square);

            _action.Click += (_, _) => { if (_installed) _start(); else _download(); };
            _square.Click += (_, _) => { if (_installed) _start(); else _download(); };
            _more.Click += (_, _) => _moreMenu();
            _badge.Click += (_, _) => BodyClick?.Invoke(this, EventArgs.Empty);
            _title.Click += (_, _) => BodyClick?.Invoke(this, EventArgs.Empty);
            _desc.Click += (_, _) => BodyClick?.Invoke(this, EventArgs.Empty);
            _pill.Click += (_, _) => BodyClick?.Invoke(this, EventArgs.Empty);
            foreach (Control child in Controls) child.MouseEnter += (_, _) => { _hover = true; Invalidate(); };
            foreach (Control child in Controls) child.MouseLeave += (_, _) => { _hover = false; Invalidate(); };
            Height = 120;
            Width = Math.Max(600, width);
            Layout();
        }

        public void Handlers(Action start, Action download, Action more)
        {
            _start = start;
            _download = download;
            _moreMenu = more;
        }

        public void SetActionEnabled(bool enabled)
        {
            _action.Enabled = enabled;
        }

        public bool Selected
        {
            get => _selected;
            set { _selected = value; Invalidate(); }
        }

        private static string Describe(AndroidVersion version)
        {
            var month = version.ApiLevel switch
            {
                8 => "2010 年 5 月", 10 => "2011 年 2 月", 11 => "2011 年 2 月", 12 => "2011 年 5 月",
                13 => "2011 年 7 月", 14 => "2011 年 10 月", 15 => "2011 年 12 月", 16 => "2012 年 7 月",
                17 => "2012 年 11 月", 18 => "2013 年 7 月", 19 => "2013 年 10 月", 20 => "2013 年 12 月",
                21 => "2014 年 11 月", 22 => "2015 年 3 月", 23 => "2015 年 10 月", 24 => "2016 年 8 月",
                25 => "2016 年 10 月", 26 => "2017 年 8 月", 27 => "2017 年 12 月", 28 => "2018 年 8 月",
                29 => "2019 年 9 月", 30 => "2020 年 9 月", 31 => "2021 年 10 月", 32 => "2022 年 3 月",
                33 => "2022 年 8 月", 34 => "2023 年 10 月", 35 => "2024 年 9 月", 36 => "2025 年 3 月",
                _ => "2026 年 6 月",
            };
            var hint = version.ApiLevel switch
            {
                >= 36 => "最新特性，更智能的体验",
                35 => "性能优化，更流畅的体验",
                34 => "稳定可靠，兼容广泛",
                <= 33 => "经典版本，稳定高效",
            };
            return month + "  ·  " + hint;
        }

        private void RaiseClick()
        {
            BodyClick?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            RaiseClick();
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Layout(); }

        private void Layout()
        {
            if (_square == null || _badge == null || _title == null || _desc == null || _pill == null || _more == null || _action == null) return;
            var height = Height;
            _badge.Bounds = new Rectangle(18, (height - 82) / 2, 82, 82);
            _title.Bounds = new Rectangle(118, 18, Math.Max(180, Width - 520), 26);
            _desc.Bounds = new Rectangle(118, 46, Math.Max(200, Width - 520), 22);
            var pillWidth = _pill.PreferredWidth;
            _pill.Bounds = new Rectangle(118, 74, pillWidth, 24);

            var right = Width - 18;
            const int square = 44;
            _square.Bounds = new Rectangle(right - square, (height - square) / 2, square, square);

            var actionWidth = 74;
            _action.Bounds = new Rectangle(_square.Left - 8 - actionWidth, (height - 28) / 2, actionWidth, 28);

            var moreWidth = 96;
            _more.Bounds = new Rectangle(_action.Left - 12 - moreWidth, (height - 30) / 2, moreWidth, 30);
            _more.Region = new Region(Round(new Rectangle(0, 0, moreWidth, 30), 8));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = Round(rect, 12);
            using var fill = new SolidBrush(Color.White);
            g.FillPath(fill, path);
            using var pen = new Pen(_selected ? AccentColor : (_hover ? Color.FromArgb(214, 218, 224) : LineColor), _selected ? 1.6f : 1f);
            g.DrawPath(pen, path);
        }
    }

    /// <summary>卡片左侧的版本图标：深灰圆角方块 + 数字 + 安卓机器人。</summary>
    internal sealed class IconBadge : Panel
    {
        private readonly string _number;

        public IconBadge(AndroidVersion version)
        {
            // "Android 16" -> "16"，"Android 4.4 KitKat" -> "4.4"
            var label = version.Label.Replace("Android ", "").Trim();
            var cut = label.IndexOf(' ');
            _number = cut > 0 ? label.Substring(0, cut) : label;
            BackColor = PageColor;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using var path = Round(new Rectangle(0, 0, Width - 1, Height - 1), 12);
            using var fill = new SolidBrush(Color.FromArgb(43, 47, 54));
            g.FillPath(fill, path);

            using var numberFont = F("Segoe UI", 21f, FontStyle.Bold);
            var width = (int)g.MeasureString(_number, numberFont).Width;
            g.DrawString(_number, numberFont, new SolidBrush(Color.White), (Width - width) / 2, 12);
            ChromeBar.DrawRobot(g, Width / 2 - 12, Height - 32, 24, Color.White);
        }
    }

    /// <summary>状态胶囊：已安装（绿）/ 可下载（蓝）。</summary>
    internal sealed class StatusPill : Panel
    {
        private readonly bool _installed;
        public int PreferredWidth => 84;

        public StatusPill(bool installed)
        {
            _installed = installed;
            BackColor = PageColor;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using var path = Round(new Rectangle(0, 0, Width - 1, Height - 1), Height / 2);
            using var fill = new SolidBrush(_installed ? GreenBg : BlueBg);
            g.FillPath(fill, path);
            using var dot = new SolidBrush(_installed ? GreenColor : AccentColor);
            g.FillEllipse(dot, 10, Height / 2 - 3, 6, 6);
            using var font = F("Microsoft YaHei UI", 8.5f);
            var text = _installed ? L.T("gui.installed") : L.T("gui.available");
            g.DrawString(text, font, new SolidBrush(_installed ? GreenColor : AccentColor), 22, Height / 2 - 9);
        }
    }
}





