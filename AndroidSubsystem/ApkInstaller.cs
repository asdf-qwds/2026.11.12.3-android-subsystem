using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AndroidSubsystem;

/// <summary>把 APK 拖进界面时，用 SDK 里的 adb 安装到正在运行的模拟器。</summary>
internal static class ApkInstaller
{
    public static string? AdbExe(Settings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.SdkRoot)) return null;
        var adb = Path.Combine(settings.SdkRoot!, "platform-tools", "adb.exe");
        return File.Exists(adb) ? adb : null;
    }

    public static bool IsApk(string path) =>
        File.Exists(path) && path.EndsWith(".apk", StringComparison.OrdinalIgnoreCase);

    /// <summary>返回 (成功, 说明文本, 输出日志)。</summary>
    public static async Task<(bool ok, string message, string log)> InstallAsync(string apkPath, Settings settings)
    {
        var adb = AdbExe(settings);
        if (adb == null)
            return (false, L.T("skin.noadb"), "");

        var log = "";

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var devices = await Task.Run(() => ListDevices(adb));
            log += "[devices] " + (devices.Count == 0 ? "(空)" : string.Join(", ", devices.Select(d => d.serial + ":" + d.state))) + "\n";

            var online = devices.Where(d => d.state == "device").Select(d => d.serial).ToList();
            if (online.Count == 0)
            {
                log += await Task.Run(() => RecoverAsync(adb, devices));
                await Task.Delay(3000);
                continue;
            }

            var ok = false;
            foreach (var serial in online)
            {
                var res = await Task.Run(() => ProcessRunner.Run(
                    adb, new[] { "-s", serial, "install", "-r", apkPath }, timeoutMs: 240000));
                log += "[" + serial + "]\n" + res.Combined + "\n";

                if (res.Combined.Contains("Success", StringComparison.OrdinalIgnoreCase))
                {
                    ok = true;
                    continue;
                }

                // 离线 / 未授权 / 被别的 adb 占用：重连后再试
                if (res.Combined.Contains("offline", StringComparison.OrdinalIgnoreCase)
                    || res.Combined.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Run(() => RecoverAsync(adb, devices));
                    await Task.Delay(3000);
                    var retry = await Task.Run(() => ProcessRunner.Run(
                        adb, new[] { "-s", serial, "install", "-r", apkPath }, timeoutMs: 240000));
                    log += "[" + serial + " retry]\n" + retry.Combined + "\n";
                    if (retry.Combined.Contains("Success", StringComparison.OrdinalIgnoreCase)) ok = true;
                }
            }

            if (ok)
                return (true, L.T("skin.ok") + "：" + Path.GetFileName(apkPath), log);
        }

        var names = string.Join(", ", ListDevices(adb).Select(d => d.serial + "(" + d.state + ")"));
        var why = string.IsNullOrEmpty(names) ? L.T("skin.nodevice") : L.T("skin.offline") + "：" + names;
        return (false, L.T("skin.fail") + " — " + why, log);
    }

    private static System.Collections.Generic.List<(string serial, string state)> ListDevices(string adb)
    {
        var result = new System.Collections.Generic.List<(string, string)>();
        var res = ProcessRunner.Run(adb, new[] { "devices" });
        foreach (var raw in res.Combined.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices") || line.StartsWith("*")) continue;
            var parts = line.Split('\t', ' ');
            if (parts.Length < 2) continue;
            var serial = parts[0].Trim();
            var state = parts[1].Trim();
            if (serial.Length == 0 || state.Length == 0) continue;
            result.Add((serial, state));
        }
        return result;
    }

    /// <summary>把离线 / 卡住的 adb 连接拉回来：先 reconnect，再重启 adb server。</summary>
    private static string RecoverAsync(string adb, System.Collections.Generic.List<(string serial, string state)> devices)
    {
        var log = "";
        try
        {
            if (devices.Count > 0)
            {
                var res = ProcessRunner.Run(adb, new[] { "reconnect", "offline" }, timeoutMs: 30000);
                log += "[reconnect offline] " + res.Combined.Trim() + "\n";
            }

            ProcessRunner.Run(adb, new[] { "kill-server" }, timeoutMs: 30000);
            Thread.Sleep(1500);
            ProcessRunner.Run(adb, new[] { "start-server" }, timeoutMs: 60000);
            log += "[adb server] restarted\n";

            var serials = devices.Select(d => d.serial).ToList();
            foreach (var serial in serials)
                ProcessRunner.Run(adb, new[] { "-s", serial, "wait-for-device" }, timeoutMs: 60000);
        }
        catch (Exception ex)
        {
            log += "[recover] " + ex.Message + "\n";
        }
        return log;
    }
}
