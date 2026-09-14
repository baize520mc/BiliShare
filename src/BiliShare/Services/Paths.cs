namespace BiliShare.Services;

/// <summary>
/// 统一路径服务：所有配置、数据、日志、WebView2 用户数据，一律存放到软件根目录 <c>data/</c> 下。
/// 不写入 %LocalAppData% / %AppData% / %Temp% 等系统目录；删除软件根目录即可彻底清除、不留痕。
/// </summary>
public static class Paths
{
    /// <summary>软件根目录（exe 所在目录，即 <see cref="AppContext.BaseDirectory"/>）。</summary>
    public static string RootDir { get; } = AppContext.BaseDirectory;

    /// <summary>数据目录：软件根目录/data。</summary>
    public static string DataDir { get; } = System.IO.Path.Combine(RootDir, "data");

    /// <summary>离线模式数据目录 data/offline（与在线配置隔离：离线 Cookie/密钥只存本地此目录）。</summary>
    public static string OfflineDataDir => System.IO.Path.Combine(DataDir, "offline");

    /// <summary>离线模式加密密钥 data/offline/key.dat。</summary>
    public static string LocalKeyFile => System.IO.Path.Combine(OfflineDataDir, "key.dat");

    /// <summary>离线模式本地加密 Cookie data/offline/cookie.dat。</summary>
    public static string LocalCookieFile => System.IO.Path.Combine(OfflineDataDir, "cookie.dat");

    /// <summary>配置文件 config.json（全局：运行模式 + 在线配置，与离线 Cookie 数据分离）。</summary>
    public static string ConfigFile => System.IO.Path.Combine(DataDir, "config.json");

    /// <summary>日志目录 data/logs。</summary>
    public static string LogDir => System.IO.Path.Combine(DataDir, "logs");

    /// <summary>崩溃日志 data/logs/crash.log。</summary>
    public static string CrashLogFile => System.IO.Path.Combine(LogDir, "crash.log");

    /// <summary>WebView2 用户数据目录 data/webview2_userdata（多标签共享环境 + 独立登录窗口各自的 user data folder）。</summary>
    public static string WebView2UserDataDir => System.IO.Path.Combine(DataDir, "webview2_userdata");

    /// <summary>「清除所有数据」标记文件：下次启动时清理运行期被锁定的 WebView2 用户数据目录。</summary>
    public static string CleanupMarkerFile => System.IO.Path.Combine(DataDir, ".reset");

    /// <summary>确保 data 及常用子目录存在（幂等）。</summary>
    public static void EnsureDirectories()
    {
        System.IO.Directory.CreateDirectory(DataDir);
        System.IO.Directory.CreateDirectory(OfflineDataDir);
        System.IO.Directory.CreateDirectory(LogDir);
        System.IO.Directory.CreateDirectory(WebView2UserDataDir);
    }
}