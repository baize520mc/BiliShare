using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BiliShare.Models;

namespace BiliShare.Services;

/// <summary>离线 Cookie 校验结果。</summary>
public sealed class BiliValidateResult
{
    public bool Valid { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Uid { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

/// <summary>离线 B 站协议异常，<see cref="Exception.Message"/> 为可直接展示给用户的中文提示。</summary>
public sealed class BiliAuthException : Exception
{
    public BiliAuthException(string message) : base(message) { }
}

/// <summary>
/// 离线模式本地 B 站认证服务：验证 Cookie 有效性（nav 接口）与自动续期（6 步刷新协议）。
/// 不依赖任何后端，全部请求直接发往 B 站。
/// </summary>
public sealed class BiliAuthService : IDisposable
{
    private const string Ua =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";

    private static readonly Regex CsrfRegex = new(
        @"<div id=""1-name"">([a-z0-9]+?)</div>", RegexOptions.Compiled);

    /// <summary>B 站 correspond 接口固定 RSA 公钥（1024 位，SPKI）。</summary>
    private const string PublicKeyPem = @"-----BEGIN PUBLIC KEY-----
MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDLgd2OAkcGVtoE3ThUREbio0Eg
Uc/prcajMKXvkCKFCWhJYJcLkcM2DKKcSeFpD/j6Boy538YXnR6VhcuUJOhH2x71
nzPjfdTcqMz7djHum0qSZA0AyCBDABUqCrfNgCiJ00Ra7GmRj+YCK1NJEuewlb40
JNrRuoEUXpabUzGB8QIDAQAB
-----END PUBLIC KEY-----";

    private readonly HttpClient _http;

    public BiliAuthService()
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false, // 手动管理 Cookie 池（读取并合并 Set-Cookie）
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    public void Dispose() => _http.Dispose();

    /// <summary>验证 Cookie 是否有效（B 站 nav 接口），有效则返回用户名/UID。</summary>
    public async Task<BiliValidateResult> ValidateAsync(LocalCookie cookie, CancellationToken ct = default)
    {
        if (!cookie.IsComplete)
            return new BiliValidateResult { Valid = false, Message = "Cookie 不完整（缺少 SESSDATA 或 bili_jct）" };

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.bilibili.com/x/web-interface/nav");
        ApplyCommonHeaders(req, isPost: false);
        req.Headers.TryAddWithoutValidation("Cookie", cookie.ToCookieString());

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var code = root.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            var isLogin = data.TryGetProperty("isLogin", out var il) && il.GetBoolean();
            if (code == 0 && isLogin)
            {
                return new BiliValidateResult
                {
                    Valid = true,
                    Username = data.TryGetProperty("uname", out var u) ? u.GetString() ?? "" : "",
                    Uid = data.TryGetProperty("mid", out var m) ? m.GetRawText() : "",
                    Message = "Cookie 有效",
                };
            }
        }

        return new BiliValidateResult
        {
            Valid = false,
            Message = code == -101 ? "未登录 / SESSDATA 已失效" : "Cookie 无效",
        };
    }

    /// <summary>
    /// 用 refresh_token 触发 B 站原生 6 步刷新协议，返回更新后的 Cookie。
    /// 失败抛 <see cref="BiliAuthException"/>（含可直接展示的中文提示）。
    /// </summary>
    public async Task<LocalCookie> RefreshAsync(LocalCookie cookie, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cookie.RefreshToken))
            throw new BiliAuthException("缺少 refresh_token（ac_time_value），无法自动刷新，请重新登录获取完整 Cookie");

        var pool = BuildPool(cookie);

        // Step 0 预热：补齐 sid / buvid_fp 等会话字段
        await Step0Async(pool, ct);

        // Step 1 时间戳
        var ts = await Step1Async(pool, ct);

        // Step 2 生成 correspond 路径（本地 RSA-OAEP），生成后必须延迟 3 秒
        var path = GenerateCorrespondPath(ts);
        await Task.Delay(3000, ct);

        // Step 3 提取 refresh_csrf
        var refreshCsrf = await Step3Async(pool, path, ct);

        // Step 4 刷新
        var newRefreshToken = await Step4Async(pool, refreshCsrf, cookie.RefreshToken, ct);

        // Step 5 确认 / 作废旧 token（失败不致命）
        try { await Step5Async(pool, cookie.RefreshToken, ct); } catch { }

        // Step 6 非空覆盖
        return BuildResult(cookie, pool, newRefreshToken);
    }

    // ---------- 会话池 ----------

    private static Dictionary<string, string> BuildPool(LocalCookie c)
    {
        var pool = new Dictionary<string, string>(StringComparer.Ordinal);
        Set(pool, "SESSDATA", c.Sessdata);
        Set(pool, "bili_jct", c.BiliJct);
        Set(pool, "DedeUserID", c.DedeUserId);
        Set(pool, "sid", c.Sid);
        if (!string.IsNullOrWhiteSpace(c.DedeUserId))
            Set(pool, "DedeUserID__ckMd5", Md5Hex8(c.DedeUserId));
        Set(pool, "buvid3", GenerateBuvid());
        Set(pool, "buvid4", GenerateBuvid());
        return pool;
    }

    private static void Set(Dictionary<string, string> pool, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            pool[key] = value;
    }

    private static string? Get(Dictionary<string, string> pool, string key)
        => pool.TryGetValue(key, out var v) ? v : null;

    private static string NonEmpty(Dictionary<string, string> pool, string key, string? fallback)
        => pool.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : (fallback ?? string.Empty);

    private static string BuildCookieHeader(Dictionary<string, string> pool)
        => string.Join("; ", pool.Select(kv => $"{kv.Key}={kv.Value}"));

    private static void MergeSetCookies(HttpResponseMessage resp, Dictionary<string, string> pool)
    {
        if (!resp.Headers.TryGetValues("Set-Cookie", out var values))
            return;

        foreach (var header in values)
        {
            var pair = header.Split(';')[0].Trim();
            var idx = pair.IndexOf('=');
            if (idx <= 0) continue;
            var name = pair[..idx].Trim();
            var value = pair[(idx + 1)..].Trim();
            if (name.Length > 0)
                pool[name] = value;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, Dictionary<string, string> pool, HttpContent? content, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        ApplyCommonHeaders(req, method == HttpMethod.Post);
        if (content != null) req.Content = content;
        if (pool.Count > 0)
            req.Headers.TryAddWithoutValidation("Cookie", BuildCookieHeader(pool));
        return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static void ApplyCommonHeaders(HttpRequestMessage req, bool isPost)
    {
        req.Headers.TryAddWithoutValidation("User-Agent", Ua);
        req.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        if (isPost)
        {
            req.Headers.TryAddWithoutValidation("Origin", "https://www.bilibili.com");
            req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        }
    }

    // ---------- 各步骤 ----------

    private async Task Step0Async(Dictionary<string, string> pool, CancellationToken ct)
    {
        using var resp = await SendAsync(HttpMethod.Get, "https://www.bilibili.com/", pool, null, ct);
        MergeSetCookies(resp, pool);
        _ = resp.Content.ReadAsByteArrayAsync(ct); // 丢弃响应体
    }

    private async Task<long> Step1Async(Dictionary<string, string> pool, CancellationToken ct)
    {
        var csrf = Get(pool, "bili_jct") ?? string.Empty;
        var url = $"https://passport.bilibili.com/x/passport-login/web/cookie/info?csrf={Uri.EscapeDataString(csrf)}";
        using var resp = await SendAsync(HttpMethod.Get, url, pool, null, ct);
        MergeSetCookies(resp, pool);
        var body = await resp.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var code = root.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
        if (code == 0 && root.TryGetProperty("data", out var data))
            return data.TryGetProperty("timestamp", out var t) ? t.GetInt64() : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (code == -101)
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); // SESSDATA 失效但 refresh_token 可能仍有效，回退本地时间继续
        throw new BiliAuthException(MapBiliError("获取时间戳", code, MessageOf(root)));
    }

    private static string GenerateCorrespondPath(long timestampMs)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(PublicKeyPem);
        var plaintext = Encoding.UTF8.GetBytes($"refresh_{timestampMs}");
        var cipher = rsa.Encrypt(plaintext, RSAEncryptionPadding.OaepSHA256);
        return Convert.ToHexString(cipher).ToLowerInvariant();
    }

    private async Task<string> Step3Async(Dictionary<string, string> pool, string path, CancellationToken ct)
    {
        var url = $"https://www.bilibili.com/correspond/1/{path}";
        using var resp = await SendAsync(HttpMethod.Get, url, pool, null, ct);
        MergeSetCookies(resp, pool);
        var body = await resp.Content.ReadAsStringAsync(ct);

        var m = CsrfRegex.Match(body);
        if (!m.Success)
            throw new BiliAuthException("无法从 B 站 correspond 页面提取 refresh_csrf，请稍后重试或重新获取 Cookie");
        return m.Groups[1].Value;
    }

    private async Task<string?> Step4Async(
        Dictionary<string, string> pool, string refreshCsrf, string oldRefreshToken, CancellationToken ct)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["csrf"] = Get(pool, "bili_jct") ?? string.Empty,
            ["refresh_csrf"] = refreshCsrf,
            ["source"] = "main_web",
            ["refresh_token"] = oldRefreshToken,
        });
        using var resp = await SendAsync(
            HttpMethod.Post, "https://passport.bilibili.com/x/passport-login/web/cookie/refresh", pool, form, ct);
        MergeSetCookies(resp, pool);
        var body = await resp.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var code = root.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
        if (code != 0)
            throw new BiliAuthException(MapBiliError("刷新", code, MessageOf(root)));

        var data = root.GetProperty("data");

        // 合并新 Cookie（cookies_info.domains[].cookies[]）
        if (data.TryGetProperty("cookies_info", out var ci) && ci.TryGetProperty("domains", out var domains))
        {
            foreach (var domain in domains.EnumerateArray())
            {
                if (!domain.TryGetProperty("cookies", out var cookies)) continue;
                foreach (var item in cookies.EnumerateArray())
                {
                    var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var value = item.TryGetProperty("value", out var v) ? v.GetString() : null;
                    if (!string.IsNullOrEmpty(name) && value != null)
                        pool[name] = value;
                }
            }
        }

        if (data.TryGetProperty("refresh_token", out var rt) && rt.ValueKind != JsonValueKind.Null)
            return rt.GetString();
        return null;
    }

    private async Task Step5Async(Dictionary<string, string> pool, string oldRefreshToken, CancellationToken ct)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["csrf"] = Get(pool, "bili_jct") ?? string.Empty,
            ["refresh_token"] = oldRefreshToken,
        });
        using var resp = await SendAsync(
            HttpMethod.Post, "https://passport.bilibili.com/x/passport-login/web/confirm/refresh", pool, form, ct);
        MergeSetCookies(resp, pool);
    }

    private static LocalCookie BuildResult(LocalCookie old, Dictionary<string, string> pool, string? newRefreshToken)
    {
        return new LocalCookie
        {
            Sessdata = NonEmpty(pool, "SESSDATA", old.Sessdata),
            BiliJct = NonEmpty(pool, "bili_jct", old.BiliJct),
            DedeUserId = NonEmpty(pool, "DedeUserID", old.DedeUserId),
            Sid = NonEmpty(pool, "sid", old.Sid),
            RefreshToken = !string.IsNullOrWhiteSpace(newRefreshToken) ? newRefreshToken : old.RefreshToken,
            SavedAt = DateTimeOffset.UtcNow,
        };
    }

    // ---------- 工具 ----------

    private static string GenerateBuvid()
        => Guid.NewGuid().ToString("N").ToUpperInvariant()
           + Convert.ToHexString(RandomNumberGenerator.GetBytes(6))
           + "infoc";

    private static string Md5Hex8(string input)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }

    private static string MessageOf(JsonElement root)
        => root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString() ?? string.Empty
            : string.Empty;

    private static string MapBiliError(string step, int code, string message) => code switch
    {
        86095 => "Cookie 续期失败：登录态与 refresh_token 不匹配，请重新登录并在同一次会话中重新抓取全部字段",
        -101 => "未登录或 SESSDATA 已失效，请重新登录后重新获取 Cookie",
        -111 => "bili_jct（csrf）校验失败，请重新获取完整 Cookie",
        _ => $"B 站{step}失败（code={code}）：{message}",
    };
}