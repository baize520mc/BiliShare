namespace BiliShare.Services;

/// <summary>清除所有本地数据并重启应用（恢复到首次引导状态）。</summary>
public static class DataCleaner
{
    /// <summary>
    /// 删除可删的本地数据文件，写「下次启动清理」标记，启动新实例并退出当前实例。
    /// 仅清除本地 data 目录下的所有数据（含离线数据），不调用任何在线清除 API（如 POST /cookie/clear）。
    /// WebView2 用户数据目录因运行期被 WebView2 进程锁定，交给新实例在启动早期清理。
    /// </summary>
    public static void ResetAndRestart()
    {
        TryDeleteFile(Paths.ConfigFile);
        TryDeleteFile(Paths.LocalCookieFile);
        TryDeleteFile(Paths.LocalKeyFile);
        TryDeleteFile(Paths.CrashLogFile);
        TryDeleteDirectory(Paths.OfflineDataDir);
        TryDeleteDirectory(Paths.LogDir);

        // 运行期被锁定的 WebView2 用户数据：写标记，交给下一进程启动早期清空
        try
        {
            Paths.EnsureDirectories();
            File.WriteAllText(Paths.CleanupMarkerFile, "reset");
        }
        catch
        {
        }

        // 重启：先拉起新实例，再硬退出当前实例（立即释放 WebView2 文件锁）
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
        {
            try { System.Diagnostics.Process.Start(exe); }
            catch { }
        }
        Environment.Exit(0);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
        }
    }
}