using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using BiliShare.Models;
using BiliShare.Services;
using BiliShare.Views;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace BiliShare;

public sealed partial class MainWindow : Window
{
    private const string HomeUrl = "https://www.bilibili.com";

    /// <summary>允许的域名白名单（含子域名）。</summary>
    private static readonly string[] AllowedHostSuffixes =
    {
        ".bilibili.com",
        ".hdslb.com",
        ".bilivideo.com",
        ".biligc.com",
    };

    private readonly List<BrowserTab> _tabs = new();
    private readonly AppConfig _config = ConfigService.Load();
    private CoreWebView2Environment? _sharedEnvironment;
    private SettingsWindow? _settingsWindow;
    private int _tabCounter;
    private BiliFingerprint? _fingerprint;
    private BrowserTab? _hoverTab; // 当前鼠标悬浮的标签页（用于浅色高亮）

    private bool _isFullScreen;
    private bool _promptShown; // 防止白名单拦截弹框刷屏
    private bool _cookiePromptShown; // 防止在线模式"获取 Cookie"询问刷屏
    private StatusData? _lastStatus; // 最近一次 /status 结果，供 Cookie 注入判断开关

    private InputNonClientPointerSource? _ncInputSource;
    private const double TitleBarHeightDip = 34; // 标签栏高度（作为可拖拽标题栏）

    private Brush ResourceBrush(string key) => ThemeService.Brush(key, RootGrid);
    private static Style ResourceStyle(string key) => (Style)Application.Current.Resources[key];

    public MainWindow()
    {
        this.InitializeComponent();
        this.AppWindow.Title = "一个神秘的Bilibili客户端";
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "BiliShare.ico");
        if (File.Exists(iconPath))
            this.AppWindow.SetIcon(iconPath);

        SetupWindowChrome();
        SetupDragRegion();
        this.AppWindow.Changed += AppWindow_Changed;

        ThemeService.Attach(RootGrid);
        Closed += (_, _) => ThemeService.Detach(RootGrid);

        // 默认最大化启动
        if (this.AppWindow.Presenter is OverlappedPresenter p) p.Maximize();
        UpdateMaxRestoreIcon();
        UpdateDragRegion();

        NewTab(HomeUrl);
        _ = UpdateStatusAsync();
        _ = AutoRefreshOfflineCookieAsync();
    }

    // ========================== 标签管理 ==========================

    private async void NewTab(string? url = null)
    {
        var env = await EnsureSharedEnvironmentAsync();

        var webView = new WebView2
        {
            DefaultBackgroundColor = Colors.White,
        };
        await webView.EnsureCoreWebView2Async(env);
        WireWebView(webView);

        var tab = new BrowserTab
        {
            Sequence = ++_tabCounter,
            WebView = webView,
            TabButtonView = null!,
            TitleView = null!,
        };
        (tab.TabButtonView, tab.TitleView) = CreateTabButton(tab);

        WebViewHost.Children.Add(webView);
        TabStrip.Children.Add(tab.TabButtonView);
        _tabs.Add(tab);

        // 导航前注入登录态与设备指纹 Cookie，确保 B 站呈现已登录状态
        await InjectSessionCookiesAsync(webView.CoreWebView2);

        webView.CoreWebView2.Navigate(url ?? HomeUrl);
        ActivateTab(tab);
    }

    /// <summary>注入 B 站登录态 Cookie（在线取服务端 / 离线读本地）与设备指纹。</summary>
    private async Task InjectSessionCookiesAsync(CoreWebView2 core)
    {
        // 设备指纹（buvid3/buvid4 等，缓存一次保证会话内稳定；失败不影响登录）
        try
        {
            if (_fingerprint == null)
            {
                using var fpService = new FingerprintService();
                _fingerprint = await fpService.GetFingerprintAsync();
            }
            CookieInjector.Inject(core, _fingerprint!);
        }
        catch
        {
        }

        // 登录态 Cookie
        try
        {
            if (_config.Mode == AppMode.Online)
            {
                if (string.IsNullOrWhiteSpace(_config.ServerBaseUrl) || string.IsNullOrWhiteSpace(_config.PAT))
                    return;

                using var api = new ApiClient(_config.ServerBaseUrl, _config.PAT);
                // 优先复用启动时已拉取的状态；未拉取时补一次，据此判断客户端连接开关
                var status = _lastStatus ?? await api.GetStatusAsync();
                _lastStatus = status;
                if (!status.ClientEnabled)
                    return; // 未开放客户端连接：无法读取 Cookie，页面保持未登录，由 UpdateStatusAsync 提示

                var data = await api.GetClientCookieAsync();
                if (!string.IsNullOrWhiteSpace(data.Cookie))
                    CookieInjector.Inject(core, data.Cookie);
            }
            else
            {
                var cookie = LocalCookieService.Load();
                if (cookie is { IsComplete: true })
                    CookieInjector.Inject(core, cookie.ToCookieString());
            }
        }
        catch
        {
            // 连接/获取失败时不阻断浏览
        }
    }

    private async Task<CoreWebView2Environment> EnsureSharedEnvironmentAsync()
    {
        if (_sharedEnvironment != null)
            return _sharedEnvironment;

        var userDataFolder = Paths.WebView2UserDataDir;
        Directory.CreateDirectory(userDataFolder);

        _sharedEnvironment = await CoreWebView2Environment.CreateWithOptionsAsync(
            null, userDataFolder, new CoreWebView2EnvironmentOptions());
        return _sharedEnvironment;
    }

    /// <summary>构建标签栏中的滑尺项，并返回 (容器, 标题文本)。</summary>
    private (Border, TextBlock) CreateTabButton(BrowserTab tab)
    {
        var title = new TextBlock
        {
            Text = tab.Title,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 130,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 12,
            Foreground = ResourceBrush("BrandTextMutedBrush"),
        };

        // 标题用透明按钮承载点击：标签栏处于系统拖拽区域内，纯 Border 的 Tapped
        // 命中测试不稳定，改用 Button 的 Click 事件可靠切换标签页。
        var titleButton = new Button
        {
            Content = title,
            Style = ResourceStyle("BrandTabButtonStyle"),
            Padding = new Thickness(0),
        };
        titleButton.Click += (_, _) => ActivateTab(tab);

        var close = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 10 },
            Style = ResourceStyle("BrandTabCloseButtonStyle"),
            Padding = new Thickness(0, 0, 0, 0),
        };
        close.Click += (_, _) => CloseTab(tab);

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { titleButton, close },
        };

        var border = new Border
        {
            Child = content,
            Height = 26,
            MinWidth = 64,
            MaxWidth = 172,
            Padding = new Thickness(10, 0, 6, 0),
            CornerRadius = new CornerRadius(8),
            Tag = "tab",
            Background = new SolidColorBrush(Colors.Transparent),
        };
        border.Tapped += (_, _) => ActivateTab(tab);
        border.PointerEntered += (_, _) => { _hoverTab = tab; RenderTabStyles(); };
        border.PointerExited += (_, _) => { if (ReferenceEquals(_hoverTab, tab)) _hoverTab = null; RenderTabStyles(); };
        return (border, title);
    }

    private void ActivateTab(BrowserTab tab)
    {
        foreach (var t in _tabs)
        {
            bool isActive = ReferenceEquals(t, tab);
            t.IsActive = isActive;
            t.WebView.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        }
        RenderTabStyles();
        UpdateNavState();
    }

    private void RenderTabStyles()
    {
        var activeBg = ResourceBrush("BrandPinkBrush");
        var hoverBg = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        var inactiveBg = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
        var activeFg = new SolidColorBrush(Colors.White);
        var inactiveFg = ResourceBrush("BrandTextMutedBrush");

        foreach (var t in _tabs)
        {
            t.TabButtonView.Background = t.IsActive ? activeBg
                : (ReferenceEquals(t, _hoverTab) ? hoverBg : inactiveBg);
            t.TitleView.Foreground = t.IsActive ? activeFg : inactiveFg;
        }
    }

    private void CloseTab(BrowserTab tab)
    {
        bool wasActive = tab.IsActive;
        _tabs.Remove(tab);
        TabStrip.Children.Remove(tab.TabButtonView);
        WebViewHost.Children.Remove(tab.WebView);
        tab.WebView.Close();

        if (_tabs.Count == 0)
        {
            // 关闭最后一个标签时，新建空白页，防止窗口无内容
            NewTab("about:blank");
            return;
        }

        if (wasActive)
            ActivateTab(_tabs[^1]);
        else if (_tabs.FirstOrDefault(t => t.IsActive) == null)
            ActivateTab(_tabs[^1]);
    }

    // ========================== WebView 事件 ==========================

    private void WireWebView(WebView2 wv)
    {
        wv.CoreWebView2.NavigationStarting += OnNavigationStarting;
        wv.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
        // 用闭包捕获 WebView2 控件实例来定位标签（CoreWebView2 投影对象不可靠，改用控件引用匹配）
        wv.CoreWebView2.SourceChanged += (sender, args) => OnSourceChanged(wv, sender, args);
        wv.CoreWebView2.DocumentTitleChanged += (sender, args) => OnDocumentTitleChanged(wv, sender, args);
        wv.CoreWebView2.ContainsFullScreenElementChanged += (sender, args) => OnFullScreenElementChanged(wv, sender, args);
    }

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!IsAllowedUri(args.Uri))
        {
            args.Cancel = true;
            _ = ShowBlockedPromptAsync();
        }
    }

    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true; // 不弹出系统浏览器
        var url = args.Uri;
        DispatcherQueue.TryEnqueue(() => NewTab(url));
    }

    private void OnSourceChanged(WebView2 wv, CoreWebView2 sender, CoreWebView2SourceChangedEventArgs args)
    {
        var tab = _tabs.FirstOrDefault(t => ReferenceEquals(t.WebView, wv));
        if (tab == null || !tab.IsActive) return;
        UpdateNavState();
    }

    private void OnDocumentTitleChanged(WebView2 wv, CoreWebView2 sender, object? args)
    {
        var tab = _tabs.FirstOrDefault(t => ReferenceEquals(t.WebView, wv));
        if (tab == null) return;
        var title = sender.DocumentTitle;
        if (string.IsNullOrWhiteSpace(title)) return;
        tab.Title = title;
        tab.TitleView.Text = title;
    }

    private void OnFullScreenElementChanged(WebView2 wv, CoreWebView2 sender, object? args)
    {
        var tab = _tabs.FirstOrDefault(t => ReferenceEquals(t.WebView, wv));
        if (tab == null) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            bool full = sender.ContainsFullScreenElement;
            if (tab.InsideFullScreen == full) return;
            tab.InsideFullScreen = full;

            bool anyFull = _tabs.Any(t => t.InsideFullScreen);
            if (anyFull != _isFullScreen)
            {
                _isFullScreen = anyFull;
                // 全屏时隐藏标签栏与地址栏，让视频占满整个窗口
                var chrome = _isFullScreen ? Visibility.Collapsed : Visibility.Visible;
                TabBarArea.Visibility = chrome;
                ToolbarArea.Visibility = chrome;

                if (_isFullScreen)
                    this.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                else
                    this.AppWindow.SetPresenter(AppWindowPresenterKind.Default);

                UpdateDragRegion();
            }
        });
    }

    // ========================== 导航控制（作用于当前激活标签） ==========================

    private BrowserTab? GetActiveTab() => _tabs.FirstOrDefault(t => t.IsActive);

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetActiveTab();
        if (tab?.WebView.CanGoBack == true) tab.WebView.GoBack();
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetActiveTab();
        if (tab?.WebView.CanGoForward == true) tab.WebView.GoForward();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetActiveTab();
        if (tab != null) tab.WebView.Reload();
    }

    private void HomeButton_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetActiveTab();
        tab?.WebView.CoreWebView2?.Navigate(HomeUrl);
    }

    private void AddTabButton_Click(object sender, RoutedEventArgs e) => NewTab(HomeUrl);

    // ========================== 窗口控制（隐藏系统标题栏后自定义） ==========================

    /// <summary>隐藏系统标题栏（保留边框以便调整大小），三按钮由标签栏右端接管。</summary>
    private void SetupWindowChrome()
    {
        if (this.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
            presenter.IsResizable = true;
        }
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args) => UpdateMaxRestoreIcon();

    private void UpdateMaxRestoreIcon()
    {
        if (MaxRestoreIcon == null) return;
        bool maximized = this.AppWindow.Presenter is OverlappedPresenter p
                         && p.State == OverlappedPresenterState.Maximized;
        MaxRestoreIcon.Glyph = maximized ? "\uE923" : "\uE922"; // 还原 / 最大化
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (this.AppWindow.Presenter is OverlappedPresenter p) p.Minimize();
    }

    private void MaxRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (this.AppWindow.Presenter is OverlappedPresenter p)
        {
            if (p.State == OverlappedPresenterState.Maximized) p.Restore();
            else p.Maximize();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => this.Close();

    /// <summary>接入系统非客户区拖拽：整个标签栏作为标题栏拖拽手柄（按钮/标签命中由 XAML 层优先处理）。</summary>
    private void SetupDragRegion()
    {
        _ncInputSource = InputNonClientPointerSource.GetForWindowId(
            Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this)));
        RootGrid.SizeChanged += (_, _) => UpdateDragRegion();
        UpdateDragRegion();
    }

    /// <summary>按当前标签栏几何范围更新可拖拽标题栏区域（物理像素）。</summary>
    private void UpdateDragRegion()
    {
        if (_ncInputSource == null) return;

        // 全屏时无标题栏，清除拖拽区
        if (_isFullScreen)
        {
            _ncInputSource.ClearAllRegionRects();
            return;
        }

        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        var width = AppWindow.ClientSize.Width;
        var height = (int)Math.Round(TitleBarHeightDip * scale);
        _ncInputSource.SetRegionRects(NonClientRegionKind.Caption,
            new[] { new RectInt32(0, 0, Math.Max(0, width), height) });
    }

    private void UpdateNavState()
    {
        var tab = GetActiveTab();
        BackButton.IsEnabled = tab?.WebView.CanGoBack == true;
        ForwardButton.IsEnabled = tab?.WebView.CanGoForward == true;
        RefreshButton.IsEnabled = tab != null;
        AddressBar.Text = ResolveAddressText(tab?.WebView.CoreWebView2?.Source);
    }

    // ========================== 工具 / 占位 ==========================

    /// <summary>地址栏文字：空地址或空白页显示占位提示，否则显示完整 URL。</summary>
    private static string ResolveAddressText(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || string.Equals(uri, "about:blank", StringComparison.OrdinalIgnoreCase))
            return "阅读浏览 · 点触即达";
        return uri;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _settingsWindow = new SettingsWindow(_config);
        _settingsWindow.Saved += OnSettingsSaved;
        _settingsWindow.Activate();
    }

    private void OnSettingsSaved() => _ = UpdateStatusAsync();

    // ========================== 连接状态灯 ==========================

    /// <summary>根据当前配置探测连接/Cookie 状态，更新状态灯与文案。</summary>
    private async Task UpdateStatusAsync()
    {
        try
        {
            if (_config.Mode == AppMode.Online)
            {
                if (string.IsNullOrWhiteSpace(_config.ServerBaseUrl) || string.IsNullOrWhiteSpace(_config.PAT))
                {
                    SetStatus("未连接", "BrandIdleBrush");
                    return;
                }

                using var api = new ApiClient(_config.ServerBaseUrl, _config.PAT);
                var status = await api.GetStatusAsync();
                _lastStatus = status;

                // 依次校验开关状态（对应 API 规范推荐启动流程），未开启则提示并停留在未登录态
                if (!status.Enabled)
                {
                    SetStatus("已禁用", "BrandWarnBrush");
                    await ShowStatusPromptAsync("插件已全局禁用", "请联系管理员在 Halo 后台开启插件功能。");
                    return;
                }
                if (!status.UserEnabled)
                {
                    SetStatus("未启用", "BrandWarnBrush");
                    await ShowStatusPromptAsync("功能未启用", "请在个人中心开启插件功能（用户总开关）后重启客户端。");
                    return;
                }
                if (!status.ClientEnabled)
                {
                    SetStatus("未连接", "BrandWarnBrush");
                    await ShowStatusPromptAsync("未开放客户端连接", "请在个人中心开启「接受客户端连接」后重启客户端。");
                    return;
                }

                try { await api.ConnectAsync(); } catch { } // 客户端连接握手，失败不致命

                if (status.Valid)
                {
                    SetStatus("已登录", "BrandSuccessBrush");
                }
                else
                {
                    SetStatus("未登录", "BrandWarnBrush");
                    await PromptFetchCookieIfMissingAsync();
                    return;
                }
            }
            else
            {
                var cookie = LocalCookieService.Load();
                SetStatus(cookie is { IsComplete: true } ? "已登录" : "未登录",
                          cookie is { IsComplete: true } ? "BrandSuccessBrush" : "BrandIdleBrush");
            }
        }
        catch
        {
            SetStatus("连接失败", "BrandDangerBrush");
        }
    }

    private void SetStatus(string text, string brushKey)
    {
        StatusText.Text = text;
        StatusText.Foreground = ResourceBrush(brushKey);
        StatusLight.Fill = ResourceBrush(brushKey);
    }

    /// <summary>弹出在线模式的开关/状态提示（窗口未就绪时静默，状态灯文案已反映问题）。</summary>
    private async Task ShowStatusPromptAsync(string title, string content)
    {
        try
        {
            await new ContentDialog
            {
                Title = title,
                Content = content,
                CloseButtonText = "知道了",
                XamlRoot = RootGrid.XamlRoot,
            }.ShowAsync();
        }
        catch
        {
            // 忽略：窗口尚未就绪时无法弹窗，状态灯文案已提示
        }
    }

    /// <summary>离线模式每次启动时自动续期本地 Cookie（需求 8.6.3），失败保留旧值不打断启动。</summary>
    private async Task AutoRefreshOfflineCookieAsync()
    {
        if (_config.Mode != AppMode.Offline)
            return;

        var cookie = LocalCookieService.Load();
        if (cookie == null || !cookie.IsComplete || string.IsNullOrWhiteSpace(cookie.RefreshToken))
            return;

        try
        {
            using var auth = new BiliAuthService();
            var updated = await auth.RefreshAsync(cookie);
            LocalCookieService.Save(updated);
            _ = UpdateStatusAsync();
        }
        catch
        {
            // 自动刷新失败（网络/登录态失效等）时保留旧 Cookie，不打断启动
        }
    }

    /// <summary>在线模式检测到服务器无 Cookie 时，询问用户是否立即登录并上传。</summary>
    private async Task PromptFetchCookieIfMissingAsync()
    {
        if (_cookiePromptShown) return;
        _cookiePromptShown = true;
        try
        {
            var dialog = new ContentDialog
            {
                Title = "服务器暂无 Cookie",
                Content = "当前账户在服务器上还没有 Cookie，是否立即登录 B 站并自动上传？",
                PrimaryButtonText = "获取 Cookie",
                CloseButtonText = "暂不",
                XamlRoot = RootGrid.XamlRoot,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                if (await FetchAndUploadCookieAsync())
                    _ = UpdateStatusAsync(); // 上传成功后才刷新状态灯
            }
        }
        finally
        {
            _cookiePromptShown = false;
        }
    }

    /// <summary>打开登录窗口捕获 Cookie 并上传到云端，返回是否上传成功。</summary>
    private async Task<bool> FetchAndUploadCookieAsync()
    {
        var cookie = await OpenLoginWindowAsync();
        if (cookie == null || !cookie.IsComplete)
            return false;

        if (string.IsNullOrWhiteSpace(_config.ServerBaseUrl) || string.IsNullOrWhiteSpace(_config.PAT))
            return false;

        try
        {
            using var api = new ApiClient(_config.ServerBaseUrl, _config.PAT);
            await api.UploadCookieAsync(new CookieSubmitRequest
            {
                Sessdata = cookie.Sessdata,
                BiliJct = cookie.BiliJct,
                DedeUserId = cookie.DedeUserId,
                RefreshToken = cookie.RefreshToken,
                Sid = cookie.Sid,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>打开独立登录窗口，返回捕获到的 Cookie（未完成/取消则为 null）。</summary>
    private Task<LocalCookie?> OpenLoginWindowAsync()
    {
        var tcs = new TaskCompletionSource<LocalCookie?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var win = new LoginWindow();
        win.Completed += c => tcs.TrySetResult(c);
        win.Closed += (_, _) => tcs.TrySetResult(null);
        win.Activate();
        return tcs.Task;
    }

    /// <summary>域名白名单校验。</summary>
    private static bool IsAllowedUri(string uri)
    {
        if (System.Uri.TryCreate(uri, System.UriKind.Absolute, out var u))
        {
            if (string.Equals(u.Scheme, "about", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(u.Scheme, "file", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(u.Scheme, "data", StringComparison.OrdinalIgnoreCase))
                return true;

            if (u.Scheme is "http" or "https")
            {
                var host = u.Host.ToLowerInvariant();
                foreach (var suffix in AllowedHostSuffixes)
                {
                    if (host == suffix.TrimStart('.') || host.EndsWith(suffix, StringComparison.Ordinal))
                        return true;
                }
            }
        }
        return false;
    }

    private async Task ShowBlockedPromptAsync()
    {
        if (_promptShown) return;
        _promptShown = true;
        await new ContentDialog
        {
            Title = "已拦截",
            Content = "本客户端仅允许访问B站相关页面",
            CloseButtonText = "知道了",
            XamlRoot = RootGrid.XamlRoot,
        }.ShowAsync();
        _promptShown = false;
    }
}
