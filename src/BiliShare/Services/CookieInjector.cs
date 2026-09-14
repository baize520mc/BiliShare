using BiliShare.Models;
using Microsoft.Web.WebView2.Core;

namespace BiliShare.Services;

/// <summary>
/// Cookie 注入：将登录态 Cookie 串 / 设备指纹写入 WebView2 的 CookieManager。
/// </summary>
public static class CookieInjector
{
    /// <summary>将 Cookie 串（<c>name=value; ...</c>）逐条注入到指定域名。</summary>
    public static void Inject(CoreWebView2 core, string cookieString, string domain = ".bilibili.com")
    {
        var manager = core.CookieManager;
        foreach (var pair in ParseCookieString(cookieString))
        {
            var cookie = manager.CreateCookie(pair.Key, pair.Value, domain, "/");
            // SESSDATA 为 HttpOnly 登录态 Cookie
            cookie.IsHttpOnly = string.Equals(pair.Key, "SESSDATA", StringComparison.OrdinalIgnoreCase);
            manager.AddOrUpdateCookie(cookie);
        }
    }

    /// <summary>注入设备指纹 Cookie（buvid3/buvid4/buvid_fp/b_nut/CURRENT_FNVAL）。</summary>
    public static void Inject(CoreWebView2 core, BiliFingerprint fingerprint, string domain = ".bilibili.com")
    {
        var manager = core.CookieManager;
        foreach (var pair in fingerprint.ToCookies())
        {
            var cookie = manager.CreateCookie(pair.Key, pair.Value, domain, "/");
            manager.AddOrUpdateCookie(cookie);
        }
    }

    private static IEnumerable<KeyValuePair<string, string>> ParseCookieString(string cookieString)
    {
        if (string.IsNullOrWhiteSpace(cookieString))
            yield break;

        foreach (var part in cookieString.Split(';'))
        {
            var idx = part.IndexOf('=');
            if (idx <= 0)
                continue;

            var name = part[..idx].Trim();
            var value = part[(idx + 1)..].Trim();
            if (string.IsNullOrEmpty(name))
                continue;

            yield return new KeyValuePair<string, string>(name, value);
        }
    }
}