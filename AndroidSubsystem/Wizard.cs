using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AndroidSubsystem;

/// <summary>首次运行向导：选目录 → 装基础组件 → 选 Android 版本下载 → 可立即启动。</summary>
internal static class Wizard
{
    public static async Task<int> RunAsync(string? presetRoot)
    {
        var settings = Settings.Load();

        ConsoleUi.Step("第 1 步：选择下载目录");
        string root;

        if (presetRoot != null)
        {
            root = Path.GetFullPath(presetRoot.Trim().Trim('"'));
            ConsoleUi.Info("使用指定目录：" + root);
        }
        else if (settings.SdkRoot != null && Directory.Exists(settings.SdkRoot))
        {
            ConsoleUi.Info("上次使用的目录：" + settings.SdkRoot);
            if (ConsoleUi.Confirm("继续使用该目录？", true))
            {
                root = settings.SdkRoot;
            }
            else
            {
                root = PromptRoot();
            }
        }
        else
        {
            root = PromptRoot();
        }

        try
        {
            Directory.CreateDirectory(root);
            var probe = Path.Combine(root, ".write_test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            ConsoleUi.Error("目录不可写：" + ex.Message);
            return 1;
        }

        var drive = Path.GetPathRoot(root) ?? "C:\\";
        try
        {
            var free = new DriveInfo(drive).AvailableFreeSpace / (1024.0 * 1024 * 1024);
            ConsoleUi.Info($"目标磁盘剩余空间：{free:0.0} GB（全部镜像可能需要数百 GB）");
        }
        catch { }

        var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmedRoot.Length <= 2 && trimmedRoot.EndsWith(':'))
        {
            ConsoleUi.Warn("注意：选择的是盘符根目录。SDK 会把 platform-tools、emulator、system-images 等直接铺在盘符下面，");
            ConsoleUi.Warn("并且 sdkmanager 在盘符根目录下会误判为“已安装”而实际不写入。建议使用子目录，例如 " + trimmedRoot + "\\AndroidSDK。");
            ConsoleUi.Info("（如果你已安装 Android Studio，也可以直接填它的 SDK 目录，例如 D:\\SDK，本工具会直接复用。）");
            if (!ConsoleUi.Confirm("仍要继续使用该盘符根目录？", false))
            {
                ConsoleUi.Info("已取消，请重新运行并填入子目录。");
                return 1;
            }
        }

        var looksLikeSdk = File.Exists(Path.Combine(root, "platform-tools", "adb.exe"))
                        || Directory.Exists(Path.Combine(root, "platforms"))
                        || Directory.Exists(Path.Combine(root, "system-images"));
        if (looksLikeSdk)
            ConsoleUi.Ok("检测到已有 Android SDK，将直接复用其中的 platform-tools / emulator / system-images。");

        settings.SdkRoot = root;
        settings.Save();

        // ---------------------------------------------------------------- 第 2 步
        var sdk = new AndroidSdk(root);
        ConsoleUi.Step("第 2 步：安装 Android Studio 虚拟机运行所需组件");
        ConsoleUi.Info("包含 JDK、cmdline-tools、platform-tools(ADB)、emulator，首次需下载约 500 MB，请耐心等待。");
        await sdk.EnsureBaseAsync();

        // ---------------------------------------------------------------- 第 3 步
        ConsoleUi.Step("第 3 步：选择要下载的 Android 版本");
        ConsoleUi.Info("正在读取镜像仓库（首次读取约需 1 分钟）…");
        var repo = await Task.Run(() => sdk.ListImages());

        var status = new Dictionary<string, string>();
        foreach (var v in Catalog.Versions)
        {
            var img = sdk.ResolveImage(repo, v);
            status[v.Key] = img == null
                ? (repo.IsLoaded ? "不支持" : "未知")
                : (sdk.IsImageInstalled(img) ? "已安装" : "可下载");
        }

        Console.WriteLine();
        Console.WriteLine($"{"版本键",-14}{"Android 版本",-28}{"API",-6}状态");
        Console.WriteLine(new string('-', 70));
        foreach (var v in Catalog.Versions)
            Console.WriteLine($"{v.Key,-14}{v.Label,-28}{v.ApiLevel,-6}{status[v.Key]}");
        Console.WriteLine();

        ConsoleUi.Info("输入要下载的版本（可多个，用逗号分隔），或输入 all 下载全部，直接回车跳过。");

        while (true)
        {
            var input = ConsoleUi.Ask("要下载的版本", "");
            if (string.IsNullOrWhiteSpace(input)) break;

            IEnumerable<AndroidVersion> targets;
            if (input.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                targets = Catalog.Versions.Where(v => status[v.Key] == "可下载").ToList();
                ConsoleUi.Warn($"将下载 {targets.Count()} 个镜像，占用空间可能达数百 GB。");
                if (!ConsoleUi.Confirm("确认继续？", false)) break;
            }
            else
            {
                var list = new List<AndroidVersion>();
                var bad = new List<string>();
                foreach (var token in input.Split(new[] { ',', '，', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var v = Catalog.Resolve(token);
                    if (v == null) bad.Add(token);
                    else if (!list.Any(x => x.Key == v.Key)) list.Add(v);
                }
                if (bad.Count > 0)
                {
                    ConsoleUi.Error("无法识别：" + string.Join("、", bad));
                    continue;
                }
                targets = list;
            }

            foreach (var v in targets)
            {
                var img = sdk.ResolveImage(repo, v);
                if (img == null)
                {
                    ConsoleUi.Warn($"{v.Label}（API {v.ApiLevel}）官方仓库无可用镜像，已跳过。");
                    continue;
                }
                if (sdk.IsImageInstalled(img))
                {
                    ConsoleUi.Ok($"{v.Label} 已安装，跳过。");
                    continue;
                }

                try
                {
                    await sdk.InstallImageAsync(img);
                }
                catch (Exception ex)
                {
                    ConsoleUi.Error($"{v.Label} 下载失败：{ex.Message}");
                }
            }
            break;
        }

        // ---------------------------------------------------------------- 第 4 步
        ConsoleUi.Step("第 4 步：启动虚拟机（可选）");
        var installed = Catalog.Versions
            .Where(v =>
            {
                var img = sdk.ResolveImage(repo, v);
                return img != null && sdk.IsImageInstalled(img);
            })
            .ToList();

        if (installed.Count == 0)
        {
            ConsoleUi.Info("当前没有已安装的系统镜像。");
            ConsoleUi.Info("之后可用：android.exe -download android17 下载，android.exe -start android17 启动。");
            return 0;
        }

        ConsoleUi.Info("已安装的版本：" + string.Join("、", installed.Select(v => v.Key)));
        var startInput = ConsoleUi.Ask("要启动的版本（回车跳过）", installed.Last().Key);
        if (string.IsNullOrWhiteSpace(startInput)) return 0;

        var targetVersion = Catalog.Resolve(startInput);
        if (targetVersion == null)
        {
            ConsoleUi.Error("无法识别的版本：" + startInput);
            return 1;
        }

        var targetImage = sdk.ResolveImage(repo, targetVersion);
        if (targetImage == null || !sdk.IsImageInstalled(targetImage))
        {
            ConsoleUi.Error("该版本镜像未安装，请先下载。");
            return 1;
        }

        await sdk.EnsureAvdAsync(targetVersion, targetImage);
        var avdName = AndroidSdk.AvdNameFor(targetVersion);
        if (AndroidSdk.IsRunning(avdName))
        {
            AndroidSdk.BringToFront(avdName);
            ConsoleUi.Ok($"{targetVersion.Label} 虚拟机已在运行，已把窗口切到前台（AVD: {avdName}）。");
            return 0;
        }
        var proc = sdk.LaunchEmulator(avdName);
        ConsoleUi.Ok($"{targetVersion.Label} 虚拟机已启动（AVD: {avdName}，机型: Pixel 9 Pro）");
        await AndroidSdk.EnsureWindowVisibleAsync(avdName, 60);
        await sdk.WaitForBootAsync(proc, 240);

        return 0;
    }

    private static string PromptRoot()
    {
        var def = @"C:\AndroidSubsystem";
        ConsoleUi.Info("用于存放 Android SDK、系统镜像与 AVD，建议选择空间充足的磁盘（全部镜像可能数百 GB）。");
        var input = ConsoleUi.Ask("下载目录", def) ?? def;
        var full = Path.GetFullPath(input.Trim().Trim('"'));
        return full;
    }
}
