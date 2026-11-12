using System;
using System.Collections.Generic;
using System.Linq;

namespace AndroidSubsystem;

internal sealed record AndroidVersion(string Key, string Label, int ApiLevel);

/// <summary>从 Android 2.2 (API 8) 到 Android 17 (API 37) 的版本目录。</summary>
internal static class Catalog
{
    public static readonly IReadOnlyList<AndroidVersion> Versions = new List<AndroidVersion>
    {
        new("android2.2",    "Android 2.2 Froyo",        8),
        new("android2.3",    "Android 2.3 Gingerbread",  10),
        new("android3.0",    "Android 3.0 Honeycomb",    11),
        new("android3.1",    "Android 3.1 Honeycomb",    12),
        new("android3.2",    "Android 3.2 Honeycomb",    13),
        new("android4.0",    "Android 4.0 ICS",          14),
        new("android4.0.3",  "Android 4.0.3 ICS",        15),
        new("android4.1",    "Android 4.1 Jelly Bean",   16),
        new("android4.2",    "Android 4.2 Jelly Bean",   17),
        new("android4.3",    "Android 4.3 Jelly Bean",   18),
        new("android4.4",    "Android 4.4 KitKat",       19),
        new("android4.4w",   "Android 4.4W Wear",        20),
        new("android5.0",    "Android 5.0 Lollipop",     21),
        new("android5.1",    "Android 5.1 Lollipop",     22),
        new("android6.0",    "Android 6.0 Marshmallow",  23),
        new("android7.0",    "Android 7.0 Nougat",       24),
        new("android7.1",    "Android 7.1 Nougat",       25),
        new("android8.0",    "Android 8.0 Oreo",         26),
        new("android8.1",    "Android 8.1 Oreo",         27),
        new("android9",      "Android 9 Pie",            28),
        new("android10",     "Android 10",               29),
        new("android11",     "Android 11",               30),
        new("android12",     "Android 12",               31),
        new("android12l",    "Android 12L",              32),
        new("android13",     "Android 13",               33),
        new("android14",     "Android 14",               34),
        new("android15",     "Android 15",               35),
        new("android16",     "Android 16",               36),
        new("android17",     "Android 17",               37),
    };

    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["android4.0.4"] = "android4.0.3",
        ["android4.4.2"] = "android4.4",
        ["android4.4.4"] = "android4.4",
        ["android5"] = "android5.0",
        ["android6"] = "android6.0",
        ["android7"] = "android7.0",
        ["android8"] = "android8.0",
        ["android4.4wear"] = "android4.4w",
    };

    /// <summary>把用户输入（17 / android17 / Android 17 / 2.2 …）解析为版本条目。</summary>
    public static AndroidVersion? Resolve(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var raw = input.Trim().Trim('"').ToLowerInvariant().Replace(" ", "").Replace("_", ".");
        if (raw.StartsWith("android")) raw = raw.Substring("android".Length);
        if (raw.StartsWith("api")) raw = raw.Substring(3);
        if (raw.Length == 0) return null;

        raw = "android" + raw;
        if (Aliases.TryGetValue(raw, out var alias)) raw = alias;

        var hit = Versions.FirstOrDefault(v => v.Key == raw);
        if (hit != null) return hit;

        // 允许 "android9.0" / "android4.0.0" 这类补零写法
        hit = Versions.FirstOrDefault(v => v.Key.TrimEnd('0').TrimEnd('.') == raw.TrimEnd('0').TrimEnd('.'));
        return hit;
    }

    public static AndroidVersion ResolveOrThrow(string? input)
    {
        var v = Resolve(input);
        if (v == null)
            throw new ArgumentException($"无法识别的 Android 版本：\"{input}\"。可用 -list 查看全部版本。");
        return v;
    }

    public static AndroidVersion? ByApi(int api) => Versions.FirstOrDefault(v => v.ApiLevel == api);
}
