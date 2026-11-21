using System;
using System.IO;
using System.Text.Json;

namespace AndroidSubsystem;

internal sealed class Settings
{
    public string? SdkRoot { get; set; }

    /// <summary>启动时检查新版本（默认开）。</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>界面语言：zh / ko / ja / en。</summary>
    public string Language { get; set; } = "zh";

    /// <summary>不带参数启动时直接打开图形界面。</summary>
    public bool GuiMode { get; set; }

    /// <summary>图形界面造型：1 雷电风格，2 默认风格。</summary>
    public string Skin { get; set; } = "1";

    /// <summary>更新方式：stable 只更新正式版（忽略以年份开头的预览版），preview 只更新预览版（以年份开头）。</summary>
    public string UpdateChannel { get; set; } = "stable";

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AndroidSubsystem");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
                if (s != null) return s;
            }
        }
        catch { /* 配置损坏则视为未配置 */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            ConsoleUi.Warn("写入配置失败：" + ex.Message);
        }
    }
}
