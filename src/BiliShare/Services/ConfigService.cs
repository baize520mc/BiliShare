using System.Text.Json;
using BiliShare.Models;

namespace BiliShare.Services;

/// <summary>
/// 应用配置的读取与保存，持久化到 <c>data/config.json</c>。
/// </summary>
public static class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// 读取配置；文件不存在或损坏时回退为默认配置。
    /// </summary>
    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(Paths.ConfigFile))
            {
                var json = File.ReadAllText(Paths.ConfigFile);
                return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            }
        }
        catch
        {
            // 损坏的配置文件不阻塞启动，回退默认值
        }
        return new AppConfig();
    }

    /// <summary>
    /// 保存配置（先写临时文件再替换，尽量保证原子性）。
    /// </summary>
    public static void Save(AppConfig config)
    {
        Paths.EnsureDirectories();
        var json = JsonSerializer.Serialize(config, JsonOptions);
        var tmp = Paths.ConfigFile + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, Paths.ConfigFile, overwrite: true);
    }
}