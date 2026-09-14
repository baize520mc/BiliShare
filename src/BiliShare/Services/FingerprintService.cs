using System.Net.Http;
using System.Text.Json;
using BiliShare.Models;

namespace BiliShare.Services;

/// <summary>
/// B 站设备指纹服务：优先调用公开的 <c>spi</c> 接口获取 buvid3/buvid4，失败时本地 fallback 生成。
/// buvid_fp 依赖前端 JS 算法，当前以随机占位生成，联调阶段校准。
/// </summary>
public sealed class FingerprintService : IDisposable
{
    private const string SpiUrl = "https://api.bilibili.com/x/frontend/finger/spi";

    private readonly HttpClient _http;

    public FingerprintService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>获取设备指纹（优先服务端，失败回退本地生成）。</summary>
    public async Task<BiliFingerprint> GetFingerprintAsync(CancellationToken ct = default)
    {
        var fp = await TryFromSpiAsync(ct);
        return fp ?? GenerateFallback();
    }

    public void Dispose() => _http.Dispose();

    private async Task<BiliFingerprint?> TryFromSpiAsync(CancellationToken ct)
    {
        try
        {
            var json = await _http.GetStringAsync(SpiUrl, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("code", out var code) && code.GetInt32() != 0)
                return null;
            if (!root.TryGetProperty("data", out var data))
                return null;

            var b3 = data.TryGetProperty("b_3", out var v3) ? v3.GetString() : null;
            var b4 = data.TryGetProperty("b_4", out var v4) ? v4.GetString() : null;
            if (string.IsNullOrEmpty(b3) || string.IsNullOrEmpty(b4))
                return null;

            return Build(b3, b4, fromServer: true);
        }
        catch
        {
            return null;
        }
    }

    private static BiliFingerprint GenerateFallback()
    {
        var guid = Guid.NewGuid();
        return Build(guid.ToString("N") + "infoc", guid.ToString("D"), fromServer: false);
    }

    private static BiliFingerprint Build(string buvid3, string buvid4, bool fromServer) => new()
    {
        Buvid3 = buvid3,
        Buvid4 = buvid4,
        BuvidFp = GenerateBuvidFp(),
        BNut = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        CurrentFnval = 4048,
        FromServer = fromServer,
    };

    private static string GenerateBuvidFp()
    {
        // 真实 buvid_fp 由前端 fourier JS 计算，此处以随机十六进制占位
        Span<byte> buf = stackalloc byte[16];
        Random.Shared.NextBytes(buf);
        return Convert.ToHexString(buf).ToLowerInvariant();
    }
}