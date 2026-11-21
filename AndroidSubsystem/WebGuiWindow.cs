using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AndroidSubsystem;

/// <summary>
/// 图形界面主窗口（android.exe -gui）：界面由 HTML（Assets/gui.html）严格按 Assets/skin2.png 渲染，
/// C# 只做宿主与业务（启动 / 下载 / 删除 / 设置 / 安装）。
/// </summary>
internal sealed class WebGuiWindow : Form
{
    // 全局只允许一个图形界面实例（WebView2 的用户数据目录只有一个，再开第二个会加载失败）
    private static WebGuiWindow? _instance;

    /// <summary>已经开过图形界面就直接把它显示到前台，否则新建一个。</summary>
    public static WebGuiWindow ShowOrActivate(Settings settings)
    {
        if (_instance != null && !_instance.IsDisposed)
        {
            if (_instance.WindowState == FormWindowState.Minimized)
                _instance.WindowState = FormWindowState.Normal;
            _instance.Show();
            _instance.Activate();
            _instance.BringToFront();
            return _instance;
        }
        var window = new WebGuiWindow(settings);
        window.FormClosed += (_, _) => { if (_instance == window) _instance = null; };
        _instance = window;
        window.Show();
        return window;
    }

    /// <summary>语言变了：把已经开着的图形界面立刻按新语言重画一遍。</summary>
    public static void RefreshLocale()
    {
        var window = _instance;
        if (window == null || window.IsDisposed || !window.IsHandleCreated) return;
        try
        {
            window.BeginInvoke(new Action(() =>
            {
                if (window.IsDisposed) return;
                window.Text = L.T("app");
                window.PushData();
            }));
        }
        catch { }
    }

    private readonly Settings _settings;
    private readonly AndroidSdk? _sdk;
    private readonly WebView2 _view = new();
    private AndroidVersion? _selected;
    private bool _maximized;
    private bool _crashing;

    private readonly List<string> _log = new();

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int index);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 0x2;

    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);

    private static float ScreenScale()
    {
        var bySystem = 1f;
        var byDc = 1f;
        try { var dpi = GetDpiForSystem(); if (dpi >= 96) bySystem = dpi / 96f; } catch { }
        try
        {
            var hdc = GetDC(IntPtr.Zero);
            var dpi = GetDeviceCaps(hdc, 88);
            ReleaseDC(IntPtr.Zero, hdc);
            if (dpi >= 96) byDc = dpi / 96f;
        }
        catch { }
        return Math.Max(bySystem, byDc);
    }

    public WebGuiWindow(Settings settings)
    {
        _settings = settings;
        L.Current = L.Parse(settings.Language);
        _sdk = OpenSdk();

        ClientSize = new Size(900, 600);
        MinimumSize = new Size(760, 500);
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.White;
        Text = L.T("app");
        DoubleBuffered = true;

        _view.Dock = DockStyle.Fill;
        _view.DefaultBackgroundColor = Color.White;
        Controls.Add(_view);

        // 允许把 APK 直接拖进窗口（拖到“安装包”区域效果相同）
        AllowDrop = true;
        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;

        Resize += (_, _) => { ApplyRoundRegion(); ApplyZoom(); };
        Load += (_, _) =>
        {
            ApplyScreenSize();
            ApplyRoundRegion();
            AppendLog($"dpi={DeviceDpi} client={ClientSize.Width}x{ClientSize.Height} screen={(Screen.PrimaryScreen?.Bounds.Width)}x{(Screen.PrimaryScreen?.Bounds.Height)}");
            _ = InitAsync();
        };
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data == null) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effect = DragDropEffects.Copy;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var apk = files.FirstOrDefault(f => f.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));
        if (apk == null)
        {
            MessageBox.Show("只支持 .apk 安装包。", "安卓虚拟机", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        InstallApk(apk);
    }

    private void ApplyZoom()
    {
        // 页面自己按视口等比缩放（transform: scale），这里保持 100%，避免双重缩放
        if (_view.CoreWebView2 == null) return;
        _view.ZoomFactor = 1.0;
    }

    /// <summary>按屏幕物理尺寸 + 当前 DPI 计算窗口大小，确保窗口完整落在屏幕内。</summary>
    private void ApplyScreenSize()
    {
        var dpi = 1f;
        try
        {
            var d = GetDpiForWindow(Handle);
            if (d >= 96) dpi = d / 96f;
        }
        catch { }
        if (dpi <= 1f) dpi = ScreenScale();
        if (dpi <= 0) dpi = 1f;

        var scr = Screen.PrimaryScreen;
        var physW = scr?.Bounds.Width ?? 1280;
        var physH = scr?.Bounds.Height ?? 800;
        var w = (int)((physW - 12) / dpi);
        var h = (int)((physH - 56) / dpi);
        ClientSize = new Size(Math.Max(720, w), Math.Max(480, h));
        MinimumSize = new Size((int)(720 / dpi), (int)(480 / dpi));

        var area = scr?.WorkingArea ?? new Rectangle(0, 0, physW, physH);
        Location = new Point(
            area.Left + Math.Max(0, (area.Width - Width) / 2),
            area.Top + Math.Max(0, (area.Height - Height) / 2));
    }

    private AndroidSdk? OpenSdk()
    {
        var root = _settings.SdkRoot;
        return !string.IsNullOrWhiteSpace(root) && Directory.Exists(root) ? new AndroidSdk(root!) : null;
    }

    private void ApplyRoundRegion()
    {
        if (_maximized || WindowState == FormWindowState.Maximized) { Region = null; return; }
        using var path = GuiWindow.Round(new Rectangle(0, 0, Width, Height), 12);
        Region = new Region(path);
    }

    // ---------------------------------------------------------------- WebView2 初始化

    private async System.Threading.Tasks.Task InitAsync()
    {
        try
        {
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AndroidSubsystem", "WebView");
            Directory.CreateDirectory(dataDir);

            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await _view.EnsureCoreWebView2Async(env);

            var core = _view.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.WebMessageReceived += OnWebMessage;
            core.NavigationCompleted += (_, _) =>
            {
                ApplyZoom();
                PushData();
                StartStateTimer();
                if (Environment.GetEnvironmentVariable("ANDROID_UI_DEMO") == "1")
                {
                    PushProgress(true, "下载 Android 14", 42, "860 / 2048 MB · 6.3 MB/s", false);
                }
            };

            var uiDir = PrepareUiFolder();
            core.SetVirtualHostNameToFolderMapping(
                "app.local", uiDir, CoreWebView2HostResourceAccessKind.Allow);
            core.Navigate("https://app.local/gui.html");
        }
        catch (Exception ex)
        {
            AppendLog("WebView2 初始化失败：" + ex.Message);
            MessageBox.Show(
                "界面组件 WebView2 加载失败。请确认系统已安装 Microsoft Edge WebView2 Runtime。\n\n" + ex.Message,
                "安卓虚拟机", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string PrepareUiFolder()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AndroidSubsystem", "ui");
        var icons = Path.Combine(dir, "icons");
        Directory.CreateDirectory(icons);

        var asm = typeof(WebGuiWindow).Assembly;
        const string prefix = "AndroidSubsystem.Assets.";
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = name.Substring(prefix.Length);
            string target;
            if (rest.StartsWith("icons.", StringComparison.OrdinalIgnoreCase))
            {
                target = Path.Combine(icons, rest.Substring("icons.".Length));
            }
            else
            {
                target = Path.Combine(dir, rest);
            }

            try
            {
                using var stream = asm.GetManifestResourceStream(name);
                if (stream == null) continue;
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                if (ms.Length == 0) continue;
                File.WriteAllBytes(target, ms.ToArray());
            }
            catch { }
        }
        return dir;
    }

    // ---------------------------------------------------------------- 数据推送

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

    private object BuildPayload()
    {
        var ui = new
        {
            appName = L.T("app"),
            subtitle = L.T("gui.subtitle"),
            search = L.T("gui.search"),
            files = L.T("gui.files"),
            manage = L.T("gui.manage"),
            pick = L.T("gui.pick"),
            pick2 = L.T("gui.pick2"),
            start = L.T("gui.start"),
            download = L.T("gui.download"),
            more = L.T("gui.more"),
            installed = L.T("gui.installed"),
            notInstalled = L.T("gui.notinstalled"),
            running = L.T("gui.running"),
            stop = L.T("gui.stopone"),
            restart = L.T("gui.restart"),
            apkTitle = L.T("apk.title"),
            apkSub = L.T("apk.sub"),
            dropA = L.T("apk.drop1"),
            dropB = L.T("apk.drop2"),
            dropC = L.T("apk.drop3"),
            betterA = L.T("gui.better1"),
            betterB = L.T("gui.better2"),
            nav = new object[]
            {
                new { key = "home", label = L.T("nav.home"), svg = NavSvg("home") },
                new { key = "apk", label = L.T("apk.title"), svg = NavSvg("apk") },
                new { key = "settings", label = L.T("gui.settings"), svg = NavSvg("settings") },
            },
        };

        var versions = Catalog.Versions
            .OrderByDescending(v => v.ApiLevel)
            .Select(v => new
            {
                key = v.Key,
                label = v.Label,
                desc = DescribeOf(v),
                art = ArtOf(v),
                api = v.ApiLevel,
                installed = IsInstalled(v),
                running = IsInstalled(v) && AndroidSdk.IsRunning(AndroidSdk.AvdNameFor(v)),
            })
            .ToArray();

        return new { ui, versions, selected = _selected?.Key };
    }

    private static string ArtOf(AndroidVersion v) => v.Key switch
    {
        "android2.2" => "2_2",
        "android2.3" => "2_3",
        "android3.0" => "3_0",
        "android3.1" => "3_1",
        "android3.2" => "3_2",
        "android4.0" or "android4.0.3" => "4_0",
        "android4.1" => "4_1",
        "android4.2" => "4_2",
        "android4.3" => "4_3",
        "android4.4" or "android4.4w" => "4_4",
        "android5.0" => "5_0",
        "android5.1" => "5_1",
        "android6.0" => "6",
        "android7.0" or "android7.1" => "7",
        "android8.0" or "android8.1" => "8",
        "android9" => "9",
        "android10" => "10",
        "android11" => "11",
        "android12" or "android12l" => "12",
        "android13" => "13",
        "android14" => "14",
        "android15" => "15",
        "android16" => "16",
        _ => "17",
    };

    private static string DescribeOf(AndroidVersion v)
    {
        var api = v.ApiLevel;
        if (api >= 37) return Ym(2026, 6, "desc.features");
        if (api >= 36) return Ym(2025, 3, "desc.features");
        if (api >= 35) return Ym(2024, 9, "desc.smooth");
        if (api >= 34) return Ym(2023, 10, "desc.stable");
        if (api >= 33) return Ym(2022, 8, "desc.privacy");
        if (api >= 31) return Ym(2021, 10, "desc.design");
        if (api >= 30) return Ym(2020, 9, "desc.privacy2");
        if (api >= 29) return Ym(2019, 9, "desc.gesture");
        if (api >= 28) return Ym(2018, 8, "desc.power");
        if (api >= 26) return Ym(2017, 8, "desc.notify");
        if (api >= 24) return Ym(2016, 8, "desc.split");
        if (api >= 23) return Ym(2015, 10, "desc.perm");
        if (api >= 21) return L.T("desc.material").Replace("{y}", "2014");
        if (api >= 14) return L.T("desc.classic");
        if (api >= 11) return L.T("desc.tablet").Replace("{y}", "2011");
        return L.T("desc.early").Replace("{y}", "2010");
    }

    /// <summary>按当前语言拼「年 月 · 描述」。</summary>
    private static string Ym(int year, int month, string key)
    {
        var monthText = month.ToString();
        if (L.Current == Lang.En)
        {
            var names = new[] { "", "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
            if (month >= 1 && month <= 12) monthText = names[month];
        }
        return L.T("desc.ym")
            .Replace("{y}", year.ToString())
            .Replace("{m}", monthText)
            .Replace("{desc}", L.T(key));
    }

    private static string NavSvg(string kind) => kind switch
    {
        "home" => "<svg viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.6\"><rect x=\"3.5\" y=\"8\" width=\"17\" height=\"10\" rx=\"3.5\"/><circle cx=\"9\" cy=\"13\" r=\"1\"/><circle cx=\"15\" cy=\"13\" r=\"1\"/><path d=\"M7.2 8L6 5.4M16.8 8l1.2-2.6\"/></svg>",
        "apk" => "<svg viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.6\"><rect x=\"3\" y=\"5\" width=\"18\" height=\"14\" rx=\"3\"/><path d=\"M8 12h8M12 8v8\"/></svg>",
        _ => "<svg viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.6\"><circle cx=\"12\" cy=\"12\" r=\"3.2\"/><path d=\"M12 3v3M12 18v3M3 12h3M18 12h3M5.6 5.6l2.1 2.1M16.3 16.3l2.1 2.1M18.4 5.6l-2.1 2.1M7.7 16.3l-2.1 2.1\"/></svg>",
    };

    private void PushData()
    {
        if (_crashing) return;
        try
        {
            var json = JsonSerializer.Serialize(BuildPayload());
            _ = _view.CoreWebView2?.ExecuteScriptAsync("window.app&&window.app.setData(" + json + ")");
        }
        catch (Exception ex) { AppendLog("推送数据失败：" + ex.ToString()); }
    }

    // ---------------------------------------------------------------- 消息处理

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string cmd = "";
        string key = "";
        string text = "";
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("cmd", out var c)) cmd = c.GetString() ?? "";
            if (root.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String) key = k.GetString() ?? "";
            if (root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String) text = t.GetString() ?? "";
        }
        catch { return; }

        var version = Catalog.Resolve(key);

        switch (cmd)
        {
            case "start":
                if (version != null) StartVersion(version);
                break;
            case "stop":
                if (version != null) StopVersion(version);
                break;
            case "restart":
                if (version != null) StopVersion(version, true);
                break;
            case "download":
                if (version != null)
                {
                    if (_downloadBusy)
                    {
                        MessageBox.Show(L.T("gui.downloadbusy"), L.T("app"),
                                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                        break;
                    }
                    var tag = AskMirrorTag(version);
                    if (tag != null)
                    {
                        _downloadBusy = true;
                        BeginDownload(version);
                        RunHidden("-download " + version.Key + " " + tag, null, "download");
                    }
                }
                break;
            case "startSelected":
                if (version != null) { _selected = version; StartVersion(version); }
                else StartSelected();
                break;
            case "select":
                if (version != null) _selected = version;
                break;
            case "more":
                if (version != null) ShowMoreMenu(version);
                break;
            case "filterMenu":
                ShowFilterMenu();
                break;
            case "recommend":
                var latest = Catalog.Versions.OrderByDescending(v => v.ApiLevel).FirstOrDefault();
                if (latest != null) StartVersion(latest);
                break;
            case "settings":
                OpenSettings();
                break;
            case "openDownloads":
                OpenDownloadFolder();
                break;
            case "nav":
                if (key == "settings") OpenSettings();
                else Send("window.app.setView('" + (key == "apk" ? "apk" : "home") + "')");
                break;
            case "pickApk":
                PickAndInstallApk();
                break;
            case "drag":
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                break;
            case "min": WindowState = FormWindowState.Minimized; break;
            case "max": ToggleMaximize(); break;
            case "close": Close(); break;
            case "toast":
                AppendLog(text);
                break;
        }
    }

    private void Send(string js)
    {
        try { _ = _view.CoreWebView2?.ExecuteScriptAsync(js); } catch { }
    }

    private void ToggleMaximize()
    {
        _maximized = !_maximized;
        WindowState = _maximized ? FormWindowState.Maximized : FormWindowState.Normal;
        ApplyRoundRegion();
    }

    // ---------------------------------------------------------------- 菜单

    private void ShowFilterMenu()
    {
        var menu = new ContextMenuStrip { Font = F("Microsoft YaHei UI", 9.5f) };
        void Add(string id, string label) =>
            menu.Items.Add(new ToolStripMenuItem(label, null, (_, _) =>
            {
                Send("window.app.setFilter('" + id + "')");
            }));

        Add("all", L.T("gui.filter.all"));
        Add("installed", L.T("gui.installed"));
        Add("available", L.T("gui.available"));

        var at = Cursor.Position;
        menu.Show(new Point(at.X + 4, at.Y + 4));
    }

    private void ShowMoreMenu(AndroidVersion version)
    {
        var menu = new ContextMenuStrip { Font = F("Microsoft YaHei UI", 9.5f) };
        menu.Items.Add(new ToolStripMenuItem(L.T("gui.start"), null, (_, _) => StartVersion(version)));
        menu.Items.Add(new ToolStripMenuItem(L.T("gui.install"), null, (_, _) => PickAndInstallApk()));
        menu.Items.Add(new ToolStripMenuItem(L.T("gui.delete"), null, (_, _) => ConfirmDelete(version)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(L.T("gui.openavd"), null, (_, _) => OpenAvdFolder()));
        menu.Items.Add(new ToolStripMenuItem(L.T("gui.log"), null, (_, _) => ShowLog()));

        var at = Cursor.Position;
        menu.Show(new Point(at.X - 120, at.Y + 8));
    }

    private void OpenAvdFolder()
    {
        var dir = Path.Combine(_settings.SdkRoot ?? "", "avd");
        if (!Directory.Exists(dir)) dir = _settings.SdkRoot ?? "";
        try { Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "\"" + dir + "\"", UseShellExecute = true }); }
        catch (Exception ex) { AppendLog(ex.Message); }
    }

    private void OpenDownloadFolder()
    {
        try
        {
            var root = _settings.SdkRoot ?? ".";
            if (!Directory.Exists(root)) root = Environment.GetFolderPath(Environment.SpecialFolder.MyComputer);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + root + "\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { AppendLog(ex.Message); }
    }

    private void ShowLog()
    {
        string text;
        lock (_log) text = string.Join(Environment.NewLine, _log.TakeLast(60));
        MessageBox.Show(text.Length == 0 ? "（暂无日志）" : text, "运行日志", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ---------------------------------------------------------------- 动作

    private void RunHidden(string arguments, string? stdIn = null, string kind = "")
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
            psi.Environment["ANDROID_GUI_PROGRESS"] = "1";
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                AppendLog(e.Data);
                OnChildLine(kind, e.Data);
            };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) AppendLog("[err] " + e.Data); };
            process.Exited += (_, _) =>
            {
                AppendLog("— 结束：" + arguments);
                try { BeginInvoke(new Action(PushData)); } catch { }
                if (kind == "download")
                {
                    var code = 0;
                    try { code = process.ExitCode; } catch { }
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            _downloadBusy = false;
                            if (code == 0) FinishDownload(null);
                            else PushProgress(true, L.T("gui.download"), 0, L.T("gui.downloadfail"), true);
                        }));
                    }
                    catch { }
                }
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
        catch (Exception ex) { AppendLog("启动失败：" + ex.Message); }
    }

    // ---------------------------------------------------------------- 底部进度

    private void PushProgress(bool visible, string label, double percent, string text, bool done, bool indeterminate = false)
    {
        var json = JsonSerializer.Serialize(new { visible, label, percent, text, done, indeterminate });
        Send("window.app&&window.app.setProgress(" + json + ")");
    }

    private bool _startDoneReported;
    private bool _downloadDoneReported;
    private bool _downloadBusy;
    private double _progPct;

    private void OnChildLine(string kind, string line)
    {
        if (line.StartsWith("#P|", StringComparison.Ordinal))
        {
            // #P|标签|百分比|已完成MB|总MB|速度
            var parts = line.Split('|');
            if (parts.Length >= 6)
            {
                var label = parts[1];
                double pct = 0;
                double.TryParse(parts[2], out pct);
                _progPct = pct;
                // 只有百分比（sdkmanager 不下发字节数时 done/total 是 -1）
                var text = (parts[3] == "-1")
                    ? $"{pct:0.0}%"
                    : $"{parts[3]} / {parts[4]} MB · {parts[5]} MB/s";
                try { BeginInvoke(new Action(() => PushProgress(true, label, pct, text, false))); } catch { }
            }
            return;
        }

        if (kind == "download" && line.Contains("下载完成"))
        {
            try { BeginInvoke(new Action(() => FinishDownload(line.Trim()))); } catch { }
        }

        if (kind == "download")
        {
            var t = line.Trim();
            if (t.Length > 0 && !t.StartsWith(">"))
            {
                var m = System.Text.RegularExpressions.Regex.Match(t, @"(\d{1,3})\s*%");
                if (m.Success) double.TryParse(m.Groups[1].Value, out _progPct);
                var pt = _progPct;
                try { BeginInvoke(new Action(() => PushProgress(true, L.T("gui.download"), pt, t.Replace("[OK] ", ""), false))); } catch { }
            }
        }

        if (kind == "start")
        {
            if (line.Contains("系统已完成启动") || line.Contains("桌面已就绪"))
            {
                if (_startDoneReported) return;
                _startDoneReported = true;
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        PushProgress(true, "启动", 100, "启动完成", true);
                        MessageBox.Show("启动完成", "安卓虚拟机", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }));
                }
                catch { }
            }
            else if (line.Contains("模拟器窗口已显示") || line.Contains("虚拟机已启动"))
            {
                try { BeginInvoke(new Action(() => PushProgress(true, "启动", 0, line.Trim().Replace("[OK] ", ""), false, true))); } catch { }
            }
        }
    }

    private void FinishDownload(string? message)
    {
        if (_downloadDoneReported) return;
        _downloadDoneReported = true;
        _progPct = 100;
        PushProgress(true, L.T("gui.download"), 100, message ?? L.T("gui.downloaddone"), true);
    }

    private void BeginDownload(AndroidVersion version)
    {
        _downloadDoneReported = false;
        _progPct = 0;
        PushProgress(true, L.T("gui.download") + " " + version.Label, 0, L.T("gui.preparing"), false);
    }

    private void BeginStart(AndroidVersion version)
    {
        _startDoneReported = false;
        PushProgress(true, L.T("gui.start") + " " + version.Label, 0, L.T("gui.starting"), false, true);
    }

    /// <summary>启动某个版本：直接调用官方 emulator，窗口由 emulator 自己弹出。</summary>
    private void StartVersion(AndroidVersion version)
    {
        var avd = AndroidSdk.AvdNameFor(version);
        if (AndroidSdk.IsRunning(avd))
        {
            AndroidSdk.BringToFront(avd);
            PushProgress(true, version.Label, 100, "该版本已在运行", true);
            return;
        }
        _selected = version;
        BeginStart(version);
        RunHidden("-start " + version.Key, null, "start");
    }

    /// <summary>停止虚拟机；thenStart 为 true 时停止完成后再重启。</summary>
    private void StopVersion(AndroidVersion version, bool thenStart = false)
    {
        var avd = AndroidSdk.AvdNameFor(version);
        PushProgress(true, "停止 " + version.Label, 0, "正在关闭虚拟机…", false, true);
        Task.Run(() =>
        {
            try
            {
                var sdk = OpenSdk();
                sdk?.KillEmulatorForAvd(avd);
            }
            catch (Exception ex) { AppendLog("停止失败：" + ex.Message); }

            var sw = Stopwatch.StartNew();
            while (AndroidSdk.IsRunning(avd) && sw.Elapsed.TotalSeconds < 40)
                System.Threading.Thread.Sleep(500);

            var stopped = !AndroidSdk.IsRunning(avd);
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (!stopped)
                    {
                        PushProgress(true, "停止 " + version.Label, 0, "关闭超时，请查看日志", true);
                        return;
                    }
                    PushProgress(true, "停止", 100, thenStart ? "已关闭，正在重新启动…" : "已停止", true);
                    PushData();
                }));
            }
            catch { }
            if (thenStart && stopped)
            {
                System.Threading.Thread.Sleep(2500);
                try { BeginInvoke(new Action(() => StartVersion(version))); } catch { }
            }
        });
    }

    // 定时刷新“运行中”状态（最多 3 秒延迟）
    private System.Windows.Forms.Timer? _stateTimer;
    private string _runSignature = "";

    private void StartStateTimer()
    {
        if (_stateTimer != null) return;
        try
        {
            _runSignature = string.Join(",", Catalog.Versions
                .Where(v => IsInstalled(v) && AndroidSdk.IsRunning(AndroidSdk.AvdNameFor(v)))
                .Select(v => v.Key).OrderBy(x => x));
        }
        catch { }
        _stateTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _stateTimer.Tick += (_, _) =>
        {
            try
            {
                // 语言在设置里改了就跟着换（同进程即时，跨进程最迟 3 秒内）
                var langCode = Settings.Load().Language;
                if (!string.IsNullOrEmpty(langCode) && langCode != L.CodeOf(L.Current))
                {
                    L.Current = L.Parse(langCode);
                    Text = L.T("app");
                    PushData();
                    return;
                }
                var sig = string.Join(",", Catalog.Versions
                    .Where(v => IsInstalled(v) && AndroidSdk.IsRunning(AndroidSdk.AvdNameFor(v)))
                    .Select(v => v.Key).OrderBy(x => x));
                if (sig == _runSignature) return;
                _runSignature = sig;
                PushData();
            }
            catch { }
        };
        _stateTimer.Start();
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
            Text = L.T("gui.mirror"),
            Font = F("Microsoft YaHei UI", 10.5f, FontStyle.Bold),
            Location = new Point(20, 16),
            AutoSize = true,
        };
        var r1 = new RadioButton
        {
            Text = L.T("gui.mirror1"),
            Location = new Point(22, 54),
            AutoSize = true,
            Checked = true,
        };
        var r2 = new RadioButton
        {
            Text = L.T("gui.mirror2"),
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
            BackColor = Color.FromArgb(37, 99, 235),
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
            var next = new WebGuiWindow(_settings) { StartPosition = FormStartPosition.Manual, Location = Location };
            next.Show();
            Close();
        }
        else
        {
            PushData();
        }
    }

    private void PickAndInstallApk()
    {
        using var dialog = new OpenFileDialog { Filter = "APK (*.apk)|*.apk", Title = L.T("gui.install") };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        InstallApk(dialog.FileName);
    }

    private void InstallApk(string path)
    {
        var name = Path.GetFileName(path);
        AppendLog(L.T("skin.installing") + " " + name);
        PushProgress(true, "安装 " + name, 0, "正在连接虚拟机…", false, true);
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var result = await ApkInstaller.InstallAsync(path, _settings);
                if (!string.IsNullOrWhiteSpace(result.log)) AppendLog(result.log.Trim());
                AppendLog(result.message);
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        PushProgress(true, "安装 " + name, 100, result.message, true);
                        MessageBox.Show(result.message, "安卓虚拟机", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        PushData();
                    }));
                }
                catch { }
            }
            catch (Exception ex)
            {
                AppendLog("安装失败：" + ex.Message);
                try { BeginInvoke(new Action(() => PushProgress(true, "安装 " + name, 0, "安装失败：" + ex.Message, true))); } catch { }
            }
        });
    }

    internal void AutoCheckUpdate()
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
            catch { }
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

    private static Font F(string family, float size, FontStyle style = FontStyle.Regular)
        => new Font(family, size, style);
}
