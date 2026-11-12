using System;
using System.Collections.Generic;
using System.Linq;

namespace AndroidSubsystem;

internal sealed record SystemImage(string Package, int ApiLevel, string Tag, string Abi);

/// <summary>解析 `sdkmanager --list` 得到的系统镜像清单，并按主机架构挑选最合适的一个。</summary>
internal sealed class ImageRepo
{
    private static readonly string[] TagPriority =
    {
        "google_apis_playstore",
        "google_apis",
        "google_apis_ps16k",
        "default",
        "google_atd",
        "aosp_atd",
    };

    private readonly List<SystemImage> _images;

    public bool IsLoaded { get; }
    public IReadOnlyList<SystemImage> All => _images;

    private ImageRepo(List<SystemImage> images, bool loaded)
    {
        _images = images;
        IsLoaded = loaded;
    }

    public static ImageRepo Empty { get; } = new(new List<SystemImage>(), false);

    public static ImageRepo FromList(IEnumerable<SystemImage> images)
    {
        var list = images.GroupBy(i => i.Package).Select(g => g.First()).ToList();
        return new ImageRepo(list, true);
    }

    private static int TagRank(string tag)
    {
        var idx = Array.IndexOf(TagPriority, tag);
        return idx < 0 ? TagPriority.Length : idx;
    }

    private static string[] AbiPriority()
    {
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        return arch == System.Runtime.InteropServices.Architecture.Arm64
            ? new[] { "arm64-v8a", "x86_64", "armeabi-v7a", "x86", "armeabi" }
            : new[] { "x86_64", "x86", "arm64-v8a", "armeabi-v7a", "armeabi" };
    }

    private static int AbiRank(string abi)
    {
        var idx = Array.IndexOf(AbiPriority(), abi);
        return idx < 0 ? 99 : idx;
    }

    /// <summary>为指定 API 级别挑一个镜像：优先 Play 商店版，其次主架构。</summary>
    public SystemImage? Pick(int apiLevel)
    {
        return _images
            .Where(i => i.ApiLevel == apiLevel)
            .OrderBy(i => TagRank(i.Tag))
            .ThenBy(i => AbiRank(i.Abi))
            .FirstOrDefault();
    }
}
