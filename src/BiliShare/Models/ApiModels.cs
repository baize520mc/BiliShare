using System.Text.Json.Serialization;

namespace BiliShare.Models;

/// <summary>统一响应体：<c>{code, message, data}</c>。</summary>
public sealed class ApiResponse<T>
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public T? Data { get; set; }
}

/// <summary><c>GET /status</c> 的 data。</summary>
public sealed class StatusData
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("valid")]
    public bool Valid { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("last_refresh")]
    public string? LastRefresh { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("user_enabled")]
    public bool UserEnabled { get; set; }

    [JsonPropertyName("client_enabled")]
    public bool ClientEnabled { get; set; }

    [JsonPropertyName("auto_refresh_enabled")]
    public bool AutoRefreshEnabled { get; set; }

    [JsonPropertyName("plugin_version")]
    public string PluginVersion { get; set; } = string.Empty;

    [JsonPropertyName("bili_username")]
    public string? BiliUsername { get; set; }

    [JsonPropertyName("bili_uid")]
    public string? BiliUid { get; set; }

    [JsonPropertyName("validated")]
    public bool Validated { get; set; }
}

/// <summary><c>GET /cookie</c> 的 data。</summary>
public sealed class CookieData
{
    [JsonPropertyName("cookie")]
    public string Cookie { get; set; } = string.Empty;
}

/// <summary><c>POST /cookie/refresh</c> 的 data。</summary>
public sealed class RefreshData
{
    [JsonPropertyName("new_cookie")]
    public string NewCookie { get; set; } = string.Empty;
}

/// <summary><c>POST /cookie</c> 的请求体（字段名与 B 站 Cookie 原始命名一致）。</summary>
public sealed class CookieSubmitRequest
{
    [JsonPropertyName("SESSDATA")]
    public string Sessdata { get; set; } = string.Empty;

    [JsonPropertyName("bili_jct")]
    public string BiliJct { get; set; } = string.Empty;

    [JsonPropertyName("DedeUserID")]
    public string? DedeUserId { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("sid")]
    public string? Sid { get; set; }
}

/// <summary><c>POST /cookie/validate</c> 的 data。</summary>
public sealed class ValidateData
{
    [JsonPropertyName("valid")]
    public bool Valid { get; set; }

    [JsonPropertyName("bili_username")]
    public string? BiliUsername { get; set; }

    [JsonPropertyName("bili_uid")]
    public string? BiliUid { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

/// <summary><c>PUT /cookie/preferences</c> 的请求体（null = 不修改）。</summary>
public sealed class PreferencesRequest
{
    [JsonPropertyName("userEnabled")]
    public bool? UserEnabled { get; set; }

    [JsonPropertyName("clientEnabled")]
    public bool? ClientEnabled { get; set; }

    [JsonPropertyName("autoRefreshEnabled")]
    public bool? AutoRefreshEnabled { get; set; }
}

/// <summary>Halo 当前用户信息（来自 Halo 核心 API <c>/apis/api.console.halo.run/v1alpha1/users/-</c>）。</summary>
public sealed class HaloUserData
{
    /// <summary>登录用户名（metadata.name）。</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>显示名（spec.displayName），可能为空。</summary>
    public string DisplayName { get; set; } = string.Empty;
}
