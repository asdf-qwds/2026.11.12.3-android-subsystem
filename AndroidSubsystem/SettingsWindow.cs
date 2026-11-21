using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AndroidSubsystem;

/// <summary>
/// android.exe -settings 打开的设置窗口。
/// 左侧一列导航：更新 / 语言 / 图形界面 / 模拟器，右侧对应内容页。
/// </summary>
internal sealed class SettingsWindow : Form
{
    private readonly Settings _settings;
    private Panel _sidebar = new BufferedPanel();
    private Panel _content = new BufferedPanel();
    private Panel _navHost = new BufferedPanel();
    private string _page = "update";

    public SettingsWindow(Settings settings)
    {
        _settings = settings;
        L.Current = L.Parse(settings.Language);
        DoubleBuffered = true;
        Rebuild();
    }

    // ---------------------------------------------------------------- 外壳

    private void Rebuild()
    {
        SuspendLayout();

        // 彻底销毁旧的一整套控件，避免旧文字/旧面板叠在新界面上（语言切换残影、内容区白屏）
        if (Controls.Count > 0)
        {
            foreach (Control c in Controls.Cast<Control>().ToArray())
            {
                Controls.Remove(c);
                c.Dispose();
            }
        }

        _sidebar = new BufferedPanel();
        _content = new BufferedPanel();
        _navHost = new BufferedPanel();

        Text = L.T("settings") + " · " + L.T("app");
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(960, 650);
        MinimumSize = new Size(860, 600);
        BackColor = Color.FromArgb(245, 246, 249);
        Font = new Font("Microsoft YaHei UI", 10f);

        _sidebar.Dock = DockStyle.Left;
        _sidebar.Width = 214;
        _sidebar.BackColor = Color.FromArgb(20, 22, 26);

        var brand = new Label
        {
            Text = L.T("app"),
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold),
            Location = new Point(24, 26),
            AutoSize = true,
        };
        var build = new Label
        {
            Text = "build " + ConsoleUi.BuildVersion,
            ForeColor = Color.FromArgb(140, 145, 155),
            Font = new Font("Microsoft YaHei UI", 8.5f),
            Location = new Point(26, 56),
            AutoSize = true,
        };

        _navHost.Location = new Point(0, 96);
        _navHost.Size = new Size(214, 420);
        _navHost.BackColor = Color.FromArgb(20, 22, 26);
        BuildNavButtons();

        _sidebar.Controls.Add(brand);
        _sidebar.Controls.Add(build);
        _sidebar.Controls.Add(_navHost);

        _content.Dock = DockStyle.Fill;
        _content.BackColor = Color.White;
        _content.Padding = new Padding(30, 24, 30, 24);
        _content.AutoScroll = true;

        Controls.Add(_content);
        Controls.Add(_sidebar);

        ResumeLayout(true);
        PerformLayout();
        Navigate(_page);
        _sidebar.Invalidate(true);
        _content.Invalidate(true);
        Refresh();
        Update();
    }

    private void BuildNavButtons()
    {
        var items = new[]
        {
            ("update", "nav.update"),
            ("language", "nav.language"),
            ("gui", "nav.gui"),
        };

        var y = 0;
        foreach (var (key, labelKey) in items)
        {
            var button = new Button
            {
                Text = L.T(labelKey),
                TextAlign = ContentAlignment.MiddleLeft,
                FlatStyle = FlatStyle.Flat,
                FlatAppearance = { BorderSize = 0 },
                BackColor = Color.FromArgb(20, 22, 26),
                ForeColor = key == _page ? Color.White : Color.FromArgb(190, 194, 202),
                Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold),
                Padding = new Padding(24, 0, 0, 0),
                Size = new Size(214, 44),
                Location = new Point(0, y),
                Cursor = Cursors.Hand,
            };
            var target = key;
            button.Click += (_, _) => Navigate(target);
            _navHost.Controls.Add(button);
            y += 46;
        }
    }

    private void Navigate(string page)
    {
        _page = page;
        _content.Controls.Clear();
        switch (page)
        {
            case "language": _content.Controls.Add(LanguagePage()); break;
            case "gui": _content.Controls.Add(GuiPage()); break;
            default: _content.Controls.Add(UpdatePage()); break;
        }
        BuildNavHighlight();
        _navHost.Invalidate(true);
        _navHost.Update();
    }

    private void BuildNavHighlight()
    {
        var keys = new[] { "update", "language", "gui" };
        for (var i = 0; i < _navHost.Controls.Count && i < keys.Length; i++)
        {
            var b = (Button)_navHost.Controls[i];
            var active = keys[i] == _page;
            b.ForeColor = active ? Color.White : Color.FromArgb(190, 194, 202);
            b.BackColor = active ? Color.FromArgb(38, 42, 52) : Color.FromArgb(20, 22, 26);
            b.Invalidate();
            b.Update();
        }
    }

    // ---------------------------------------------------------------- 通用小件

    private static Label Title(string text, int top)
    {
        return new Label
        {
            Text = text,
            Font = new Font("Microsoft YaHei UI", 16f, FontStyle.Bold),
            ForeColor = Color.FromArgb(24, 26, 32),
            Location = new Point(0, top),
            AutoSize = true,
        };
    }

    private static Label Note(string text, int top, int width)
    {
        return new Label
        {
            Text = text,
            Font = new Font("Microsoft YaHei UI", 9.5f),
            ForeColor = Color.FromArgb(110, 116, 128),
            Location = new Point(2, top),
            AutoSize = false,
            Width = width,
            Height = 40,
        };
    }

    private static Button Primary(string text, int x, int y, int width, EventHandler click)
    {
        var button = new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(width, 38),
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0 },
            BackColor = Color.FromArgb(52, 110, 235),
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 10f),
            Cursor = Cursors.Hand,
        };
        button.Click += click;
        return button;
    }

    // ---------------------------------------------------------------- 更新页

    private Panel UpdatePage()
    {
        var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        panel.Controls.Add(Title(L.T("update.title"), 0));
        panel.Controls.Add(Note(L.T("update.desc"), 46, 640));

        var version = new Label
        {
            Text = L.T("update.version") + "：" + ConsoleUi.BuildVersion,
            Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold),
            ForeColor = Color.FromArgb(30, 34, 42),
            Location = new Point(2, 100),
            AutoSize = true,
        };
        panel.Controls.Add(version);

        var auto = new CheckBox
        {
            Text = L.T("update.auto"),
            Checked = _settings.AutoUpdate,
            Location = new Point(2, 142),
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 10f),
        };
        auto.CheckedChanged += (_, _) =>
        {
            _settings.AutoUpdate = auto.Checked;
            _settings.Save();
        };
        panel.Controls.Add(auto);

        var channelLabel = new Label
        {
            Text = L.T("update.channel"),
            Font = new Font("Microsoft YaHei UI", 10f),
            ForeColor = Color.FromArgb(70, 76, 86),
            Location = new Point(2, 186),
            AutoSize = true,
        };
        panel.Controls.Add(channelLabel);

        var chStable = new RadioButton
        {
            Text = L.T("update.channel.stable"),
            Checked = (_settings.UpdateChannel ?? "stable") != "preview",
            Location = new Point(118, 182),
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 10f),
        };
        var chPreview = new RadioButton
        {
            Text = L.T("update.channel.preview"),
            Checked = (_settings.UpdateChannel ?? "stable") == "preview",
            Location = new Point(220, 182),
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 10f),
        };
        chStable.CheckedChanged += (_, _) =>
        {
            if (!chStable.Checked) return;
            _settings.UpdateChannel = "stable";
            _settings.Save();
        };
        chPreview.CheckedChanged += (_, _) =>
        {
            if (!chPreview.Checked) return;
            _settings.UpdateChannel = "preview";
            _settings.Save();
        };
        panel.Controls.Add(chStable);
        panel.Controls.Add(chPreview);
        panel.Controls.Add(Note(L.T("update.channel.hint"), 216, 640));

        panel.Controls.Add(Primary(L.T("update.check"), 2, 264, 170, (_, _) => LaunchConsole("-update")));
        panel.Controls.Add(Note(L.T("update.hint"), 310, 640));
        return panel;
    }

    // ---------------------------------------------------------------- 语言页

    private Panel LanguagePage()
    {
        var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        panel.Controls.Add(Title(L.T("lang.title"), 0));
        panel.Controls.Add(Note(L.T("lang.desc"), 46, 640));

        var options = new[]
        {
            (Lang.Zh, "中文"),
            (Lang.Ko, "한국어"),
            (Lang.Ja, "日本語"),
            (Lang.En, "English"),
        };

        var y = 100;
        foreach (var (lang, label) in options)
        {
            var radio = new RadioButton
            {
                Text = label,
                Checked = L.Current == lang,
                Location = new Point(2, y),
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 11f),
            };
            var chosen = lang;
            radio.CheckedChanged += (_, _) =>
            {
                if (!radio.Checked) return;
                L.Current = chosen;
                _settings.Language = L.CodeOf(chosen);
                _settings.Save();
                WebGuiWindow.RefreshLocale();
                BeginInvoke(new Action(Rebuild));
            };
            panel.Controls.Add(radio);
            y += 38;
        }

        panel.Controls.Add(Note(L.T("lang.hint"), y + 16, 640));
        return panel;
    }

    // ---------------------------------------------------------------- 图形界面页

    private Panel GuiPage()
    {
        var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        panel.Controls.Add(Title(L.T("gui.title"), 0));
        panel.Controls.Add(Note(L.T("gui.desc"), 46, 640));

        var mode = new CheckBox
        {
            Text = L.T("gui.mode"),
            Checked = _settings.GuiMode,
            Location = new Point(2, 100),
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 10f),
        };
        mode.CheckedChanged += (_, _) =>
        {
            _settings.GuiMode = mode.Checked;
            _settings.Save();
        };
        panel.Controls.Add(mode);

        panel.Controls.Add(Primary(L.T("gui.open"), 2, 142, 200, (_, _) =>
        {
            WebGuiWindow.ShowOrActivate(_settings);
        }));

        panel.Controls.Add(Note(L.T("gui.hint"), 196, 640));
        return panel;
    }


    // ---------------------------------------------------------------- 工具

    /// <summary>把这个 exe 再起一个实例，跑命令行子命令（更新/下载这类需要 Y/N 的流程在控制台里完成）。</summary>
    internal static void LaunchConsole(string arguments)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, L.T("app"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}

/// <summary>带双缓冲的面板，避免切换语言/拖动窗口时的黑块与残影。</summary>
internal sealed class BufferedPanel : Panel
{
    public BufferedPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        ResizeRedraw = true;
    }
}



