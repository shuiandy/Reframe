using System.Text.Json;
using Reframe.Core;

namespace Reframe.Services;

/// <summary>配置读写:%LOCALAPPDATA%\Reframe\config.json</summary>
public static class ConfigStore
{
    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reframe");

    public static string Path_ => Path.Combine(Dir, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(Path_))
            {
                string json = File.ReadAllText(Path_);
                var cfg = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig);
                if (cfg != null) return cfg;
            }
        }
        catch { /* 损坏就回落到默认 */ }

        var def = AppConfig.CreateDefault();
        Save(def);
        return def;
    }

    public static void Save(AppConfig cfg)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            string json = JsonSerializer.Serialize(cfg, ConfigJsonContext.Default.AppConfig);
            File.WriteAllText(Path_, json);
        }
        catch { /* 暂时吞掉,UI 层可提示 */ }
    }
}
