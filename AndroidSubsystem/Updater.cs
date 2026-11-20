using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace AndroidSubsystem;

/// <summary>
/// 通过 GitHub Releases 检查并安装新版本（android.exe -update）。
/// 版本号取最近一个 Release 的 tag（形如 v2026.11.12.3），与当前 exe 的构建版本比较；
/// 远端更新时询问 Y/N，Y 则下载 Release 里的 android.exe 并自替换。
/// </summary>
internal static class Updater
{
    // 发布仓库（可用环境变量 ANDSYS_UPDATE_REPO=owner/repo 覆盖，便于仓库改名后继续可用）
    private const string DefaultOwner = "asdf-qwds";
    private const string DefaultRepo = "2026.11.12.3-android-subsystem";

    private static readonly HttpClient Http = CreateClient();

    private sealed class ReleaseInfo
    {
        public string Tag = "";
        public string Name = "";
        public string PageUrl = "";
        public string? AssetUrl;
        public string? AssetName;
        public long AssetSize;
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(30),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AndroidSubsystem/" + ConsoleUi.BuildVersion);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static (string owner, string repo) Target()
    {
        var raw = Environment.GetEnvironmentVariable("ANDSYS_UPDATE_REPO");
        if (!string.IsNullOrWhiteSpace(raw) && raw.Contains('/'))
        {
            var parts = raw.Split('/');
            return (parts[0].Trim(), parts[1].Trim());
        }
        return (DefaultOwner, DefaultRepo);
    }

    // ---------------------------------------------------------------- 入口

    public static async Task<int> RunAsync()
    {
        var (owner, repo) = Target();
        var current = ConsoleUi.BuildVersion;

        ConsoleUi.Step("检查更新（GitHub Releases）");
        ConsoleUi.Info($"当前版本：{current}");
        ConsoleUi.Info($"版本来源：https://github.com/{owner}/{repo}/releases");

        ReleaseInfo latest;
        try
        {
            latest = await FetchLatestAsync(owner, repo);
        }
        catch (Exception ex)
        {
            ConsoleUi.Error("查询更新失败：" + ex.Message);
            ConsoleUi.Info("请检查网络（或代理）后重试；也可直接访问 Releases 页面手动下载。");
            return 1;
        }

        if (string.IsNullOrEmpty(latest.Tag))
        {
            ConsoleUi.Warn("远端还没有发布任何 Release，无需更新。");
            return 0;
        }

        ConsoleUi.Info($"远端最新：{latest.Tag}"
            + (string.IsNullOrEmpty(latest.Name) ? "" : $"（{latest.Name}）"));

        var cmp = CompareVersions(latest.Tag, current);
        if (cmp <= 0)
        {
            ConsoleUi.Ok($"当前已是最新版本（{current}），无需更新。");
            return 0;
        }

        ConsoleUi.Warn($"发现新版本 {latest.Tag}，当前为 {current}。");
        if (latest.AssetUrl == null)
        {
            ConsoleUi.Error("该 Release 没有附带 android.exe 资产，请到页面手动下载：");
            ConsoleUi.Info(latest.PageUrl);
            return 1;
        }

        if (!ConsoleUi.Confirm($"是否现在更新到 {latest.Tag}？", true))
        {
            ConsoleUi.Info("已取消，保持当前版本。");
            return 0;
        }

        return await DownloadAndReplaceAsync(latest);
    }

    // ---------------------------------------------------------------- GitHub

    private static async Task<ReleaseInfo> FetchLatestAsync(string owner, string repo)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
        var json = await Http.GetStringAsync(url);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var info = new ReleaseInfo
        {
            Tag = root.TryGetProperty("tag_name", out var tag) ? (tag.GetString() ?? "") : "",
            Name = root.TryGetProperty("name", out var name) ? (name.GetString() ?? "") : "",
            PageUrl = root.TryGetProperty("html_url", out var page) ? (page.GetString() ?? "") : "",
        };

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var an)) continue;
                var fileName = an.GetString() ?? "";
                if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                // 优先选名字就叫 android.exe 的资产
                if (info.AssetUrl != null && !fileName.Equals("android.exe", StringComparison.OrdinalIgnoreCase))
                    continue;

                info.AssetName = fileName;
                info.AssetUrl = asset.TryGetProperty("browser_download_url", out var dl) ? dl.GetString() : null;
                info.AssetSize = asset.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var n) ? n : 0;
                if (fileName.Equals("android.exe", StringComparison.OrdinalIgnoreCase)) break;
            }
        }

        return info;
    }

    // ---------------------------------------------------------------- 版本比较

    /// <summary>"2026-11-12.3" 与 "v2026.11.12.3" 视为同一版本。</summary>
    private static int[] Split(string text)
    {
        var t = (text ?? "").Trim().TrimStart('v', 'V').Replace('-', '.');
        var parts = t.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
        var nums = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var digits = new string(parts[i].TakeWhile(char.IsDigit).ToArray());
            nums[i] = int.TryParse(digits, out var n) ? n : 0;
        }
        return nums;
    }

    private static int CompareVersions(string a, string b)
    {
        var x = Split(a);
        var y = Split(b);
        var len = Math.Max(x.Length, y.Length);
        for (var i = 0; i < len; i++)
        {
            var xa = i < x.Length ? x[i] : 0;
            var ya = i < y.Length ? y[i] : 0;
            if (xa != ya) return xa < ya ? -1 : 1;
        }
        return 0;
    }

    // ---------------------------------------------------------------- 下载并自替换

    private static async Task<int> DownloadAndReplaceAsync(ReleaseInfo latest)
    {
        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(currentExe) || !File.Exists(currentExe))
        {
            ConsoleUi.Error("无法定位当前 exe 路径，请手动下载替换：");
            ConsoleUi.Info(latest.PageUrl);
            return 1;
        }

        var dir = Path.GetDirectoryName(currentExe)!;
        var newPath = Path.Combine(dir, "android.new.exe");
        var oldPath = Path.Combine(dir, "android.old.exe");

        try
        {
            await DownloadFileAsync(latest.AssetUrl!, newPath, latest.AssetName ?? "android.exe");
        }
        catch (Exception ex)
        {
            ConsoleUi.Error("下载新版本失败：" + ex.Message);
            ConsoleUi.Info("可手动下载后覆盖：" + latest.PageUrl);
            return 1;
        }

        if (latest.AssetSize > 0)
        {
            var got = new FileInfo(newPath).Length;
            if (got != latest.AssetSize)
            {
                ConsoleUi.Error($"下载不完整（{got} / {latest.AssetSize} 字节），已放弃替换。");
                TryDelete(newPath);
                return 1;
            }
        }

        var replaced = false;
        try
        {
            TryDelete(oldPath);
            // 运行中的 exe 可以被改名，但不能被覆盖，所以先备份再替换
            File.Move(currentExe, oldPath);
            File.Move(newPath, currentExe);
            replaced = true;
        }
        catch (Exception ex)
        {
            ConsoleUi.Error("替换 exe 失败：" + ex.Message);
            if (File.Exists(oldPath) && !File.Exists(currentExe))
            {
                try { File.Move(oldPath, currentExe); } catch { }
            }
            TryDelete(newPath);
            ConsoleUi.Info("可手动下载后覆盖：" + latest.PageUrl);
            return 1;
        }

        if (replaced)
        {
            ConsoleUi.Ok($"已更新到 {latest.Tag}。");
            ConsoleUi.Info("请重新运行 android.exe 使用新版本（旧版本会以 android.old.exe 留在同目录，下次启动自动清理）。");
        }
        return replaced ? 0 : 1;
    }

    // ---------------------------------------------------------------- 清理

    /// <summary>启动时清掉上次自更新留下的 android.old.exe（失败无所谓）。</summary>
    public static void CleanupOld()
    {
        try
        {
            var currentExe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExe)) return;
            var oldPath = Path.Combine(Path.GetDirectoryName(currentExe)!, "android.old.exe");
            if (File.Exists(oldPath)) File.Delete(oldPath);
        }
        catch { }
    }

    // ---------------------------------------------------------------- 下载（带重试与断流检测）

    /// <summary>
    /// 下载 Release 资产。GitHub 会 302 到 release-assets.githubusercontent.com，这里手动跟随，
    /// 并在断流超过 20 秒时判定失败重试（这套网络环境下 .NET 直连资产域名偶发卡死）。
    /// </summary>
    private static async Task DownloadFileAsync(string url, string destPath, string label)
    {
        // 直连 release-assets.githubusercontent.com 在部分网络下会卡死，
        // 优先走加速镜像，均失败后再试直连。
        var candidates = new[]
        {
            "https://ghfast.top/" + url,
            "https://gh-proxy.com/" + url,
            url,
        };
        Exception? last = null;
        for (var i = 0; i < candidates.Length; i++)
        {
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    await DownloadOnceAsync(candidates[i], destPath, label);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    ConsoleUi.EndProgress();
                    TryDelete(destPath + ".part");
                    ConsoleUi.Warn($"下载失败（{(i < 2 ? "镜像" + (i + 1) : "直连")} 第 {attempt} 次）：{ex.Message}");
                    if (attempt < 2) await Task.Delay(1500);
                }
            }
        }
        throw last ?? new IOException("下载失败");
    }

    private static async Task DownloadOnceAsync(string sourceUrl, string destPath, string label)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 8,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AndroidSubsystem/" + ConsoleUi.BuildVersion);

        // 等响应头同样要有上限，否则网络卡死时进程会永久挂起
        using var headTimeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(45));
        var resp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, sourceUrl),
            HttpCompletionOption.ResponseHeadersRead, headTimeout.Token);
        try
        {
            resp.EnsureSuccessStatusCode();

            var part = destPath + ".part";
            TryDelete(part);

            long total = resp.Content.Headers.ContentLength ?? -1;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long done = 0;
            long lastReport = 0;

            await using (var src = await resp.Content.ReadAsStreamAsync())
            await using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    // 单次读取超过 20 秒没有数据即认为断流
                    using var stall = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
                    int read;
                    try
                    {
                        read = await src.ReadAsync(buffer, stall.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        throw new IOException("下载停滞超过 20 秒");
                    }

                    if (read <= 0) break;
                    await fs.WriteAsync(buffer.AsMemory(0, read));
                    done += read;

                    if (sw.ElapsedMilliseconds - lastReport > 200)
                    {
                        lastReport = sw.ElapsedMilliseconds;
                        var mbps = done / 1048576.0 / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                        ConsoleUi.Progress(label, done, total, mbps);
                    }
                }
            }

            ConsoleUi.EndProgress();
            if (total > 0 && done < total)
                throw new IOException($"只收到 {done} / {total} 字节");

            MoveOverwrite(part, destPath);
            ConsoleUi.Ok($"{label} 下载完成（{done / 1048576.0:0.0} MB）");
        }
        finally
        {
            resp.Dispose();
        }
    }

    private static void MoveOverwrite(string from, string to)
    {
        if (File.Exists(to)) File.Delete(to);
        File.Move(from, to);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
