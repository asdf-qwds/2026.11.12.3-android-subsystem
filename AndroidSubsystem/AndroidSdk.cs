using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AndroidSubsystem;

/// <summary>Android SDK（Android Studio 使用的那一套）的安装、镜像管理、AVD 创建与启动。</summary>
internal sealed class AndroidSdk
{
    // 官方 cmdline-tools（按新到旧尝试）
    private static readonly string[] CmdlineToolsUrls =
    {
        "https://dl.google.com/android/repository/commandlinetools-win-13114758_latest.zip",
        "https://dl.google.com/android/repository/commandlinetools-win-12266719_latest.zip",
        "https://dl.google.com/android/repository/commandlinetools-win-11076708_latest.zip",
        "https://dl.google.com/android/repository/commandlinetools-win-10406996_latest.zip",
    };

    private static readonly string[] JdkUrls =
    {
        "https://aka.ms/download-jdk/microsoft-jdk-17-windows-x64.zip",
        "https://aka.ms/download-jdk/microsoft-jdk-21-windows-x64.zip",
    };

    private const string DefaultDevice = "pixel_9_pro";

    /// <summary>
    /// 默认用软件渲染。理由：Windows 上 Intel 核显的 OpenGL 转发（Android Emulator OpenGL ES
    /// Translator）经常出现 "DisplaySurfaceGlContextHelper context was preempted"、
    /// "Failed to restore previous context" 等错误，表现为窗口开着但画面一片纯白。
    /// swiftshader_indirect 走 CPU 渲染，稳定但慢一些；显卡正常时可用 -gpu host / -gpu auto。
    /// </summary>
    public const string DefaultGpuMode = "swiftshader_indirect";

    private string? _javaHome;
    private bool _javaChecked;

    public AndroidSdk(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    public string CmdlineToolsDir => Path.Combine(Root, "cmdline-tools", "latest");
    public string SdkManagerBat => Path.Combine(CmdlineToolsDir, "bin", "sdkmanager.bat");
    public string AvdManagerBat => Path.Combine(CmdlineToolsDir, "bin", "avdmanager.bat");
    public string EmulatorExe => Path.Combine(Root, "emulator", "emulator.exe");
    public string AdbExe => Path.Combine(Root, "platform-tools", "adb.exe");
    public string JdkDir => Path.Combine(Root, "jdk");
    public string AvdHome => Path.Combine(Root, "avd");
    public string LogsDir => Path.Combine(Root, "logs");
    public string SystemImagesDir => Path.Combine(Root, "system-images");

    /// <summary>下载/解压的暂存目录，刻意放在 SDK 根目录下：与目标同卷，且不占用系统盘空间。</summary>
    public string StagingDir => Path.Combine(Root, ".staging");

    public bool HasCmdlineTools => File.Exists(SdkManagerBat);
    public bool HasEmulator => File.Exists(EmulatorExe);
    public bool HasAdb => File.Exists(AdbExe);

    public string JavaExe => _javaHome != null
        ? Path.Combine(_javaHome, "bin", "java.exe")
        : "java.exe";

    public Dictionary<string, string> Env()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ANDROID_SDK_ROOT"] = Root,
            ["ANDROID_HOME"] = Root,
            ["ANDROID_AVD_HOME"] = AvdHome,
            ["ANDROID_USER_HOME"] = Path.Combine(Root, "android-user"),
        };
        if (_javaHome != null) env["JAVA_HOME"] = _javaHome;

        // 保证子进程 PATH 至少包含系统目录与 JDK（批处理脚本里的 findstr 等系统命令依赖它）
        var sys32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");
        var parts = new List<string>();
        if (_javaHome != null) parts.Add(Path.Combine(_javaHome, "bin"));
        parts.Add(sys32);
        parts.Add(Path.Combine(sys32, "Wbem"));
        parts.Add(Environment.GetEnvironmentVariable("PATH") ?? "");
        env["PATH"] = string.Join(";", parts.Where(p => !string.IsNullOrEmpty(p)));
        return env;
    }

    // ---------------------------------------------------------------- 基础环境

    /// <summary>准备 JDK、cmdline-tools、platform-tools(adb)、emulator，并接受许可协议。</summary>
    public async Task EnsureBaseAsync()
    {
        Directory.CreateDirectory(Root);
        CleanStaging();
        await EnsureJavaAsync();
        await EnsureCmdlineToolsAsync();

        ConsoleUi.Step("安装 platform-tools（ADB 工作集）");
        await InstallPackagesAsync("platform-tools");

        ConsoleUi.Step("安装 emulator（Android Studio 虚拟机引擎）");
        await InstallPackagesAsync("emulator");

        ConsoleUi.Step("接受 Android SDK 许可协议");
        AcceptLicenses();

        ConsoleUi.Ok("基础组件就绪：" + Root);
    }

    public async Task EnsureJavaAsync()
    {
        var bundled = Path.Combine(JdkDir, "bin", "java.exe");
        if (File.Exists(bundled))
        {
            _javaHome = JdkDir;
            return;
        }
        if (_javaChecked) return;
        _javaChecked = true;

        ConsoleUi.Step("检查 Java 运行环境（sdkmanager 需要 JDK 17+）");

        int major = 0;
        try
        {
            var r = ProcessRunner.Run("java", new[] { "-version" }, timeoutMs: 20000);
            if (r.Success) major = ParseJavaMajor(r.StdErr.Length > 0 ? r.StdErr : r.StdOut);
        }
        catch { /* PATH 中没有 java */ }

        if (major >= 17)
        {
            ConsoleUi.Ok($"使用系统已安装的 JDK {major}");
            return;
        }

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(javaHome) && File.Exists(Path.Combine(javaHome, "bin", "java.exe")))
        {
            var r = ProcessRunner.Run(Path.Combine(javaHome, "bin", "java.exe"), new[] { "-version" }, timeoutMs: 20000);
            if (r.Success && ParseJavaMajor(r.StdErr) >= 17)
            {
                _javaHome = javaHome;
                ConsoleUi.Ok("使用 JAVA_HOME 指向的 JDK：" + javaHome);
                return;
            }
        }

        ConsoleUi.Info("未找到可用的 JDK，开始下载 Microsoft OpenJDK 17");
        string? url = null;
        foreach (var candidate in JdkUrls)
        {
            if (await Downloader.ExistsAsync(candidate)) { url = candidate; break; }
        }
        if (url == null) throw new InvalidOperationException("无法下载 JDK（网络不可达或被拦截）。");

        Directory.CreateDirectory(StagingDir);
        var zip = Path.Combine(StagingDir, "jdk.zip");
        await Downloader.DownloadAsync(url, zip, "JDK 17");

        var temp = Path.Combine(StagingDir, "jdk_" + Guid.NewGuid().ToString("N"));
        ZipFile.ExtractToDirectory(zip, temp, true);
        var home = FindJavaHome(temp);
        if (Directory.Exists(JdkDir)) Directory.Delete(JdkDir, true);
        MoveDirectory(home, JdkDir);
        TryDeleteDirectory(temp);
        try { File.Delete(zip); } catch { }

        _javaHome = JdkDir;
        ConsoleUi.Ok("JDK 已安装：" + JdkDir);
    }

    private async Task EnsureCmdlineToolsAsync()
    {
        if (HasCmdlineTools)
        {
            ConsoleUi.Ok("cmdline-tools 已就绪");
            return;
        }

        ConsoleUi.Step("下载 Android 命令行工具 cmdline-tools");
        string? url = null;
        foreach (var candidate in CmdlineToolsUrls)
        {
            if (await Downloader.ExistsAsync(candidate)) { url = candidate; break; }
        }
        if (url == null) throw new InvalidOperationException("无法下载 cmdline-tools（网络不可达或被拦截）。");

        Directory.CreateDirectory(StagingDir);
        var zip = Path.Combine(StagingDir, "cmdline-tools.zip");
        await Downloader.DownloadAsync(url, zip, "cmdline-tools");

        var temp = Path.Combine(StagingDir, "tools_" + Guid.NewGuid().ToString("N"));
        ZipFile.ExtractToDirectory(zip, temp, true);

        var src = Path.Combine(temp, "cmdline-tools");
        if (!Directory.Exists(src))
            throw new InvalidOperationException("cmdline-tools 压缩包结构异常。");

        var parent = Path.Combine(Root, "cmdline-tools");
        Directory.CreateDirectory(parent);
        var dest = CmdlineToolsDir;
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        MoveDirectory(src, dest);

        TryDeleteDirectory(temp);
        try { File.Delete(zip); } catch { }

        ConsoleUi.Ok("cmdline-tools 已安装：" + dest);
    }

    private void CleanStaging()
    {
        // 清理上次中断留下的暂存文件（仅在 .staging 目录内部）
        TryDeleteDirectory(StagingDir);
    }

    // ---------------------------------------------------------------- sdkmanager / avdmanager

    public ProcessResult SdkManager(IReadOnlyList<string> args, int timeoutMs = 0, Action<string>? onOut = null)
    {
        if (!HasCmdlineTools) throw new InvalidOperationException("cmdline-tools 尚未安装，请先执行 android.exe -setup");
        ResolveJavaFallback();
        return RunCli("sdkmanager-classpath.jar", "com.android.sdklib.tool.sdkmanager.SdkManagerCli",
                      "com.android.sdklib.toolsdir", args, timeoutMs, onOut);
    }

    private void ResolveJavaFallback()
    {
        if (_javaHome != null) return;
        if (File.Exists(Path.Combine(JdkDir, "bin", "java.exe"))) { _javaHome = JdkDir; return; }
        var jh = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(jh) && File.Exists(Path.Combine(jh, "bin", "java.exe")))
            _javaHome = jh;
    }

    /// <summary>
    /// 直接以 java 运行 cmdline-tools 的 CLI 主类。
    /// 不走 .bat：批处理依赖 findstr 等系统命令，PATH 不完整时会静默失败（并且返回 0）。
    /// 两个工具的 toolsdir 属性名不同。
    /// </summary>
    private ProcessResult RunCli(string jarName, string mainClass, string toolsDirProperty,
                                 IReadOnlyList<string> args, int timeoutMs, Action<string>? onOut)
    {
        var toolsDir = CmdlineToolsDir;
        var cliArgs = new List<string>
        {
            $"-D{toolsDirProperty}={Path.Combine(toolsDir, "bin", "..")}",
            "-classpath", Path.Combine(toolsDir, "lib", jarName),
            mainClass,
        };
        cliArgs.AddRange(args);

        // 许可证确认需要反复输入 y
        var yes = string.Concat(Enumerable.Repeat("y\n", 600));
        return ProcessRunner.Run(JavaExe, cliArgs, stdIn: yes, environment: Env(),
                                 timeoutMs: timeoutMs, onStdOut: onOut);
    }

    public void AcceptLicenses()
    {
        try { SdkManager(new[] { "--licenses" }, timeoutMs: 240000, onOut: null); }
        catch { /* 许可失败不阻断主流程 */ }
    }

    public async Task InstallPackagesAsync(params string[] packages)
    {
        foreach (var p in packages)
            ConsoleUi.Info("安装：" + p);

        var r = SdkManager(packages, timeoutMs: 0);
        if (!r.Success)
        {
            var tail = string.Join("\n", r.Combined.Split('\n').TakeLast(8));
            throw new InvalidOperationException($"sdkmanager 安装失败：{string.Join(", ", packages)}\n{tail}");
        }
        ConsoleUi.Ok("完成：" + string.Join(", ", packages));
        await Task.CompletedTask;
    }

    public ImageRepo ListImages()
    {
        if (!HasCmdlineTools) return ImageRepo.Empty;

        var r = SdkManager(new[] { "--list" }, timeoutMs: 300000);
        if (!r.Success)
        {
            ConsoleUi.Warn("读取镜像清单失败，将只显示本地已安装状态。");
            return ImageRepo.Empty;
        }

        var regex = new Regex(@"system-images;android-(\d+);([^;|\s]+);([^;|\s]+)", RegexOptions.Compiled);
        var found = new List<SystemImage>();
        foreach (var line in r.StdOut.Split('\n'))
        {
            var m = regex.Match(line);
            if (!m.Success) continue;
            found.Add(new SystemImage(m.Value, int.Parse(m.Groups[1].Value),
                                      m.Groups[2].Value.Trim(), m.Groups[3].Value.Trim()));
        }
        return ImageRepo.FromList(found);
    }

    public bool IsImageInstalled(SystemImage img)
    {
        var dir = Path.Combine(SystemImagesDir, $"android-{img.ApiLevel}", img.Tag, img.Ab i);
        if (Directory.Exists(dir) && Directory.EnumerateFiles(dir).Any()) return true;

        // 预览版镜像目录带小数位：android-37.0
        dir = Path.Combine(SystemImagesDir, $"android-{img.ApiLevel}.0", img.Tag, img.Ab i);
        return Directory.Exists(dir) && Directory.EnumerateFiles(dir).Any();
    }

    /// <summary>
    /// 为某个 Android 版本确定要用的系统镜像：本地已安装的优先（含 Android Studio 下载的
    /// preview 镜像目录 android-XX.0），其次才是官方在线清单里的。
    /// </summary>
    public SystemImage? ResolveImage(ImageRepo repo, AndroidVersion version)
    {
        var local = FindLocalImage(version.ApiLevel);
        if (local != null) return local;

        if (repo.IsLoaded)
        {
            var pick = repo.Pick(version.ApiLevel);
            if (pick != null) return pick;
        }
        return null;
    }

    /// <summary>扫描 <SDK根>\system-images\android-&lt;API&gt;[.0] 下已安装的镜像。</summary>
    public SystemImage? FindLocalImage(int apiLevel)
    {
        foreach (var apiDirName in new[] { $"android-{apiLevel}", $"android-{apiLevel}.0" })
        {
            var apiDir = Path.Combine(SystemImagesDir, apiDirName);
            if (!Directory.Exists(apiDir)) continue;

            foreach (var tag in new[] { "google_apis_playstore", "google_apis", "default" })
            {
                var hit = FirstAbiUnder(Path.Combine(apiDir, tag), apiLevel, apiDirName, tag);
                if (hit != null) return hit;
            }

            foreach (var tagDir in Directory.EnumerateDirectories(apiDir))
            {
                var tag = Path.GetFileName(tagDir);
                var hit = FirstAbiUnder(tagDir, apiLevel, apiDirName, tag);
                if (hit != null) return hit;
            }
        }
        return null;
    }

    /// <summary>包名要用真实的目录名（预览版是 android-37.0），否则 avdmanager 找不到镜像。</summary>
    private static SystemImage? FirstAbiUnder(string tagDir, int apiLevel, string apiDirName, string tag)
    {
        if (!Directory.Exists(tagDir)) return null;
        foreach (var abiDir in Directory.EnumerateDirectories(tagDir))
        {
            var abi = Path.GetFileName(abiDir);
            return new SystemImage($"system-images;{apiDirName};{tag};{abi}", apiLevel, tag, abi);
        }
        return null;
    }

    public async Task InstallImageAsync(SystemImage img)
    {
        ConsoleUi.Step($"下载系统镜像 {img.Package}");
        ConsoleUi.Info("体积较大（通常 1–2 GB），请保持网络畅通…");
        await InstallPackagesAsync(img.Package);
    }

    // ---------------------------------------------------------------- AVD

    public static string AvdNameFor(AndroidVersion v) =>
        "as_" + Regex.Replace(v.Key, "[^a-z0-9]", "_");

    public string AvdDirFor(string avdName) => Path.Combine(AvdHome, avdName + ".avd");
    public string AvdIniFor(string avdName) => Path.Combine(AvdHome, avdName + ".ini");

    public bool AvdExists(string avdName) =>
        Directory.Exists(AvdDirFor(avdName)) && File.Exists(AvdIniFor(avdName));

    public async Task EnsureAvdAsync(AndroidVersion version, SystemImage img)
    {
        var name = AvdNameFor(version);
        if (AvdExists(name))
        {
            ConsoleUi.Ok($"AVD 已存在：{name}");
            PatchAvdConfig(name);
            await Task.CompletedTask;
            return;
        }

        Directory.CreateDirectory(AvdHome);
        ConsoleUi.Step($"创建 AVD（{DefaultDevice}）");

        string lastError = "";
        var devices = new[] { DefaultDevice, "pixel_9", "pixel_8_pro", "pixel_7_pro" };
        foreach (var device in devices)
        {
            var args = new List<string>
            {
                "create", "avd",
                "--force",
                "--name", name,
                "--package", img.Package,
                "--device", device,
            };
            var r = RunCli("avdmanager-classpath.jar", "com.android.sdklib.tool.AvdManagerCli",
                           "com.android.sdkmanager.toolsdir", args, 180000, null);
            if (r.Success && AvdExists(name))
            {
                ConsoleUi.Ok($"AVD 已创建：{name}（设备型号 {device}）");
                PatchAvdConfig(name);
                return;
            }
            lastError = r.Combined;
        }

        throw new InvalidOperationException("创建 AVD 失败：\n" + lastError);
    }

    /// <summary>按当前磁盘剩余空间决定 userdata 分区大小，避免模拟器因空间不足直接 FATAL。</summary>
    private string PickDataPartitionSize()
    {
        long freeMb = 4096;
        try
        {
            var root = Path.GetPathRoot(Root) ?? "C:\\";
            freeMb = new DriveInfo(root).AvailableFreeSpace / (1024 * 1024);
        }
        catch { }

        // 模拟器还需要额外的系统/缓存分区空间，预留约 2 GB
        var usable = freeMb - 2000;
        var size = Math.Clamp(usable, 1024, 8192);
        size = size / 512 * 512;
        return size + "M";
    }

    private void PatchAvdConfig(string avdName)
    {
        var cfg = Path.Combine(AvdDirFor(avdName), "config.ini");
        if (!File.Exists(cfg)) return;

        var overrides = new (string key, string value)[]
        {
            ("hw.ramSize", "4096"),
            ("vm.heapSize", "512"),
            ("hw.cpu.ncore", "4"),
            ("hw.keyboard", "yes"),
            ("hw.gpu.enabled", "yes"),
            ("hw.gpu.mode", "auto"),
            ("disk.dataPartition.size", PickDataPartitionSize()),
            ("showDeviceFrame", "yes"),
        };

        var keep = new List<string>();
        foreach (var line in File.ReadAllLines(cfg))
        {
            var key = line.Split('=')[0].Trim();
            if (overrides.Any(o => o.key == key)) continue;
            keep.Add(line);
        }
        foreach (var o in overrides) keep.Add($"{o.key}={o.value}");
        File.WriteAllLines(cfg, keep);
    }

    // ---------------------------------------------------------------- 启动

    public Process LaunchEmulator(string avdName, string gpu = DefaultGpuMode)
    {
        if (!HasEmulator) throw new InvalidOperationException("emulator 未安装，请先执行 android.exe -setup");

        Directory.CreateDirectory(AvdHome);
        Directory.CreateDirectory(LogsDir);
        PatchAvdConfig(avdName);

        var args = new List<string>
        {
            "-avd", avdName,
            "-gpu", gpu,
            "-netdelay", "none",
            "-netspeed", "full",
            "-no-boot-anim",
        };

        // 快照与渲染器绑定：GPU 模式变了还去加载旧快照，画面会停在旧帧或一片白/灰。
        // 因此记录上次使用的模式，模式变化（或首次不知道模式）时清掉快照，强制冷启动。
        var avdDir = AvdDirFor(avdName);
        var marker = Path.Combine(avdDir, ".gpu_mode");
        var snapshots = Path.Combine(avdDir, "snapshots");
        string? lastGpu = File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
        var snapshotsExist = Directory.Exists(snapshots) && Directory.EnumerateFileSystemEntries(snapshots).Any();
        if (snapshotsExist && !string.Equals(lastGpu, gpu, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteDirectory(snapshots);
            args.Add("-no-snapshot-load");
            ConsoleUi.Info(lastGpu == null
                ? "发现旧快照但不知道它用的图形模式，已清除并冷启动。"
                : $"图形模式由 {lastGpu} 改为 {gpu}，已清除旧快照并冷启动。");
        }
        try
        {
            Directory.CreateDirectory(avdDir);
            File.WriteAllText(marker, gpu);
        }
        catch { }

        var log = Path.Combine(LogsDir, $"emulator-{avdName}.log");
        return ProcessRunner.StartDetached(EmulatorExe, args, Env(), log, hideWindow: false);
    }

    public ProcessResult Adb(params string[] args) =>
        ProcessRunner.Run(AdbExe, args, environment: Env(), timeoutMs: 60000);

    /// <summary>
    /// 等待模拟器主窗口出现，并强制把它显示到前台、必要时挪回屏幕内。
    /// 窗口属于 qemu-system-x86_64 子进程，句柄可能被隐藏（任务栏有图标却看不到界面）。
    /// 返回值：找到的窗口描述（标题与坐标），未找到返回 null。
    /// </summary>
    public static async Task<string?> EnsureWindowVisibleAsync(string avdName, int seconds = 60)
    {
        for (var i = 0; i < seconds; i++)
        {
            var handles = WindowHelper.FindByTitle("Android Emulator - " + avdName);
            if (handles.Count == 0 && i > 10) handles = WindowHelper.FindByTitle(avdName);
            if (handles.Count > 0)
            {
                foreach (var h in handles) WindowHelper.ShowAndActivate(h);
                await Task.Delay(1500);
                return WindowHelper.Describe(handles[0]);
            }
            await Task.Delay(1000);
        }
        return null;
    }

    /// <summary>该 AVD 是否已经有正在运行的实例（窗口已存在即视为运行中）。</summary>
    public static bool IsRunning(string avdName) =>
        WindowHelper.FindByTitle("Android Emulator - " + avdName).Count > 0;

    public static void BringToFront(string avdName)
    {
        foreach (var h in WindowHelper.FindByTitle("Android Emulator - " + avdName))
            WindowHelper.ShowAndActivate(h);
    }

    public async Task<bool> WaitForBootAsync(Process? emulator, int timeoutSeconds = 180)
    {
        if (!HasAdb) return false;

        ConsoleUi.Info("等待系统启动完成…");
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < timeoutSeconds)
        {
            if (emulator != null)
            {
                try
                {
                    if (emulator.HasExited)
                    {
                        ConsoleUi.Error("模拟器进程已退出，请查看日志目录：" + LogsDir);
                        return false;
                    }
                }
                catch { }
            }

            try
            {
                var r = Adb("shell", "getprop", "sys.boot_completed");
                if (r.Success && r.StdOut.Trim().Contains('1'))
                    return true;
            }
            catch { }
            await Task.Delay(3000);
        }
        return false;
    }

    // ---------------------------------------------------------------- 启动后健康检查

    /// <summary>虚拟机启动后是否真的活着（能进桌面），还是陷入崩溃重启循环。</summary>
    internal sealed class GuestHealth
    {
        public bool AdbReachable;
        public double UptimeSeconds;
        public bool Rebooted;
        public int SurfaceFlingerCrashes;
        public bool DmaReadbackAssertion;

        public bool Healthy => AdbReachable && !Rebooted && SurfaceFlingerCrashes == 0;
    }

    /// <summary>
    /// boot_completed 之后系统仍可能立刻崩溃重启（典型：SurfaceFlinger 在 RegionSampling 线程
    /// 断言失败 SIGABRT，launcher 跟着拿 DEAD_OBJECT，画面白屏并反复重启）。
    /// 判断依据是"运行时长是否被重置 + crash 缓冲里有没有 SurfaceFlinger 的致命信号"，
    /// 而不是绝对时长（刚起来才一两分钟的虚拟机本来就该是几十秒）。
    /// </summary>
    public async Task<GuestHealth> CheckGuestHealthAsync(int settleSeconds = 45)
    {
        var h = new GuestHealth();

        var before = ReadUptime();
        await Task.Delay(settleSeconds * 1000);
        var after = ReadUptime();

        h.AdbReachable = after > 0;
        h.UptimeSeconds = after;
        // 等待 settleSeconds 后运行时长没涨够，说明中途重启过
        if (before > 0 && after > 0 && after < before + settleSeconds * 0.85) h.Rebooted = true;

        var crash = Adb("shell", "logcat", "-d", "-b", "crash", "-t", "150");
        foreach (var line in crash.StdOut.Split('\n'))
        {
            if (line.Contains("Fatal signal") && line.Contains("surfaceflinger")) h.SurfaceFlingerCrashes++;
            if (line.Contains("hasReadColorBufferDma")) h.DmaReadbackAssertion = true;
        }
        return h;
    }

    private double ReadUptime()
    {
        var up = Adb("shell", "cat", "/proc/uptime");
        if (!up.Success) return 0;
        var tok = up.StdOut.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (tok.Length == 0) return 0;
        return double.TryParse(tok[0], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    // ---------------------------------------------------------------- 文件移动（跨卷安全）

    /// <summary>
    /// 目录移动：同卷走 Directory.Move；跨卷（会抛 "Source and destination path must have
    /// identical roots. Move will not work across volumes."）则退化为递归复制后删除源目录。
    /// </summary>
    private static void MoveDirectory(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
            return;
        }
        catch (IOException) { /* 跨卷或不支持移动，走复制 */ }
        catch (UnauthorizedAccessException) { }

        CopyDirectory(source, destination);
        TryDeleteDirectory(source);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch { /* 残留不影响使用，下次启动会清理 */ }
    }

    // ---------------------------------------------------------------- 工具

    private static int ParseJavaMajor(string text)
    {
        var m = Regex.Match(text, "\"([0-9]+)");
        if (m.Success) return int.Parse(m.Groups[1].Value);
        m = Regex.Match(text, "version\\s+([0-9]+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    private static string FindJavaHome(string root)
    {
        foreach (var exe in Directory.EnumerateFiles(root, "java.exe", SearchOption.AllDirectories))
        {
            var bin = Path.GetDirectoryName(exe);
            if (bin == null) continue;
            if (string.Equals(Path.GetFileName(bin), "bin", StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(bin)!;
        }
        throw new InvalidOperationException("JDK 压缩包中未找到 bin\\java.exe。");
    }
}
