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

            case "del":
            case "delete":
            case "rm":
            case "uninstall":
                return await DeleteAsync(rest);

            case "start":
            case "run":
                return await StartAsync(rest);

            case "setup":
            case "init":
                return await Wizard.RunAsync(rest.FirstOrDefault());

            case "update":
            case "up":
                return await Updater.RunAsync();

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
                               会先让你选镜像类型：1 google_apis / 2 google_apis_playstore
  -download <版本> 1|2         直接指定镜像类型，跳过询问（1 google_apis，2 google_apis_playstore）
  -download all                下载官方仓库中该类型全部可用的系统镜像（体积非常大，同样先选类型）
  -del <版本>                  删除该版本的镜像与虚拟机数据（快照、userdata、AVD、日志），例如 -del android17
  -start <版本>                创建/启动该版本虚拟机（默认 Pixel 9 Pro）
  -start <版本> -gpu <模式>    指定图形模式：swiftshader_indirect（默认，兼容性最好）
                               host / auto（显卡渲染快，但部分 Intel 核显会白屏）
                               angle_indirect / guest
  -stop                        关闭所有正在运行的模拟器
  -update                      检查 GitHub 上的新版本：有更新会提示 Y/N，Y 则下载并替换当前 exe
  -where                       显示当前下载目录与配置位置

版本写法：android17、17、android2.2、android5.0 ……

示例：
  android.exe -list android
  android.exe -download android17
  android.exe -download android16 2
  android.exe -start android17
  android.exe -del android17
  android.exe -update");
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
        var text = $"{"版本键",-14}{"Android 版本",-28}{"API",-6}{"状态",-26}";
        Console.WriteLine(avdName == null ? text : text + "AVD");
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

    private const string TagGoogleApis = "google_apis";
    private const string TagPlaystore = "google_apis_playstore";

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
            return await DownloadAllAsync(sdk, TagHintFrom(rest, 1));

        var version = Catalog.ResolveOrThrow(target);
        var repo = await Task.Run(() => sdk.ListImages());
        var tag = AskImageTag(version, repo, TagHintFrom(rest, 1));
        var img = PickTaggedImage(sdk, repo, version, tag);
        if (img == null)
        {
            ConsoleUi.Error($"{version.Label}（API {version.ApiLevel}）在官方仓库中没有 {tag} 镜像。");
            ConsoleUi.Info("换成另一种镜像类型再试，或用 -list android 查看该版本可下载的状态。");
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

    /// <summary>从命令行参数里取镜像类型提示：-download android17 1 / 2 / google_apis / playstore。</summary>
    private static string? TagHintFrom(string[] rest, int index)
    {
        if (rest.Length <= index) return null;
        var hint = rest[index];
        if (hint.Equals("tag", StringComparison.OrdinalIgnoreCase) || hint.Equals("--tag", StringComparison.OrdinalIgnoreCase))
            return rest.Length > index + 1 ? rest[index + 1] : null;
        return hint;
    }

    private static string? NormalizeTag(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint)) return null;
        switch (hint.Trim().TrimStart('-').ToLowerInvariant())
        {
            case "1":
            case "g":
            case "google":
            case "api":
            case "apis":
            case "google_apis":
            case "googleapis":
                return TagGoogleApis;
            case "2":
            case "p":
            case "play":
            case "store":
            case "playstore":
            case "google_play":
            case "google_apis_playstore":
                return TagPlaystore;
            default:
                return null;
        }
    }

    /// <summary>询问要哪种镜像：1 google_apis，2 google_apis_playstore。</summary>
    private static string AskImageTag(AndroidVersion version, ImageRepo repo, string? hint, string? purpose = null)
    {
        var forced = NormalizeTag(hint);
        if (forced != null)
        {
            ConsoleUi.Info($"镜像类型（命令行指定）：{forced}");
            return forced;
        }

        if (repo.IsLoaded)
        {
            var hasApis = repo.HasTag(version.ApiLevel, TagGoogleApis);
            var hasPlay = repo.HasTag(version.ApiLevel, TagPlaystore);
            if (hasApis && !hasPlay)
            {
                ConsoleUi.Info($"该版本只有 {TagGoogleApis} 镜像，已自动选择。");
                return TagGoogleApis;
            }
            if (hasPlay && !hasApis)
            {
                ConsoleUi.Info($"该版本只有 {TagPlaystore} 镜像，已自动选择。");
                return TagPlaystore;
            }
        }

        ConsoleUi.Step(purpose ?? $"选择镜像类型：{version.Label}（API {version.ApiLevel}）");
        ConsoleUi.Info("1. google_apis            官方系统镜像，无 Play 商店（调试友好，可自由安装 APK）");
        ConsoleUi.Info("2. google_apis_playstore  同一官方下载源的镜像，内置 Google Play 商店");
        ConsoleUi.Info("两者都从 Android Studio 官方镜像仓库下载，只是 tag 不同；旧版本可能只有其中一种。");
        var pick = ConsoleUi.Choose("请输入序号", 2, 1);
        return pick == 2 ? TagPlaystore : TagGoogleApis;
    }

    /// <summary>按指定 tag 找镜像：本地已装的那种优先，其次在线清单。</summary>
    private static SystemImage? PickTaggedImage(AndroidSdk sdk, ImageRepo repo, AndroidVersion version, string tag)
    {
        var local = sdk.FindLocalImage(version.ApiLevel, tag);
        if (local != null) return local;
        return repo.IsLoaded ? repo.PickTagged(version.ApiLevel, tag) : null;
    }

    private static async Task<int> DownloadAllAsync(AndroidSdk sdk, string? tagHint = null)
    {
        var repo = await Task.Run(() => sdk.ListImages());
        if (!repo.IsLoaded)
        {
            ConsoleUi.Error("无法读取镜像仓库清单，请检查网络后重试。");
            return 1;
        }

        var tag = NormalizeTag(tagHint);
        if (tag == null)
        {
            ConsoleUi.Step("选择镜像类型（全部版本共用）");
            ConsoleUi.Info("1. google_apis            官方系统镜像，无 Play 商店");
            ConsoleUi.Info("2. google_apis_playstore  同一官方下载源的镜像，内置 Google Play 商店");
            tag = ConsoleUi.Choose("请输入序号", 2, 1) == 2 ? TagPlaystore : TagGoogleApis;
        }
        else
        {
            ConsoleUi.Info("镜像类型（命令行指定）：" + tag);
        }

        var items = Catalog.Versions
            .Select(v => (version: v, image: PickTaggedImage(sdk, repo, v, tag)))
            .Where(p => p.image != null)
            .ToList();

        var pending = items.Where(p => !sdk.IsImageInstalled(p.image!)).ToList();
        ConsoleUi.Warn($"官方仓库中共有 {items.Count} 个版本有 {tag} 镜像，其中 {pending.Count} 个未安装。");
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

    // ---------------------------------------------------------------- delete

    /// <summary>删除某个版本的全部本地数据：系统镜像 + AVD（快照/userdata）+ 配置 + 日志。</summary>
    private static async Task<int> DeleteAsync(string[] rest)
    {
        var target = rest.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(target))
        {
            ConsoleUi.Error("缺少版本参数。示例：android.exe -del android17");
            return 2;
        }

        var settings = Settings.Load();
        if (settings.SdkRoot == null)
        {
            ConsoleUi.Error("尚未设置下载目录，请先运行：android.exe -setup");
            return 1;
        }

        var version = Catalog.ResolveOrThrow(target);
        var sdk = new AndroidSdk(settings.SdkRoot);
        var avdName = AndroidSdk.AvdNameFor(version);

        // 只看本地，不读在线清单：删东西不需要联网
        var img = sdk.FindLocalImage(version.ApiLevel);
        var imageInstalled = img != null && sdk.IsImageInstalled(img);
        var avdExists = sdk.AvdExists(avdName);

        ConsoleUi.Step($"删除 {version.Label}（API {version.ApiLevel}）");
        ConsoleUi.Info(version.Label + " 的镜像与虚拟机数据");

        if (!imageInstalled && !avdExists && !Directory.Exists(sdk.AvdDirFor(avdName)))
        {
            ConsoleUi.Warn($"{version.Label}（API {version.ApiLevel}）当前没有安装，无需删除。");
            ConsoleUi.Info("-list android 中该版本显示为「可下载」；要安装请执行：android.exe -download " + version.Key);
            return 0;
        }

        long bytes = 0;
        foreach (var p in sdk.VersionPaths(version)) bytes += AndroidSdk.DirectorySize(p);
        var sizeText = bytes >= 1024L * 1024 * 1024
            ? $"{bytes / 1024.0 / 1024 / 1024:F2} GB"
            : $"{bytes / 1024.0 / 1024:F1} MB";

        ConsoleUi.Info("将删除以下内容：");
        if (imageInstalled) ConsoleUi.Info("  系统镜像：" + (img?.Package ?? "system-images\\android-" + version.ApiLevel));
        else ConsoleUi.Info("  系统镜像：（未安装）");
        ConsoleUi.Info("  虚拟机 AVD：" + avdName + (avdExists ? "" : "（不存在）"));
        ConsoleUi.Info("  占用约 " + sizeText);

        if (!ConsoleUi.Confirm("删除后要重新下载镜像（约 1–2 GB）才能再用，确认删除？", false))
        {
            ConsoleUi.Info("已取消，没有删除任何内容。");
            return 0;
        }

        var deleted = await Task.Run(() => sdk.DeleteVersionData(version, imageInstalled ? img?.Package : null));

        ConsoleUi.Step("复核");
        var stillImage = sdk.FindLocalImage(version.ApiLevel);
        var stillInstalled = stillImage != null && sdk.IsImageInstalled(stillImage);
        var stillAvd = sdk.AvdExists(avdName) || Directory.Exists(sdk.AvdDirFor(avdName));

        if (deleted.Count > 0)
            ConsoleUi.Info("已删除 " + deleted.Count + " 项：" + string.Join("、", deleted));

        if (stillInstalled || stillAvd)
        {
            ConsoleUi.Error("仍有残留未能删除（多半是被进程占用，先执行 android.exe -stop 再重试）。");
            return 1;
        }

        ConsoleUi.Ok($"{version.Label}（API {version.ApiLevel}）的镜像与数据已全部删除，该版本现在显示为「未安装」。");
        ConsoleUi.Info("复核：-list android 中该版本为「可下载」，-start 需要先 -download " + version.Key);
        return 0;
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
