using System.Linq;
using System.Text.Json;
using BiliShare.Models;
using BiliShare.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Streams;

namespace BiliShare.Views;

/// <summary>
/// 离线模式登录窗口：独立 WebView2 环境加载 B 站登录页，
/// 登录成功后从 CookieManager 捕获 SESSDATA/bili_jct 等，尽力从登录接口响应提取 refresh_token。
/// </summary>
public sealed partial class LoginWindow : Window
{
    private readonly string _tempUserDataDir;
    private string? _refreshToken;
    private bool _captured;
    private DispatcherTimer? _pollTimer;

    /// <summary>捕获完成：成功返回 <see cref="LocalCookie"/>，失败/取消返回 null。</summary>
    public event Action<LocalCookie?>? Completed;

    public LoginWindow()
    {
        this.InitializeComponent();
        this.AppWindow.Title = "登录 B 站";
        this.AppWindow.Resize(new Windows.Graphics.SizeInt32(500, 720));
        // 默认最大化启动，便于完整展示 B 站登录页
        if (this.AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();

        _tempUserDataDir = System.IO.Path.Combine(Paths.WebView2UserDataDir, $"login_{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(_tempUserDataDir);

        ThemeService.Attach(RootGrid);
        this.Closed += (_, _) => { CleanupTempDirAsync(); ThemeService.Detach(RootGrid); };
        InitializeWebViewAsync();
    }

    private async void InitializeWebViewAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateWithOptionsAsync(
                null, _tempUserDataDir, new CoreWebView2EnvironmentOptions());

            await WebView.EnsureCoreWebView2Async(env);
            var core = WebView.CoreWebView2;

            // 拦截登录接口响应，捕获 refresh_token（尽力而为）
            core.AddWebResourceRequestedFilter("*passport.bilibili.com*", CoreWebView2WebResourceContext.All);
            core.WebResourceResponseReceived += OnResponseReceived;

            // 登录成功后会发生跳转，跳转完成即检查 Cookie
            core.NavigationCompleted += (_, _) => _ = TryCaptureAsync();

            // 兜底轮询（扫码成功到跳转之间可能存在空档）
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _pollTimer.Tick += async (_, _) => await TryCaptureAsync();
            _pollTimer.Start();

            core.Navigate("https://passport.bilibili.com/login");
        }
        catch
        {
            Finish(null);
        }
    }

    private async void OnResponseReceived(CoreWebView2 sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        try
        {
            if (_refreshToken != null)
                return;

            var uri = e.Request.Uri;
            if (!uri.Contains("passport.bilibili.com"))
                return;
            // 覆盖扫码轮询、OAuth、密码登录(web/login)、验证码登录(web/sms/login)等所有登录响应
            if (!(uri.Contains("qrcode/poll") || uri.Contains("oauth2")
                  || uri.Contains("web/login") || uri.Contains("web/sms/login")))
                return;

            var status = e.Response?.StatusCode ?? 0;
            if (status < 200 || status >= 300)
                return;

            var body = await ReadBodyAsync(e.Response!);
            if (string.IsNullOrWhiteSpace(body))
                return;

            var token = ExtractRefreshToken(body);
            if (!string.IsNullOrWhiteSpace(token))
                _refreshToken = token;
        }
        catch
        {
        }
    }

    private async Task<bool> TryCaptureAsync()
    {
        if (_captured)
            return true;

        try
        {
            var cookies = await WebView.CoreWebView2.CookieManager.GetCookiesAsync("https://www.bilibili.com");
            var sessdata = cookies.FirstOrDefault(c => c.Name == "SESSDATA")?.Value;
            if (string.IsNullOrEmpty(sessdata))
                return false;

            // 已登录但缺少 refresh_token：提示并重新开始登录流程
            if (string.IsNullOrWhiteSpace(_refreshToken))
            {
                _refreshToken = null;
                await RestartLoginAsync();
                return false;
            }

            var cookie = new LocalCookie
            {
                Sessdata = sessdata,
                BiliJct = cookies.FirstOrDefault(c => c.Name == "bili_jct")?.Value ?? string.Empty,
                DedeUserId = cookies.FirstOrDefault(c => c.Name == "DedeUserID")?.Value,
                Sid = cookies.FirstOrDefault(c => c.Name == "sid")?.Value,
                RefreshToken = _refreshToken,
                SavedAt = DateTimeOffset.UtcNow,
            };

            _captured = true;
            Finish(cookie);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>提示缺失 refresh_token，清空 Cookie 并重新加载登录页，回到未捕获状态。</summary>
    private async Task RestartLoginAsync()
    {
        try
        {
            var core = WebView.CoreWebView2;
            var cookies = await core.CookieManager.GetCookiesAsync("https://www.bilibili.com");
            foreach (var c in cookies)
            {
                try { core.CookieManager.DeleteCookie(c); } catch { }
            }
        }
        catch { }

        DispatcherQueue.TryEnqueue(() =>
        {
            LoginHint.Visibility = Visibility.Visible;
            _captured = false;
            _refreshToken = null;
            _pollTimer?.Stop();
            _pollTimer = null;

            // 重新开始轮询，等待新一轮登录
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _pollTimer.Tick += async (_, _) => await TryCaptureAsync();
            _pollTimer.Start();

            var webview = WebView;
            if (webview?.CoreWebView2 != null)
                webview.CoreWebView2.Navigate("https://passport.bilibili.com/login");
        });
    }

    private void Finish(LocalCookie? cookie)
    {
        _pollTimer?.Stop();
        _pollTimer = null;

        DispatcherQueue.TryEnqueue(() =>
        {
            Completed?.Invoke(cookie);
            this.Close();
        });
    }

    private static async Task<string?> ReadBodyAsync(CoreWebView2WebResourceResponseView response)
    {
        using var content = await response.GetContentAsync();
        if (content == null || content.Size == 0)
            return null;

        var size = (uint)content.Size;
        using var reader = new DataReader(content.GetInputStreamAt(0));
        await reader.LoadAsync(size);
        return reader.ReadString(size);
    }

    private static string? ExtractRefreshToken(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return FindRefreshToken(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    private static string? FindRefreshToken(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                {
                    if (prop.Name == "refresh_token" && prop.Value.ValueKind == JsonValueKind.String)
                        return prop.Value.GetString();
                    var found = FindRefreshToken(prop.Value);
                    if (found != null)
                        return found;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                {
                    var found = FindRefreshToken(item);
                    if (found != null)
                        return found;
                }
                break;
        }
        return null;
    }

    private async void CleanupTempDirAsync()
    {
        try
        {
            await Task.Delay(600);
            if (System.IO.Directory.Exists(_tempUserDataDir))
                System.IO.Directory.Delete(_tempUserDataDir, true);
        }
        catch
        {
        }
    }
}
