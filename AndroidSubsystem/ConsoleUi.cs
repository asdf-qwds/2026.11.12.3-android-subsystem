using System;
using System.Collections.Generic;
using System.IO;

namespace AndroidSubsystem;

internal static class ConsoleUi
{
    /// <summary>构建标识，用于确认用户手里跑的是哪一版 exe（正式版用 1.0.0.0，预览版用日期式编号）。</summary>
    public const string BuildVersion = "正式版1.0.0.0";

    /// <summary>纯 ASCII 版本号，仅用于 HTTP User-Agent 等不允许中文的场合。</summary>
    public const string BuildId = "1.0.0.0";

    public static void Banner()
    {
        Console.WriteLine("==================================================================");
        Console.WriteLine("  Android Subsystem for Windows (C#)");
        Console.WriteLine("  基于 Android Studio SDK / Emulator 的安卓虚拟机管理器");
        Console.WriteLine("  build " + BuildVersion);
        Console.WriteLine("==================================================================");
        Console.WriteLine();
    }

    public static void Step(string msg)
    {
        Console.WriteLine();
        Console.WriteLine(">> " + msg);
    }

    public static void Info(string msg) => Console.WriteLine("   " + msg);
    public static void Ok(string msg) => Console.WriteLine("[OK] " + msg);
    public static void Warn(string msg) => Console.WriteLine("[! ] " + msg);
    public static void Error(string msg) => Console.WriteLine("[ERR] " + msg);

    public static string? Ask(string prompt, string? def = null)
    {
        while (true)
        {
            Console.Write(def == null ? $"{prompt}: " : $"{prompt} [{def}]: ");
            var line = Console.ReadLine();
            if (line == null) return def;
            line = line.Trim().Trim('"');
            if (line.Length == 0)
            {
                if (def != null) return def;
                continue;
            }
            return line;
        }
    }

    public static bool Confirm(string prompt, bool def = false)
    {
        Console.Write($"{prompt} [{(def ? "Y/n" : "y/N")}]: ");
        var line = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(line)) return def;
        return line is "y" or "yes" or "是" or "1";
    }

    /// <summary>在 1..count 里选一项，直接回车取默认项。</summary>
    public static int Choose(string prompt, int count, int def = 1)
    {
        while (true)
        {
            Console.Write($"{prompt} [1-{count}]（默认 {def}）: ");
            var line = Console.ReadLine();
            if (line == null) return def;
            line = line.Trim();
            if (line.Length == 0) return def;
            if (int.TryParse(line, out var n) && n >= 1 && n <= count) return n;
            Console.WriteLine($"   请输入 1 到 {count} 之间的数字。");
        }
    }

    /// <summary>强制更新：版本名里带「强制」时只能输入 Y，没有 N。</summary>
    public static void ConfirmForced(string prompt)
    {
        var first = true;
        while (true)
        {
            Console.Write($"{prompt} [Y]（强制更新，没有 N）: ");
            var line = Console.ReadLine();
            if (line == null)
            {
                Console.WriteLine();
                Console.WriteLine("   该版本为强制更新且无输入，按更新继续。");
                return;
            }
            line = line.Trim().ToLowerInvariant();
            if (line is "y" or "yes" or "是" or "1") return;
            if (first || line.Length > 0)
                Console.WriteLine("   该版本为强制更新，只能输入 Y。");
            first = false;
        }
    }

    /// <summary>单行进度条，覆盖刷新当前行。</summary>
    public static void Progress(string label, long done, long total, double mbPerSec)
    {
        double pct = total > 0 ? (double)done * 100 / total : 0;
        long doneMb = done / (1024 * 1024);
        long totalMb = total / (1024 * 1024);
        string text = total > 0
            ? $"   {label}  {pct,6:0.0}%  {doneMb,5}/{totalMb,-5} MB  {mbPerSec,5:0.0} MB/s"
            : $"   {label}  {doneMb,5} MB  {mbPerSec,5:0.0} MB/s";
        Console.Write("\r" + text.PadRight(78));
    }

    public static void EndProgress()
    {
        Console.Write("\r" + new string(' ', 78) + "\r");
    }
}
