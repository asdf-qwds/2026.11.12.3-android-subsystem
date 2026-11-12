using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AndroidSubsystem;

internal static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        var tokens = args.Where(t => t.Trim().TrimStart('-', '/').Trim().Length > 0).ToArray();
        if (tokens.Length == 0) return await Wizard.RunAsync(null);

        var cmd = tokens[0].Trim().TrimStart('-', '/').ToLowerInvariant();
        var rest = tokens.Skip(1).ToArray();

        switch (cmd)
        {
            case "help":
            case "h":
            case "?":
                Help();
                return 0;

            case "list":
            case "ls":
                return await ListAsync();

            case "download":
            case "dl":
                return await DownloadAsync(rest);

            case "start":
            case "run":
                return await StartAsync(rest);

            case "setup":
            case "init":
                return await Wizard.RunAsync(rest.FirstOrDefault());

            case "stop":
                return Stop();

            case "where":
                return Where();

            default:
                ConsoleUi.Error($"未知命令：{tokens[0]}");
                Console.WriteLine();
                Help();
                return 2;
        }
    }

    // ---------------------------------------------------------------- help

    public static void Help()
    {
        Console.WriteLine(@"用法：android.exe [命令] [参数]

  （不带参数）                 首次运行向导：选择下载目录 → 安装 emulator/ADB → 选择 Android 版本
  -setup [目录]                同上，可指定下载目录
  -list  [android]             列出 Android 2.2 ~ Android 17 的全部版本及安装状态
  -download <版本>             下载该版本的系统镜像，例如 -download android17
  -download all                下载官方仓库中全部可用的系统镜像（体积非常大）
  -start <版本>                创建/启动该版本虚拟机（默认 Pixel 9 Pro）
  -start <版本> -gpu <模式>    指定图形模式：swiftshader_indirect（默认，兼容性最好）
                               host / auto（显卡渲染快，但部分 Intel 核显会白屏）
                               angle_indirect / guest
  -stop                        关闭所有正在运行的模拟器
  -where                       显示当前下载目录与配置位置

版本写法：android17、17、android2.2、android5.0 ……

示例：
  android.exe -list android
  android.exe -download android17
  android.exe -start android17");
    }

    // ---------------------------------------------------------------- list

    private static async Task<int> ListAsync()
    {
        var settings = Settings.Load();
        string? root = settings.SdkRoot;

        if (root == null || !Directory.Exists(root))
        {
            ConsoleUi.Warn("尚未初始化下载目录，以下为内置版本清单。");
            ConsoleUi.Info("先执行 android.exe -setup 选择下载目录，即可显示每个版本的镜像可下载 / 已安装状态。");
            Console.WriteLine();
            PrintTable(Catalog.Versions, new Dictionary<string, string>(), null);
            return 0;
        }

        var sdk = new AndroidSdk(root);
        ImageRepo repo = ImageRepo.Empty;
        if (sdk.HasCmdlineTools)
        {
            await sdk.EnsureJavaAsync();
            ConsoleUi.Info("正在读取 Android SDK 镜像仓库清单…");
            repo = await Task.Run(() => sdk.ListImages());
        }

        ConsoleUi.Info("下载目录：" + root);
        if (!repo.IsLoaded)
            ConsoleUi.Warn("未能读取在线镜像清单，仅显示本地已安装状态。");

        var status = new Dictionary<string, string>();
        foreach (var v in Catalog.Versions)
            status[v.Key] = DescribeStatus(sdk, repo, v);

        Console.WriteLine();
        PrintTable(Catalog.Versions, status, AndroidSdk.AvdNameFor);
        Console.WriteLine();
        ConsoleUi.Info($"共 {Catalog.Versions.Count} 个版本（Android 2.2 / API 8 → Android 17 / API 37）");
        return 0;
    }

    private static void PrintTable(IReadOnlyList<AndroidVersion> versions,
                                   IReadOnlyDictionary<string, string> status,
                                   Func<AndroidVersion, string>? avdName)
    {
        Console.WriteLine($"{"版本键",-14}{"Android 版本",-28}{"API",-6}{"状态",-26}{(avdName != null ? "AVD" : "")}");
        Console.WriteLine(new string('-', 78));
        foreach (var v in versions)
        {
            var st = status.TryGetValue(v.Key, out var s) ? s : "";
            var avd = avdName == null ? "" : avdName(v);
            Console.WriteLine($"{v.Key,-14}{v.Label,-28}{v.ApiLevel,-6}{st,-26}{avd}");
        }
    }

    private static string DescribeStatus(AndroidSdk sdk, ImageRepo repo, AndroidVersion v)
    {
        var img = FindImage(sdk, repo, v);
        if (img == null) return repo.IsLoaded ? "不支持（官方无镜像）" : "未知";
        return sdk.IsImageInstalled(img) ? "已安装" : "可下载";
    }

    // ---------------------------------------------------------------- download

    private static async Task<int> DownloadAsync(string[] rest)
    {
        var target = rest.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(target))
        {
            ConsoleUi.Error("缺少版本参数。示例：android.exe -download android17");
            return 2;
        }

        var settings = Settings.Load();
        if (settings.SdkRoot == null)
        {
            ConsoleUi.Error("尚未设置下载目录，请先运行：android.exe -setup");
            return 1;
        }

        var sdk = new AndroidSdk(settings.SdkRoot);
        await sdk.EnsureJavaAsync();
        if (!sdk.HasCmdlineTools || !sdk.HasEmulator || !sdk.HasAdb)
            await sdk.EnsureBaseAsync();

        if (target.Equals("all", StringComparison.OrdinalIgnoreCase) || target == "*")
            return await DownloadAllAsync(sdk);

        var version = Catalog.ResolveOrThrow(target);
        var repo = await Task.Run(() => sdk.ListImages());
        var img = FindImage(sdk, repo, version);
        if (img == null)
        {
            ConsoleUi.Error($"{version.Label}（API {version.ApiLevel}）在官方仓库中没有可用的系统镜像。");
            ConsoleUi.Info("可用 -list android 查看状态为“可下载”的版本。");
            return 1;
        }

        if (sdk.IsImageInstalled(img))
        {
            ConsoleUi.Ok($"镜像已安装：{img.Package}");
            return 0;
        }

        await sdk.InstallImageAsync(img);
        return 0;
    }

    private static async Task<int> DownloadAllAsync(AndroidSdk sdk)
    {
        var repo = await Task.Run(() => sdk.ListImages());
        if (!repo.IsLoaded)
        {
            ConsoleUi.Error("无法读取镜像仓库清单，请检查网络后重试。");
            return 1;
        }

        var items = Catalog.Versions
            .Select(v => (version: v, image: FindImage(sdk, repo, v)))
            .Where(p => p.image != null)
            .ToList();

        var pending = items.Where(p => !sdk.IsImageInstalled(p.image!)).ToList();
        ConsoleUi.Warn($"官方仓库中共有 {items.Count} 个版本可用镜像，其中 {pending.Count} 个未安装。");
        ConsoleUi.Warn("每个镜像通常 1–2 GB，全部下载可能占用数百 GB 磁盘空间。");
        if (!ConsoleUi.Confirm("确认开始下载全部镜像？", false))
        {
            ConsoleUi.Info("已取消。");
            return 0;
        }

        int failed = 0;
        foreach (var (version, image) in pending)
        {
            try
            {
                await sdk.InstallImageAsync(image!);
            }
            catch (Exception ex)
            {
                failed++;
                ConsoleUi.Error($"{version.Label} 下载失败：{ex.Message}");
            }
        }

        ConsoleUi.Ok($"全部处理完成，失败 {failed} 个。");
        return failed == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- start

    private static async Task<int> StartAsync(string[] rest)
    {
        // 可选：-gpu host / -gpu auto / -gpu swiftshader_indirect / -gpu angle_indirect
        var gpuIndex = Array.FindIndex(rest, t => t.Equals("gpu", StringComparison.OrdinalIgnoreCase)
                                                 || t.StartsWith("gpu=", StringComparison.OrdinalIgnoreCase));
        var gpu = AndroidSdk.DefaultGpuMode;
        var target = rest.FirstOrDefault();

        if (gpuIndex >= 0)
        {
            var token = rest[gpuIndex];
            var hasValue = gpuIndex + 1 < rest.Length;
            gpu = token.Contains('=') ? token.Split('=').Last()
                  : (hasValue ? rest[gpuIndex + 1] : gpu);
            target = rest.Where((t, i) => i != gpuIndex && !(hasValue && i == gpuIndex + 1)).FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            ConsoleUi.Error("缺少版本参数。示例：android.exe -start android17");
            return 2;
        }

        var settings = Settings.Load();
        if (settings.SdkRoot == null)
        {
            ConsoleUi.Error("尚未设置下载目录，请先运行：android.exe -setup");
            return 1;
        }

        var sdk = new AndroidSdk(settings.SdkRoot);
        await sdk.EnsureJavaAsync();
        if (!sdk.HasCmdlineTools || !sdk.HasEmulator || !sdk.HasAdb)
            await sdk.EnsureBaseAsync();

        var version = Catalog.ResolveOrThrow(target);
        var repo = await Task.Run(() => sdk.ListImages());
        var img = FindImage(sdk, repo, version);
        if (img == null)
        {
            ConsoleUi.Error($"{version.Label}（API {version.ApiLevel}）没有可用的系统镜像。");
            return 1;
        }

        if (!sdk.IsImageInstalled(img))
        {
            ConsoleUi.Warn($"镜像尚未下载：{img.Package}");
            if (!ConsoleUi.Confirm("是否现在下载？", true))
            {
                ConsoleUi.Info($"请先执行：android.exe -download {version.Key}");
                return 1;
            }
            await sdk.InstallImageAsync(img);
        }

        await sdk.EnsureAvdAsync(version, img);

        var avdName = AndroidSdk.AvdNameFor(version);

        // 已经有一个实例在跑（窗口已存在）：直接把窗口拉回前台，避免再开一个必然失败的进程
        if (AndroidSdk.IsRunning(avdName))
        {
            AndroidSdk.BringToFront(avdName);
            ConsoleUi.Ok($"{version.Label} 虚拟机已在运行，已把窗口切到前台（AVD: {avdName}）。");
            return 0;
        }

        ConsoleUi.Info("图形模式：" + gpu + "（显卡渲染异常导致画面发白时，可加 -gpu swiftshader_indirect）");
        if (version.ApiLevel >= 37)
            ConsoleUi.Warn("注意：Android 17 是预览版镜像，在 emulator 上存在已知缺陷（SurfaceFlinger RegionSampling 读回断言崩溃），"
                + "可能白屏并反复重启；实测可正常进桌面的是 android16。");
        var proc = sdk.LaunchEmulator(avdName, gpu);

        ConsoleUi.Ok($"{version.Label} 虚拟机已启动（AVD: {avdName}，机型: Pixel 9 Pro）");

        var windowInfo = await AndroidSdk.EnsureWindowVisibleAsync(avdName, 60);
        if (windowInfo != null)
            ConsoleUi.Ok("模拟器窗口已显示：" + windowInfo);
        else
            ConsoleUi.Warn("未检测到模拟器窗口，请查看日志：" + Path.Combine(sdk.LogsDir, $"emulator-{avdName}.log"));

        var booted = await sdk.WaitForBootAsync(proc, 240);
        if (booted) ConsoleUi.Ok("系统已完成启动，可通过 adb 连接。");
        else ConsoleUi.Warn("未能确认启动完成，请查看日志目录。");

        if (booted)
        {
            ConsoleUi.Info("检查虚拟机是否真的进入了桌面（约 45 秒）…");
            var health = await sdk.CheckGuestHealthAsync(45);
            if (health.Healthy)
            {
                ConsoleUi.Ok("虚拟机运行正常，桌面已就绪。");
            }
            else
            {
                ConsoleUi.Error("虚拟机没有真正进入桌面：系统在反复崩溃重启。");
                ConsoleUi.Warn($"运行时长 {health.UptimeSeconds:F0} 秒"
                    + (health.Rebooted ? "（检查期间被重置，说明重启过）" : "")
                    + $"，SurfaceFlinger 崩溃 {health.SurfaceFlingerCrashes} 次");
                if (health.DmaReadbackAssertion)
                {
                    ConsoleUi.Warn("崩溃断言：!rcEnc->featureInfo()->hasReadColorBufferDma（SurfaceFlinger RegionSampling 读回）");
                    ConsoleUi.Warn("这是该版本系统镜像与 emulator 的上游缺陷，与本工具无关，改图形模式也无法规避。");
                }
                ConsoleUi.Info(version.ApiLevel >= 37
                    ? "请改用其它版本：android.exe -start android16（实测可正常进桌面）"
                    : "请换一个版本再试：android.exe -list android");

                // 预览版镜像崩成这样基本没救：直接改成启动能用的同代稳定版本（Android 16）。
                var fallback = Catalog.ByApi(36);
                if (version.ApiLevel >= 37 && fallback != null && !fallback.Key.Equals(version.Key, StringComparison.OrdinalIgnoreCase))
                {
                    ConsoleUi.Warn($"自动改用 {fallback.Label}（API {fallback.ApiLevel}）启动。");
                    try { sdk.Adb("emu", "kill"); } catch { }
                    await Task.Delay(6000);
                    return await StartAsync(new[] { fallback.Key });
                }

                return 3;
            }
        }

        return 0;
    }

    // ---------------------------------------------------------------- stop / where

    private static int Stop()
    {
        var settings = Settings.Load();
        if (settings.SdkRoot == null)
        {
            ConsoleUi.Error("尚未设置下载目录，请先运行：android.exe -setup");
            return 1;
        }

        var sdk = new AndroidSdk(settings.SdkRoot);
        if (!sdk.HasAdb)
        {
            ConsoleUi.Error("platform-tools 未安装。");
            return 1;
        }

        var r = sdk.Adb("devices");
        var serials = r.StdOut.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("emulator-") && l.Contains("device"))
            .Select(l => l.Split('\t')[0].Trim())
            .ToList();

        if (serials.Count == 0)
        {
            ConsoleUi.Info("没有正在运行的模拟器。");
            return 0;
        }

        foreach (var serial in serials)
        {
            sdk.Adb("-s", serial, "emu", "kill");
            ConsoleUi.Ok("已关闭：" + serial);
        }
        return 0;
    }

    private static int Where()
    {
        var settings = Settings.Load();
        ConsoleUi.Info("下载目录：" + (settings.SdkRoot ?? "（未设置）"));
        ConsoleUi.Info("配置文件：" + Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AndroidSubsystem", "settings.json"));
        return 0;
    }

    // ---------------------------------------------------------------- 镜像查找

    /// <summary>本地已安装的镜像优先，其次官方在线清单。</summary>
    private static SystemImage? FindImage(AndroidSdk sdk, ImageRepo repo, AndroidVersion version)
        => sdk.ResolveImage(repo, version);
}
