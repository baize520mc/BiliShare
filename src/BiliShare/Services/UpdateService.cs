using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BiliShare.Services;

/// <summary>单次更新检查结果：最新版本、更新说明、便携包下载地址，以及是否比当前版本新。</summary>
public sealed class UpdateInfo
{
    public string Version { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;

    /// <summary>代理加速下载地址（默认使用 gh-proxy 前缀，加速国内下载）。</summary>
    public string DownloadUrl { get; init; } = string.Empty;

    /// <summary>官方直连下载地址（代理不可用时的回退）。</summary>
    public string DirectDownloadUrl { get; init; } = string.Empty;

    public bool IsNewer { get; init; }
}

/// <summary>更新进度：状态文本 + 0~1 进度值（下载、解压共用）。</summary>
public sealed record UpdateProgress(string Status, double Ratio);

/// <summary>
/// 更新服务：从 GitHub Releases 拉取最新版本，并下载便携 ZIP 自动覆盖当前应用后重启。
/// 下载/解压均上报数值进度；覆盖前先退出当前进程，由外部 PowerShell 脚本在进程完全退出后
/// 把新文件覆盖到软件根目录（更新完全不触碰本地 <c>data/</c> 目录）。
/// </summary>
public static class UpdateService
{
    private const string Owner = "baize520mc";
    private const string Repo = "BiliShare";

    /// <summary>GitHub 下载加速代理前缀（拼接在官方下载地址之前）。</summary>
    private const string ProxyPrefix = "https://gh-proxy.org/";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(2),
    };

    static UpdateService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("BiliShare-Updater");
    }

    /// <summary>当前应用版本（x.y.z，来自程序集版本主.次.构建）。</summary>
    public static string CurrentVersion
    {
        get
        {
            var v = typeof(UpdateService).Assembly.GetName().Version;
            return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    private static Version? CurrentAssemVersion => typeof(UpdateService).Assembly.GetName().Version;

    /// <summary>
    /// 检查最新版本。找不到可用的便携包或网络错误时抛出异常（由调用方展示）。
    /// 返回的 <see cref="UpdateInfo.DownloadUrl"/> 默认带 gh-proxy 加速前缀。
    /// </summary>
    public static async Task<UpdateInfo> CheckAsync()
    {
        var json = await Http.GetStringAsync(
            $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");

        var release = JsonSerializer.Deserialize<ReleaseResponse>(json)
            ?? throw new InvalidOperationException("无法解析版本信息");

        var latest = ParseVersion(release.TagName);
        var direct = release.Assets?
            .FirstOrDefault(a => a.Name?.EndsWith("_portable.zip", StringComparison.OrdinalIgnoreCase) == true)
            ?.BrowserDownloadUrl
            ?? release.Assets?.FirstOrDefault(a => a.Name?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true)
            ?.BrowserDownloadUrl;

        if (latest == null || string.IsNullOrWhiteSpace(direct))
            throw new InvalidOperationException("发布中缺少可用的便携包");

        var current = CurrentAssemVersion;
        return new UpdateInfo
        {
            Version = $"{latest.Major}.{latest.Minor}.{latest.Build}",
            Notes = Truncate(release.Body, 500),
            DownloadUrl = ProxyPrefix + direct,
            DirectDownloadUrl = direct,
            IsNewer = current == null || latest > current,
        };
    }

    /// <summary>
    /// 下载并应用更新：下载（带进度）→ 解压到临时目录（带进度）→
    /// 经 <paramref name="confirmBeforeClose"/> 确认后写 PowerShell 脚本并退出当前进程，
    /// 由脚本在进程完全退出后覆盖文件并重启。
    /// </summary>
    /// <returns>
    /// true 表示已开始应用（进程随后退出，调用方无需再处理）；
    /// false 表示用户在确认弹窗中取消了本次更新。
    /// </returns>
    public static async Task<bool> ApplyAsync(
        UpdateInfo info,
        IProgress<UpdateProgress>? progress = null,
        Func<Task<bool>>? confirmBeforeClose = null)
    {
        var updatesDir = Path.Combine(Paths.DataDir, "updates");
        Directory.CreateDirectory(updatesDir);
        var zipPath = Path.Combine(updatesDir, "update.zip");
        var stageDir = Path.Combine(updatesDir, "stage");

        try
        {
            // ---------- 下载（数值进度） ----------
            progress?.Report(new UpdateProgress("正在下载更新…", 0));
            using (var resp = await DownloadAsync(info))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? -1;
                await using var fs = File.Create(zipPath);
                await using var stream = await resp.Content.ReadAsStreamAsync();
                var buffer = new byte[64 * 1024];
                long received = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read));
                    received += read;
                    if (total > 0)
                        progress?.Report(new UpdateProgress(
                            $"正在下载更新… {received * 100 / total}%",
                            (double)received / total));
                }
            }

            // ---------- 解压（逐条目解压，按字节数上报进度） ----------
            progress?.Report(new UpdateProgress("正在解压…", 0));
            if (Directory.Exists(stageDir))
                Directory.Delete(stageDir, true);
            Directory.CreateDirectory(stageDir);

            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var totalBytes = archive.Entries.Sum(e => e.Length);
                long extracted = 0;
                foreach (var entry in archive.Entries)
                {
                    var dest = Path.GetFullPath(Path.Combine(stageDir, entry.FullName));
                    if (!dest.StartsWith(stageDir, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("压缩包包含非法路径");

                    if (entry.FullName.EndsWith('/'))
                    {
                        Directory.CreateDirectory(dest);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, overwrite: true);
                    extracted += entry.Length;

                    if (totalBytes > 0)
                        progress?.Report(new UpdateProgress(
                            $"正在解压… {extracted * 100 / totalBytes}%",
                            (double)extracted / totalBytes));
                }
            }
            File.Delete(zipPath);

            // ---------- 应用前确认（即将关闭软件提醒） ----------
            if (confirmBeforeClose != null && !await confirmBeforeClose())
            {
                // 用户取消：清理中间产物，不关闭应用
                if (Directory.Exists(stageDir))
                    Directory.Delete(stageDir, true);
                return false;
            }

            progress?.Report(new UpdateProgress("正在应用更新，应用即将关闭…", 1));
            ApplyFromStage(stageDir, info.Version);
            return true; // 实际不会走到：ApplyFromStage 内已退出进程
        }
        catch
        {
            // 清理失败的中间产物，避免残留影响下次更新
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            try { if (Directory.Exists(stageDir)) Directory.Delete(stageDir, true); } catch { }
            throw;
        }
    }

    /// <summary>优先使用代理地址下载；代理不可用或返回错误时回退官方直连。</summary>
    private static async Task<HttpResponseMessage> DownloadAsync(UpdateInfo info)
    {
        if (string.IsNullOrEmpty(info.DownloadUrl))
            return await Http.GetAsync(info.DirectDownloadUrl, HttpCompletionOption.ResponseHeadersRead);

        try
        {
            var resp = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            if (resp.IsSuccessStatusCode)
                return resp;
            resp.Dispose();
        }
        catch
        {
            // 代理不可达，走官方直连
        }

        return await Http.GetAsync(info.DirectDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
    }

    /// <summary>
    /// 写一个 PowerShell 脚本并后台启动：等待当前进程完全退出（超时则强制结束）→ 结束残留
    /// WebView2 子进程 → 覆盖文件 → 重启新版 → 自清理。随后立即退出当前进程以释放文件锁。
    /// </summary>
    private static void ApplyFromStage(string stageDir, string newVersion)
    {
        var root = Paths.RootDir;
        var exe = Environment.ProcessPath ?? Path.Combine(root, "BiliShare.exe");
        var scriptPath = Path.Combine(Path.GetDirectoryName(stageDir)!, "apply.ps1");

        var script =
            "$ErrorActionPreference = 'SilentlyContinue'\r\n" +
            $"$root = {Pq(root)}\r\n" +
            $"$stage = {Pq(stageDir)}\r\n" +
            $"$exe = {Pq(exe)}\r\n" +
            $"$newVersion = {Pq(newVersion)}\r\n" +
            // 1) 等待主程序完全退出（10 秒未退出则强制结束，避免文件被占用）
            "$deadline = (Get-Date).AddSeconds(10)\r\n" +
            "while ((Get-Process BiliShare -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }\r\n" +
            "Get-Process BiliShare -ErrorAction SilentlyContinue | Stop-Process -Force\r\n" +
            "Start-Sleep -Milliseconds 600\r\n" +
            // 2) 结束残留的 WebView2 子进程（可能占用将被覆盖的文件）
            "Get-Process msedgewebview2 -ErrorAction SilentlyContinue | Stop-Process -Force\r\n" +
            "Start-Sleep -Milliseconds 600\r\n" +
            // 3) 覆盖文件（重试 20 次，失败则记录日志）
            "$ok = $false\r\n" +
            "for ($i = 0; $i -lt 20 -and -not $ok; $i++) {\r\n" +
            "  try {\r\n" +
            "    Copy-Item -Path (Join-Path $stage '*') -Destination $root -Recurse -Force -ErrorAction Stop\r\n" +
            "    $ok = $true\r\n" +
            "  } catch { Start-Sleep -Milliseconds 400 }\r\n" +
            "}\r\n" +
            "if (-not $ok) { 'UPDATE_COPY_FAILED' | Out-File -FilePath (Join-Path $root 'data\\updates\\apply.log') -Encoding utf8 }\r\n" +
            // 4) 清理并重启新版
            "Remove-Item $stage -Recurse -Force\r\n" +
            "Set-Location $root\r\n" +
            "Start-Process -FilePath $exe -ArgumentList ('--updated=' + $newVersion) -WorkingDirectory $root\r\n" +
            "Remove-Item $PSCommandPath -Force\r\n";

        // 带 BOM 写入：PowerShell 5.1 读无 BOM 文件按 ANSI(GBK) 解码，中文路径会乱码导致覆盖/重启失败
        File.WriteAllText(scriptPath, script, new System.Text.UTF8Encoding(true));

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        Environment.Exit(0);
    }

    /// <summary>PowerShell 单引号转义：内部单引号翻倍。</summary>
    private static string Pq(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>解析形如 <c>v1.0.1</c> / <c>1.0.1</c> 的版本号（忽略后置 -beta 等后缀）。</summary>
    private static Version? ParseVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var s = raw.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V'))
            s = s[1..];

        var cut = s.IndexOfAny(new[] { '-', ' ' });
        if (cut >= 0)
            s = s[..cut];

        return Version.TryParse(s.Trim(), out var v) ? v : null;
    }

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        var t = text.Trim();
        return t.Length <= max ? t : t[..max] + "…";
    }

    private sealed class ReleaseResponse
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("assets")]
        public List<AssetResponse>? Assets { get; set; }
    }

    private sealed class AssetResponse
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}