using System.Text.Json;
using BiliShare.Models;
using BiliShare.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;

namespace BiliShare.Views;

/// <summary>
/// 首次启动引导窗口：WebView2 加载 <c>wwwroot/setup.html</c>，通过 WebMessage 桥接完成模式/在线配置并保存。
/// </summary>
public sealed partial class SetupWindow : Window
{
    private const string VirtualHost = "app.local";

    private readonly AppConfig _config;

    /// <summary>引导完成（配置已保存），由 App 订阅以切换到主窗口。</summary>
    public event Action? Completed;

    public SetupWindow(AppConfig config)
    {
        _config = config;
        this.InitializeComponent();
        this.AppWindow.Title = "BiliShare · 初始设置";
        this.AppWindow.Resize(new Windows.Graphics.SizeInt32(820, 900));
        if (this.AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
        InitializeWebViewAsync();
    }

    private async void InitializeWebViewAsync()
    {
        var wwwroot = System.IO.Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var userDataDir = System.IO.Path.Combine(Paths.WebView2UserDataDir, "setup_profile");
        System.IO.Directory.CreateDirectory(userDataDir);

        var env = await CoreWebView2Environment.CreateWithOptionsAsync(
            null, userDataDir, new CoreWebView2EnvironmentOptions());

        await WebView.EnsureCoreWebView2Async(env);

        WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            VirtualHost, wwwroot, CoreWebView2HostResourceAccessKind.Allow);
        WebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        WebView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
        WebView.CoreWebView2.Navigate($"https://{VirtualHost}/setup.html");
    }

    private async void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var doc = JsonDocument.Parse(args.WebMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "save")
                return;

            var mode = root.TryGetProperty("mode", out var m) && m.GetString() == "offline"
                ? AppMode.Offline : AppMode.Online;
            var serverBaseUrl = root.TryGetProperty("serverBaseUrl", out var u) ? u.GetString() ?? "" : "";
            var pat = root.TryGetProperty("pat", out var p) ? p.GetString() ?? "" : "";

            // 在线模式：保存前校验 PAT 与服务端连通性（GET /status），失败回传错误并停留在引导页
            if (mode == AppMode.Online)
            {
                try
                {
                    using var api = new ApiClient(serverBaseUrl, pat);
                    await api.GetStatusAsync();
                }
                catch (ApiException ex)
                {
                    PostErrorToPage(ex.UserHint);
                    return;
                }
                catch
                {
                    PostErrorToPage("无法连接服务器，请检查服务端地址与网络");
                    return;
                }
            }

            _config.Mode = mode;
            _config.ServerBaseUrl = serverBaseUrl;
            _config.PAT = pat;
            _config.IsInitialized = true;

            ConfigService.Save(_config);

            // 先在主线程唤起主窗口，再关闭引导窗，避免应用因无窗口而退出
            DispatcherQueue.TryEnqueue(() =>
            {
                Completed?.Invoke();
                this.Close();
            });
        }
        catch
        {
            // 忽略解析失败，用户可重新提交
        }
    }

    /// <summary>将错误信息回传给引导页 JS 以便展示。</summary>
    private void PostErrorToPage(string message)
    {
        try
        {
            var json = JsonSerializer.Serialize(new { type = "error", message });
            WebView.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch
        {
        }
    }

    /// <summary>引导页外链（注册 / 教程 / 服务器）用系统默认浏览器打开，而非在 WebView2 内部导航。</summary>
    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(args.Uri)
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            // 默认浏览器打开失败时静默忽略
        }
    }
}