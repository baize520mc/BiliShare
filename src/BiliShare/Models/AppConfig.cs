namespace BiliShare.Models;

/// <summary>客户端运行模式。</summary>
public enum AppMode
{
    /// <summary>在线模式：连接 Halo 插件后端，由服务端集中管理 Cookie（PAT 鉴权、自动续期）。</summary>
    Online,

    /// <summary>离线模式：本地登录 B 站捕获 Cookie，AES-256-GCM 加密存本地。</summary>
    Offline,
}

/// <summary>界面主题。</summary>
public enum AppTheme
{
    /// <summary>跟随系统：浅色/深色随系统主题自动切换。</summary>
    System,

    /// <summary>固定浅色。</summary>
    Light,

    /// <summary>固定深色。</summary>
    Dark,
}

/// <summary>
/// 应用配置（持久化到 <c>data/config.json</c>）。
/// </summary>
public class AppConfig
{
    /// <summary>运行模式。</summary>
    public AppMode Mode { get; set; } = AppMode.Online;

    /// <summary>在线模式的 Halo 服务端地址（不含 /apis/... 后缀）。</summary>
    public string ServerBaseUrl { get; set; } = string.Empty;

    /// <summary>Halo 个人访问令牌（PAT）。</summary>
    public string PAT { get; set; } = string.Empty;

    /// <summary>是否已完成首次引导（false 表示启动时先展示引导页）。</summary>
    public bool IsInitialized { get; set; }

    /// <summary>界面主题（默认跟随系统）。</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;
}
