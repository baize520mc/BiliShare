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
    public string DownloadUrl { get; init; } = string.Empty;
    public bool IsNewer { get; init; }
}

/// <summary>
/// 更新服务：从 GitHub Releases 拉取最新版本，并下载便携 ZIP 自动覆盖当前应用后重启。
/// 更新完全不触碰本地 <c>data/</c> 目录（配置、Cookie、日志等均保留）。
/// </summary>
public static class UpdateService
{
    private const string Owner = "baize520mc";
    private const string Repo = "BiliShare";

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
    /// </summary>
    public static async Task<UpdateInfo> CheckAsync()
    {
        var json = await Http.GetStringAsync(
            $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");

        var release = JsonSerializer.Deserialize<ReleaseResponse>(json)
            ?? throw new InvalidOperationException("无法解析版本信息");

        var latest = ParseVersion(release.TagName);
        var download = release.Assets?
            .FirstOrDefault(a => a.Name?.EndsWith("_portable.zip", StringComparison.OrdinalIgnoreCase) == true)
            ?.BrowserDownloadUrl
            ?? release.Assets?.FirstOrDefault(a => a.Name?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true)
            ?.BrowserDownloadUrl;

        if (latest == null || string.IsNullOrWhiteSpace(download))
            throw new InvalidOperationException("发布中缺少可用的便携包");

        var current = CurrentAssemVersion;
        return new UpdateInfo
        {
            Version = $"{latest.Major}.{latest.Minor}.{latest.Build}",
            Notes = Truncate(release.Body, 500),
            DownloadUrl = download,
            IsNewer = current == null || latest > current,
        };
    }

    /// <summary>下载并应用更新：解压到临时目录，写更新脚本后退出当前进程，由脚本覆盖文件并重启。</summary>
    public static async Task ApplyAsync(UpdateInfo info, IProgress<string>? progress = null)
    {
        progress?.Report("正在下载更新…");

        var updatesDir = Path.Combine(Paths.DataDir, "updates");
        Directory.CreateDirectory(updatesDir);
        var zipPath = Path.Combine(updatesDir, "update.zip");

        using (var resp = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            using var fs = File.Create(zipPath);
            using var stream = await resp.Content.ReadAsStreamAsync();
            await stream.CopyToAsync(fs);
        }

        progress?.Report("正在解压…");
        var stageDir = Path.Combine(updatesDir, "stage");
        if (Directory.Exists(stageDir))
            Directory.Delete(stageDir, true);
        ZipFile.ExtractToDirectory(zipPath, stageDir);

        progress?.Report("正在应用更新，应用即将重启…");
        ApplyFromStage(stageDir);
    }

    /// <summary>
    /// 写一个 PowerShell 脚本并后台启动：等待当前进程退出 → 覆盖文件 → 重启新版 → 自清理。
    /// 随后立即退出当前进程以释放文件锁。
    /// </summary>
    private static void ApplyFromStage(string stageDir)
    {
        var root = Paths.RootDir;
        var exe = Environment.ProcessPath ?? Path.Combine(root, "BiliShare.exe");
        var scriptPath = Path.Combine(Path.GetDirectoryName(stageDir)!, "apply.ps1");

        var script =
            "$ErrorActionPreference = 'SilentlyContinue'\r\n" +
            $"$root = {Pq(root)}\r\n" +
            $"$stage = {Pq(stageDir)}\r\n" +
            $"$exe = {Pq(exe)}\r\n" +
            "while (Get-Process BiliShare -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 400 }\r\n" +
            "Start-Sleep -Milliseconds 800\r\n" +
            "Get-Process msedgewebview2 -ErrorAction SilentlyContinue | Stop-Process -Force\r\n" +
            "Start-Sleep -Milliseconds 500\r\n" +
            "$ok = $false\r\n" +
            "for ($i = 0; $i -lt 10 -and -not $ok; $i++) {\r\n" +
            "  try {\r\n" +
            "    Copy-Item -Path (Join-Path $stage '*') -Destination $root -Recurse -Force -ErrorAction Stop\r\n" +
            "    $ok = $true\r\n" +
            "  } catch { Start-Sleep -Milliseconds 500 }\r\n" +
            "}\r\n" +
            "Remove-Item $stage -Recurse -Force\r\n" +
            "Set-Location $root\r\n" +
            "Start-Process -FilePath $exe -WorkingDirectory $root\r\n" +
            "Remove-Item $PSCommandPath -Force\r\n";

        File.WriteAllText(scriptPath, script, new System.Text.UTF8Encoding(false));

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