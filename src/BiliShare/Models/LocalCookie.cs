using System.Text.Json.Serialization;

namespace BiliShare.Models;

/// <summary>
/// 本地保存的 B 站登录态（离线模式），含续期所需的 refresh_token。
/// 数据经 AES-256-GCM 加密后写入 <c>data/cookie.dat</c>。
/// </summary>
public sealed class LocalCookie
{
    [JsonPropertyName("sessdata")]
    public string Sessdata { get; set; } = string.Empty;

    [JsonPropertyName("bili_jct")]
    public string BiliJct { get; set; } = string.Empty;

    [JsonPropertyName("dede_user_id")]
    public string? DedeUserId { get; set; }

    /// <summary>续期 required：B 站登录接口响应中的 ac_time_value。</summary>
    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("sid")]
    public string? Sid { get; set; }

    [JsonPropertyName("saved_at")]
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>转为可注入浏览器 / 提交后端的 Cookie 串（不含 refresh_token）。</summary>
    public string ToCookieString()
    {
        var parts = new System.Collections.Generic.List<string>(4);
        if (!string.IsNullOrEmpty(Sessdata)) parts.Add($"SESSDATA={Sessdata}");
        if (!string.IsNullOrEmpty(BiliJct)) parts.Add($"bili_jct={BiliJct}");
        if (!string.IsNullOrEmpty(DedeUserId)) parts.Add($"DedeUserID={DedeUserId}");
        if (!string.IsNullOrEmpty(Sid)) parts.Add($"sid={Sid}");
        return string.Join("; ", parts);
    }

    /// <summary>是否具备完整登录态（SESSDATA 与 bili_jct 均非空）。</summary>
    public bool IsComplete => !string.IsNullOrEmpty(Sessdata) && !string.IsNullOrEmpty(BiliJct);

    /// <summary>从服务端返回的 Cookie 串（<c>name=value; ...</c>）构建 <see cref="LocalCookie"/>（用于在线模式导出）。</summary>
    public static LocalCookie FromCookieString(string cookieString)
    {
        var result = new LocalCookie();
        if (string.IsNullOrWhiteSpace(cookieString))
            return result;

        foreach (var part in cookieString.Split(';'))
        {
            var idx = part.IndexOf('=');
            if (idx <= 0)
                continue;

            var name = part[..idx].Trim();
            var value = part[(idx + 1)..].Trim();
            switch (name)
            {
                case "SESSDATA": result.Sessdata = value; break;
                case "bili_jct": result.BiliJct = value; break;
                case "DedeUserID": result.DedeUserId = value; break;
                case "sid": result.Sid = value; break;
            }
        }
        result.SavedAt = DateTimeOffset.UtcNow;
        return result;
    }
}