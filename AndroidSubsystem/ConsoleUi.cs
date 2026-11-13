using System;
using System.Collections.Generic;
using System.IO;

namespace AndroidSubsystem;

internal static class ConsoleUi
{
    /// <summary>构建标识，用于确认用户手里跑的是哪一版 exe。</summary>
    public const string BuildVersion = "2026-11-12.5";

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
