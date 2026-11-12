using System;
using System.IO;
using System.Text.Json;

namespace AndroidSubsystem;

internal sealed class Settings
{
    public string? SdkRoot { get; set; }

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
