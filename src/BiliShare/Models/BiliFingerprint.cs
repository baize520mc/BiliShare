namespace BiliShare.Models;

/// <summary>
/// B 站设备指纹，最终以 Cookie 形式注入（buvid3/buvid4/buvid_fp/b_nut/CURRENT_FNVAL 等）。
/// </summary>
public sealed class BiliFingerprint
{
    /// <summary>buvid3。</summary>
    public string Buvid3 { get; init; } = string.Empty;

    /// <summary>buvid4。</summary>
    public string Buvid4 { get; init; } = string.Empty;

    /// <summary>buvid_fp（设备指纹，前端 JS 算法产物）。</summary>
    public string BuvidFp { get; init; } = string.Empty;

    /// <summary>b_nut：生成时刻的 Unix 时间戳（秒）。</summary>
    public long BNut { get; init; }

    /// <summary>CURRENT_FNVAL：请求返回格式位图（B 站常用 4048）。</summary>
    public int CurrentFnval { get; init; } = 4048;

    /// <summary>是否来自服务端 spi 接口（false 表示本地 fallback 生成）。</summary>
    public bool FromServer { get; init; }

    /// <summary>转为 cookie 键值对（不含 SESSDATA 等登录态）。</summary>
    public IEnumerable<KeyValuePair<string, string>> ToCookies()
    {
        if (!string.IsNullOrEmpty(Buvid3)) yield return new("buvid3", Buvid3);
        if (!string.IsNullOrEmpty(Buvid4)) yield return new("buvid4", Buvid4);
        if (!string.IsNullOrEmpty(BuvidFp)) yield return new("buvid_fp", BuvidFp);
        yield return new("b_nut", BNut.ToString());
        yield return new("CURRENT_FNVAL", CurrentFnval.ToString());
    }
}