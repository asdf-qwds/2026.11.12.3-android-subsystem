using System;
using System.Linq;
using System.Text;

namespace AndroidSubsystem;

/// <summary>发布通道：正式版高于预览版（预览版升级到正式版算更新，反过来不算）。</summary>
internal enum ReleaseChannel
{
    Preview = 1,
    Formal = 2,
}

/// <summary>
/// 解析后的版本标识。支持三种写法：
///   正式版1.0.0.0 / v1.0.0.0
///   预览版2026-11-14-8 / v2026-11-14-8 / 2026.11.12.7
/// 任意写法前面带「强制」两个字即为强制更新（只给 Y，没有 N）。
/// </summary>
internal sealed class BuildId
{
    public const string FormalLabel = "正式版";
    public const string PreviewLabel = "预览版";

    public ReleaseChannel Channel = ReleaseChannel.Preview;
    public int[] Segments = new[] { 0 };
    public bool Forced;
    public string Label = PreviewLabel;

    /// <summary>显示用的版本名，如 正式版1.0.0.0、预览版2026-11-14-8。</summary>
    public string Display => Label + Number;

    /// <summary>正式版用点号（1.0.0.0），预览版用短横（2026-11-14-8）。</summary>
    public string Number =>
        string.Join(Channel == ReleaseChannel.Formal ? "." : "-", Segments.Select(s => s.ToString()));
}

internal static class Versioning
{
    /// <summary>从 Release 标题、tag 或 BuildVersion 里解析出版本。</summary>
    public static BuildId Parse(string? raw)
    {
        var id = new BuildId();
        var text = (raw ?? "").Trim();
        if (text.Length == 0) return id;

        if (text.Contains("强制"))
        {
            id.Forced = true;
            text = text.Replace("强制", "");
        }

        ReleaseChannel? channel = null;
        var formalAt = text.IndexOf(BuildId.FormalLabel, StringComparison.Ordinal);
        var previewAt = text.IndexOf(BuildId.PreviewLabel, StringComparison.Ordinal);
        if (formalAt >= 0)
        {
            channel = ReleaseChannel.Formal;
            text = text.Substring(formalAt + BuildId.FormalLabel.Length);
        }
        else if (previewAt >= 0)
        {
            channel = ReleaseChannel.Preview;
            text = text.Substring(previewAt + BuildId.PreviewLabel.Length);
        }

        // 标题后面的中文说明、空格、括号一律截掉，只留版本号本体
        var stop = text.IndexOfAny(new[] { ' ', '\u3000', '\t', '（', '(', '，', ',', '、', '：', ':', '【', '[', '|' });
        if (stop >= 0) text = text.Substring(0, stop);
        text = text.Trim().Trim('"', '\'', '“', '”', '「', '」');
        text = text.TrimStart('v', 'V');

        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsDigit(ch)) sb.Append(ch);
            else if (ch is '.' or '-' or '_') sb.Append('.');
        }

        var segments = sb.ToString()
            .Trim('.')
            .Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(p, out var n) ? n : 0)
            .ToArray();
        if (segments.Length == 0) segments = new[] { 0 };

        // 没写「正式版/预览版」字样时，用首位数字推断：2000 以上是日期式预览版
        id.Channel = channel ?? (segments[0] >= 2000 ? ReleaseChannel.Preview : ReleaseChannel.Formal);
        id.Label = id.Channel == ReleaseChannel.Formal ? BuildId.FormalLabel : BuildId.PreviewLabel;
        id.Segments = segments;
        return id;
    }

    /// <summary>先比通道（正式版 &gt; 预览版），再逐段比数字。</summary>
    public static int Compare(BuildId a, BuildId b)
    {
        if (a.Channel != b.Channel) return a.Channel < b.Channel ? -1 : 1;
        var len = Math.Max(a.Segments.Length, b.Segments.Length);
        for (var i = 0; i < len; i++)
        {
            var x = i < a.Segments.Length ? a.Segments[i] : 0;
            var y = i < b.Segments.Length ? b.Segments[i] : 0;
            if (x != y) return x < y ? -1 : 1;
        }
        return 0;
    }

    public static int CompareRaw(string? a, string? b) => Compare(Parse(a), Parse(b));
}
