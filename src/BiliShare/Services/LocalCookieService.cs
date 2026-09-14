using System.Text;
using System.Text.Json;
using BiliShare.Models;

namespace BiliShare.Services;

/// <summary>
/// 本地 Cookie 存取：AES-256-GCM 加密写入 <c>data/cookie.dat</c>，支持导出/导入 <c>.bilicookie</c>。
/// </summary>
public static class LocalCookieService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>加密保存到 data/cookie.dat。</summary>
    public static void Save(LocalCookie cookie)
    {
        var key = CryptoService.LoadOrCreateKey();
        var plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(cookie, JsonOptions));
        var encrypted = CryptoService.Encrypt(plaintext, key);

        Paths.EnsureDirectories();
        File.WriteAllBytes(Paths.LocalCookieFile, encrypted);
    }

    /// <summary>读取并解密；不存在、损坏或解密失败返回 null。</summary>
    public static LocalCookie? Load()
    {
        if (!File.Exists(Paths.LocalCookieFile) || !File.Exists(Paths.LocalKeyFile))
            return null;

        try
        {
            var key = File.ReadAllBytes(Paths.LocalKeyFile);
            var encrypted = File.ReadAllBytes(Paths.LocalCookieFile);
            var plaintext = CryptoService.Decrypt(encrypted, key);
            return JsonSerializer.Deserialize<LocalCookie>(Encoding.UTF8.GetString(plaintext), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>删除本地 Cookie 及密钥（清除登录态）。</summary>
    public static void Delete()
    {
        if (File.Exists(Paths.LocalCookieFile)) File.Delete(Paths.LocalCookieFile);
        if (File.Exists(Paths.LocalKeyFile)) File.Delete(Paths.LocalKeyFile);
    }

    /// <summary>导出加密 Cookie 到指定 .bilicookie 文件（用本机 key.dat 加密，仅本机可导入）。</summary>
    public static void Export(string filePath, LocalCookie cookie)
    {
        var key = CryptoService.LoadOrCreateKey();
        var plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(cookie, JsonOptions));
        File.WriteAllBytes(filePath, CryptoService.Encrypt(plaintext, key));
    }

    /// <summary>导入 .bilicookie 文件；本机无密钥或解密失败返回 null。</summary>
    public static LocalCookie? Import(string filePath)
    {
        if (!File.Exists(filePath) || !File.Exists(Paths.LocalKeyFile))
            return null;

        try
        {
            var key = File.ReadAllBytes(Paths.LocalKeyFile);
            var encrypted = File.ReadAllBytes(filePath);
            var plaintext = CryptoService.Decrypt(encrypted, key);
            return JsonSerializer.Deserialize<LocalCookie>(Encoding.UTF8.GetString(plaintext), JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}