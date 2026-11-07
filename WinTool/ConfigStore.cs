using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinTool;

public static class ConfigStore
{
    /// <summary>配置和日志放在程序目录下的 data 文件夹，不写注册表/用户目录。</summary>
    public static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "data");

    public static string ConfigPath => Path.Combine(Dir, "config.json");
    public static string LogPath => Path.Combine(Dir, "error.log");

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new AppConfig();
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<AppConfig>(json, Options) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            Log("配置读取失败，已回退默认值", ex);
            try { File.Copy(ConfigPath, ConfigPath + ".bad", overwrite: true); } catch { }
            return new AppConfig();
        }
    }

    public static void Save(AppConfig config)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(config, Options));
            File.Move(tmp, ConfigPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log("配置保存失败", ex);
        }
    }

    public static void Log(string message, Exception? ex = null)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}{ex}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不应影响主流程
        }
    }
}
