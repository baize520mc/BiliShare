using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BiliShare.Models;

namespace BiliShare.Services;

/// <summary>
/// 服务端 API 客户端：对接 bili-cookie 插件（普通用户接口）。
/// 统一响应 <c>{code, message, data}</c>，成功 = HTTP 2xx 且 code==0，失败抛 <see cref="ApiException"/>。
/// </summary>
public sealed class ApiClient : IDisposable
{
    private const string ApiPath = "apis/api.bili-cookie.halo.run/v1alpha1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly string _root;
    private readonly string _endpoint;

    public ApiClient(string serverBaseUrl, string pat)
    {
        var root = serverBaseUrl?.TrimEnd('/') ?? string.Empty;
        _root = root;
        _endpoint = $"{root}/{ApiPath}";

        _http = new HttpClient
        {
            // 刷新接口内部走 B 站协议（含约 3s correspond 延迟），超时需 ≥ 20s
            Timeout = TimeSpan.FromSeconds(30),
        };
        if (!string.IsNullOrEmpty(pat))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pat);
    }

    /// <summary>GET /status：查询插件状态与本人 Cookie 状态。</summary>
    public Task<StatusData> GetStatusAsync(CancellationToken ct = default)
        => SendAsync<StatusData>(HttpMethod.Get, "status", null, ct);

    /// <summary>GET /cookie：获取本人 Cookie 串。</summary>
    public Task<CookieData> GetCookieAsync(CancellationToken ct = default)
        => SendAsync<CookieData>(HttpMethod.Get, "cookie", null, ct);

    /// <summary>POST /cookie：提交/覆盖本人 Cookie。</summary>
    public async Task UploadCookieAsync(CookieSubmitRequest request, CancellationToken ct = default)
    {
        await SendAsync<object?>(HttpMethod.Post, "cookie", request, ct);
    }

    /// <summary>POST /cookie/refresh：手动刷新本人 Cookie。</summary>
    public Task<RefreshData> RefreshCookieAsync(CancellationToken ct = default)
        => SendAsync<RefreshData>(HttpMethod.Post, "cookie/refresh", null, ct);

    /// <summary>GET /cookie/client：客户端专用读取本人 Cookie 串（受「接受客户端连接」开关约束）。</summary>
    public Task<CookieData> GetClientCookieAsync(CancellationToken ct = default)
        => SendAsync<CookieData>(HttpMethod.Get, "cookie/client", null, ct);

    /// <summary>POST /cookie/connect：客户端连接握手（仅登记审计日志）。</summary>
    public Task ConnectAsync(CancellationToken ct = default)
        => SendAsync<object?>(HttpMethod.Post, "cookie/connect", null, ct);

    /// <summary>POST /cookie/validate：验证 Cookie 有效性（成功时抓取用户名/UID 写入服务端）。</summary>
    public Task<ValidateData> ValidateCookieAsync(CancellationToken ct = default)
        => SendAsync<ValidateData>(HttpMethod.Post, "cookie/validate", null, ct);

    /// <summary>PUT /cookie/preferences：更新用户级三开关（null = 不修改）。返回更新后的完整状态。</summary>
    public Task<StatusData> UpdatePreferencesAsync(PreferencesRequest request, CancellationToken ct = default)
        => SendAsync<StatusData>(HttpMethod.Put, "cookie/preferences", request, ct);

    /// <summary>POST /cookie/clear：清除本人全部数据并禁用。</summary>
    public Task ClearCookieAsync(CancellationToken ct = default)
        => SendAsync<object?>(HttpMethod.Post, "cookie/clear", null, ct);

    /// <summary>GET Halo 核心 API：当前登录用户详情（base 为 api.console.halo.run，非插件 API）。</summary>
    public async Task<HaloUserData> GetCurrentUserAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{_root}/apis/api.console.halo.run/v1alpha1/users/-");
        using var resp = await _http.SendAsync(req, ct);

        if (!resp.IsSuccessStatusCode)
            throw new ApiException((int)resp.StatusCode, -1, "无法获取 Halo 用户信息");

        var json = await resp.Content.ReadAsStringAsync(ct);
        return ParseHaloUser(json);
    }

    public void Dispose() => _http.Dispose();

    /// <summary>解析 Halo 用户详情，兼容 <c>{user:{...}}</c> 与直接 <c>{metadata:{...}}</c> 两种结构。</summary>
    private static HaloUserData ParseHaloUser(string json)
    {
        var result = new HaloUserData();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return result;

            // DetailedUser 结构：用户对象包在 user 字段里
            var user = root;
            if (root.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object)
                user = u;

            if (user.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
                && metadata.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                result.Username = name.GetString() ?? "";

            if (user.TryGetProperty("spec", out var spec) && spec.ValueKind == JsonValueKind.Object
                && spec.TryGetProperty("displayName", out var displayName) && displayName.ValueKind == JsonValueKind.String)
                result.DisplayName = displayName.GetString() ?? "";
        }
        catch
        {
            // 解析失败返回空，调用方按占位显示
        }
        return result;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, $"{_endpoint}/{path}");
        if (body != null)
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);

        // 部分接口（如管理员删除）返回 204，无响应体
        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent)
            return default!;

        var json = await resp.Content.ReadAsStringAsync(ct);

        ApiResponse<T>? api;
        try
        {
            api = JsonSerializer.Deserialize<ApiResponse<T>>(json, JsonOptions);
        }
        catch
        {
            throw new ApiException((int)resp.StatusCode, -1, "服务器响应无法解析");
        }

        if (api == null)
            throw new ApiException((int)resp.StatusCode, -1, "服务器响应为空");

        if (resp.IsSuccessStatusCode && api.Code == 0)
            return api.Data!;

        throw new ApiException((int)resp.StatusCode, api.Code, api.Message);
    }
}