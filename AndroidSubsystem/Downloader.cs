using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace AndroidSubsystem;

internal static class Downloader
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(30),
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AndroidSubsystem/1.0 (Windows)");
        return client;
    }

    /// <summary>URL 是否可访问。</summary>
    public static async Task<bool> ExistsAsync(string url)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public static async Task DownloadAsync(string url, string destPath, string label)
    {
        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var part = destPath + ".part";
        if (File.Exists(part)) File.Delete(part);

        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        long total = resp.Content.Headers.ContentLength ?? -1;
        var sw = Stopwatch.StartNew();
        long done = 0;
        long lastReport = 0;

        await using (var src = await resp.Content.ReadAsStreamAsync())
        await using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await src.ReadAsync(buffer)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                if (sw.ElapsedMilliseconds - lastReport > 200)
                {
                    lastReport = sw.ElapsedMilliseconds;
                    double mbps = done / 1048576.0 / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                    ConsoleUi.Progress(label, done, total, mbps);
                }
            }
        }

        ConsoleUi.EndProgress();
        if (File.Exists(destPath)) File.Delete(destPath);
        File.Move(part, destPath);
        ConsoleUi.Ok($"{label} 下载完成（{done / 1048576.0:0.0} MB）");
    }
}
