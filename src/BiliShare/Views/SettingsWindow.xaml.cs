using System.Collections.Generic;
using System.Threading.Tasks;
using BiliShare.Models;
using BiliShare.Services;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace BiliShare.Views;

/// <summary>
/// 设置窗口（原生 WinUI）：模式切换、在线配置（地址/PAT/测试连接）、账号信息、Cookie 管理、清除所有数据。
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly AppConfig _config;

    private const string OfficialServerUrl = "https://www1.baaaize.site";

    /// <summary>配置已保存并关闭前触发（供主窗口后续应用新配置）。</summary>
    public event Action? Saved;

    private InputNonClientPointerSource? _ncInputSource;
    private const double TitleBarHeightDip = 42; // 顶栏高度（作为可拖拽标题栏）

    private Brush XamlBrush(string key) => BiliShare.Services.ThemeService.Brush(key, RootGrid);

    public SettingsWindow(AppConfig config)
    {
        _config = config;
        this.InitializeComponent();
        this.AppWindow.Title = "设置";

        if (this.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = true;
            presenter.IsResizable = true;
        }

        this.AppWindow.Resize(new Windows.Graphics.SizeInt32(860, 900));

        SetupDragRegion();
        ThemeService.Attach(RootGrid);
        Closed += (_, _) => ThemeService.Detach(RootGrid);
        LoadFromConfig();
        CurrentVersionText.Text = $"v{UpdateService.CurrentVersion}";
    }

    private void LoadFromConfig()
    {
        var online = _config.Mode == AppMode.Online;
        ModeOnline.IsChecked = online;
        ModeOffline.IsChecked = !online;
        PatBox.Password = _config.PAT;

        switch (_config.Theme)
        {
            case AppTheme.Light: ThemeLight.IsChecked = true; break;
            case AppTheme.Dark: ThemeDark.IsChecked = true; break;
            default: ThemeSystem.IsChecked = true; break;
        }

        var baseUrl = _config.ServerBaseUrl?.Trim();
        var isOfficial = string.IsNullOrEmpty(baseUrl) || baseUrl == OfficialServerUrl;
        ServerProviderCombo.SelectedIndex = isOfficial ? 0 : 1;
        ServerBaseUrlBox.Text = isOfficial ? string.Empty : baseUrl;
        UpdateServerUrlField();

        UpdateOnlinePanel();
    }

    private void ServerProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateServerUrlField();

    /// <summary>根据下拉框选择：官方服务隐藏地址输入框，自定义显示输入框。</summary>
    private void UpdateServerUrlField()
    {
        var isOfficial = ServerProviderCombo.SelectedIndex == 0;
        ServerBaseUrlBox.Visibility = isOfficial ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>当前实际生效的服务端地址（官方服务固定地址，否则读取输入框）。</summary>
    private string EffectiveServerBaseUrl =>
        ServerProviderCombo.SelectedIndex == 0
            ? OfficialServerUrl
            : ServerBaseUrlBox.Text.Trim();

    private void ModeRadio_Checked(object sender, RoutedEventArgs e) => UpdateOnlinePanel();

    private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
    {
        var theme = ThemeLight.IsChecked == true
            ? AppTheme.Light
            : ThemeDark.IsChecked == true ? AppTheme.Dark : AppTheme.System;
        ThemeService.Preview(theme);
    }

    private void UpdateOnlinePanel()
    {
        var online = ModeOnline.IsChecked == true;
        var vis = online ? Visibility.Visible : Visibility.Collapsed;
        OnlinePanel.Visibility = vis;
        AccountPanel.Visibility = Visibility.Visible; // 账号信息两种模式均显示
        HaloAccountLabel.Visibility = vis; // Halo 账户行仅在线模式
        AccountHaloText.Visibility = vis;
        MoreLinkPanel.Visibility = vis;
        OnlineCookieActions.Visibility = vis;
        OfflineCookieActions.Visibility = online ? Visibility.Collapsed : Visibility.Visible;
        UpdateCookieStatus();

        if (online)
            _ = LoadAccountInfoAsync();
        else
            _ = UpdateOfflineAccountInfoAsync();
    }

    private void ShowError(string message)
    {
        ErrorBannerText.Text = message;
        ErrorBanner.Visibility = Visibility.Visible;
    }

    private void ClearError()
    {
        ErrorBannerText.Text = string.Empty;
        ErrorBanner.Visibility = Visibility.Collapsed;
    }

    // ---------- 账号信息（在线模式） ----------

    /// <summary>在线模式从 <c>GET /status</c> 拉取账号信息并填充账号面板。</summary>
    private async Task LoadAccountInfoAsync()
    {
        ResetAccountPanel();
        var url = EffectiveServerBaseUrl;
        var pat = PatBox.Password.Trim();
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(pat) || ModeOnline.IsChecked != true)
            return;

        try
        {
            using var api = new ApiClient(url, pat);
            var status = await api.GetStatusAsync();
            ApplyStatusToAccountPanel(status);

            try
            {
                var user = await api.GetCurrentUserAsync();
                ApplyHaloUserToAccountPanel(user);
            }
            catch
            {
                // Halo 用户接口可能无权限/失败，用户名保持占位
            }
        }
        catch
        {
            // 连接失败时账号信息保持占位，不打断设置流程
        }
    }

    private void ResetAccountPanel()
    {
        AccountHaloText.Text = "—";
        AccountBiliText.Text = "—";
        AccountExpiryText.Text = "—";
        AccountLastRefreshText.Text = "—";
        AccountPluginVersionText.Text = "—";
    }

    private void ApplyStatusToAccountPanel(StatusData s)
    {
        // Halo 用户名由 GetCurrentUserAsync 单独获取，此处先保持占位
        AccountHaloText.Text = "—";
        AccountBiliText.Text = !string.IsNullOrWhiteSpace(s.BiliUsername)
            ? $"{s.BiliUsername}（UID：{s.BiliUid}）"
            : (s.Validated ? "已验证" : "未验证");
        AccountExpiryText.Text = s.Valid ? $"约 {s.ExpiresIn} 天" : "—";
        AccountLastRefreshText.Text = FormatIso(s.LastRefresh);
        AccountPluginVersionText.Text = !string.IsNullOrWhiteSpace(s.PluginVersion)
            ? $"v{s.PluginVersion}"
            : "—";
    }

    /// <summary>填充 Halo 用户名（优先显示名，缺失时用登录名；获取失败保持占位）。</summary>
    private void ApplyHaloUserToAccountPanel(HaloUserData user)
    {
        if (string.IsNullOrWhiteSpace(user.Username))
        {
            AccountHaloText.Text = "—";
            return;
        }
        AccountHaloText.Text = !string.IsNullOrWhiteSpace(user.DisplayName) && user.DisplayName != user.Username
            ? $"{user.DisplayName}（{user.Username}）"
            : user.Username;
    }

    private static string FormatIso(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso))
            return "—";
        return DateTimeOffset.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : "—";
    }

    /// <summary>离线模式下，从本地 Cookie 校验 B 站账号并填充账号面板（用户名/UID、有效期估算、保存时间）。</summary>
    private async Task UpdateOfflineAccountInfoAsync()
    {
        AccountHaloText.Text = "—";
        var local = LocalCookieService.Load();
        if (local == null)
        {
            AccountBiliText.Text = "—";
            AccountExpiryText.Text = "—";
            AccountLastRefreshText.Text = "—";
            return;
        }

        AccountLastRefreshText.Text = local.SavedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        // B 站 SESSDATA 标准有效期约 30 天，按保存时间估算剩余天数
        var remainingDays = 30 - (int)(DateTimeOffset.UtcNow - local.SavedAt).TotalDays;
        AccountExpiryText.Text = remainingDays > 0 ? $"约 {remainingDays} 天" : "已过期";

        if (!local.IsComplete)
        {
            AccountBiliText.Text = "Cookie 不完整";
            return;
        }

        AccountBiliText.Text = "验证中…";
        try
        {
            using var auth = new BiliAuthService();
            var r = await auth.ValidateAsync(local);
            AccountBiliText.Text = r.Valid && !string.IsNullOrWhiteSpace(r.Username)
                ? $"{r.Username}（UID：{r.Uid}）"
                : (r.Valid ? "已验证" : (string.IsNullOrWhiteSpace(r.Message) ? "未验证" : r.Message));
        }
        catch
        {
            AccountBiliText.Text = "验证失败（网络异常）";
        }
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        var url = EffectiveServerBaseUrl;
        var pat = PatBox.Password.Trim();

        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(pat))
        {
            ShowError("请先填写服务端地址与 PAT");
            TestResult.Text = string.Empty;
            return;
        }

        TestButton.IsEnabled = false;
        ClearError();
        TestResult.Foreground = XamlBrush("BrandTextMutedBrush");
        TestResult.Text = "正在测试…";

        try
        {
            using var api = new ApiClient(url, pat);
            var status = await api.GetStatusAsync();
            ClearError();
            TestResult.Text = $"连接成功：{status.Message}";
            TestResult.Foreground = XamlBrush(status.Valid ? "BrandSuccessBrush" : "BrandWarnBrush");
            ApplyStatusToAccountPanel(status);

            // 在线服务虽连通，但客户端连接开关未开启，弹窗提醒
            if (!status.ClientEnabled)
            {
                var dialog = new ContentDialog
                {
                    Title = "客户端连接未开启",
                    Content = "在线服务已连接，但「客户端连接」开关当前关闭，客户端将无法从服务端获取 Cookie。\n请在插件设置中开启后重试。",
                    CloseButtonText = "我知道了",
                    XamlRoot = RootGrid.XamlRoot,
                };
                await dialog.ShowAsync();
            }

            try
            {
                var user = await api.GetCurrentUserAsync();
                ApplyHaloUserToAccountPanel(user);
            }
            catch
            {
                // Halo 用户名获取失败时保持占位
            }
        }
        catch (ApiException ex)
        {
            ShowError(ex.UserHint);
            TestResult.Text = string.Empty;
        }
        catch
        {
            ShowError("连接失败：请检查地址与网络");
            TestResult.Text = string.Empty;
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _config.Mode = ModeOnline.IsChecked == true ? AppMode.Online : AppMode.Offline;
        _config.ServerBaseUrl = EffectiveServerBaseUrl;
        _config.PAT = PatBox.Password.Trim();
        _config.Theme = ThemeLight.IsChecked == true
            ? AppTheme.Light
            : ThemeDark.IsChecked == true ? AppTheme.Dark : AppTheme.System;
        ConfigService.Save(_config);

        Saved?.Invoke();
        this.Close();
    }

    // ---------- 关于与更新 ----------

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        ClearError();
        UpdateStatusText.Foreground = XamlBrush("BrandTextMutedBrush");
        UpdateStatusText.Text = "正在检查更新…";

        try
        {
            var info = await UpdateService.CheckAsync();
            if (!info.IsNewer)
            {
                UpdateStatusText.Foreground = XamlBrush("BrandSuccessBrush");
                UpdateStatusText.Text = $"已是最新版本（v{info.Version}）";
                return;
            }

            UpdateStatusText.Text = $"发现新版本 v{info.Version}";
            var confirm = new ContentDialog
            {
                Title = "发现新版本",
                Content = $"当前版本 v{UpdateService.CurrentVersion}，最新版本 v{info.Version}，是否立即更新？",
                PrimaryButtonText = "立即更新",
                CloseButtonText = "稍后",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            {
                UpdateStatusText.Text = $"已发现新版本 v{info.Version}，可稍后更新";
                return;
            }

            var progress = new Progress<string>(msg => UpdateStatusText.Text = msg);
            await UpdateService.ApplyAsync(info, progress);
        }
        catch
        {
            UpdateStatusText.Text = "检查失败：网络异常或发布尚未就绪";
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    // ---------- 账号信息：超链接（在线） ----------

    private void RepoLink_Click(Hyperlink sender, HyperlinkClickEventArgs args)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/baize520mc/BiliShare")
        {
            UseShellExecute = true,
        });
    }

    private void MoreLink_Click(Hyperlink sender, HyperlinkClickEventArgs args)
    {
        var baseUrl = EffectiveServerBaseUrl.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"{baseUrl}/uc")
            {
                UseShellExecute = true,
            });
        }
        catch
        {
        }
    }

    // ---------- Cookie 管理 ----------

    private void UpdateCookieStatus()
    {
        if (ModeOnline.IsChecked == true)
        {
            CookieStatusLight.Fill = XamlBrush("BrandIdleBrush");
            CookieStatusText.Text = "在线模式 · Cookie 由服务端管理";
            return;
        }

        var local = LocalCookieService.Load();
        if (local == null)
        {
            CookieStatusLight.Fill = XamlBrush("BrandTextMutedBrush");
            CookieStatusText.Text = "尚未获取 Cookie";
        }
        else if (!local.IsComplete)
        {
            CookieStatusLight.Fill = XamlBrush("BrandWarnBrush");
            CookieStatusText.Text = "本地 Cookie 不完整，请重新获取";
        }
        else
        {
            CookieStatusLight.Fill = XamlBrush("BrandSuccessBrush");
            CookieStatusText.Text = $"已保存本地 Cookie（{local.SavedAt:yyyy-MM-dd}）";
        }
    }

    /// <summary>离线模式「获取 Cookie」：登录窗口捕获后直接加密保存到本地。</summary>
    private async void FetchCookieButton_Click(object sender, RoutedEventArgs e)
    {
        var cookie = await OpenLoginWindowAsync();
        if (cookie == null || !cookie.IsComplete)
        {
            UpdateCookieStatus();
            return;
        }

        LocalCookieService.Save(cookie);
        ClearError();
        UpdateCookieStatus();
        _ = UpdateOfflineAccountInfoAsync();
    }

    /// <summary>在线模式「重新获取并上传覆盖」：登录窗口捕获后确认并覆盖上传。</summary>
    private async void RefetchUploadButton_Click(object sender, RoutedEventArgs e)
    {
        var cookie = await OpenLoginWindowAsync();
        if (cookie == null || !cookie.IsComplete)
        {
            UpdateCookieStatus();
            return;
        }

        var confirm = new ContentDialog
        {
            Title = "已捕获 Cookie",
            Content = "获取成功，将自动上传并覆盖服务器上原有的 Cookie，是否继续？",
            PrimaryButtonText = "上传并覆盖",
            CloseButtonText = "取消",
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await confirm.ShowAsync() == ContentDialogResult.Primary)
            await UploadCookieToCloudAsync(cookie);

        UpdateCookieStatus();
    }

    /// <summary>在线模式「刷新当前 Cookie」：调用 <c>POST /cookie/refresh</c>。</summary>
    private async void RefreshCookieButton_Click(object sender, RoutedEventArgs e)
    {
        var url = EffectiveServerBaseUrl;
        var pat = PatBox.Password.Trim();
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(pat))
        {
            ShowError("请先填写服务端地址与 PAT");
            return;
        }

        RefreshCookieButton.IsEnabled = false;
        ClearError();
        try
        {
            using var api = new ApiClient(url, pat);
            await api.RefreshCookieAsync();

            var ok = new ContentDialog
            {
                Title = "刷新成功",
                Content = "Cookie 已刷新。",
                CloseButtonText = "好",
                XamlRoot = RootGrid.XamlRoot,
            };
            await ok.ShowAsync();
        }
        catch (ApiException ex)
        {
            ShowError($"刷新失败：{ex.UserHint}");
        }
        catch
        {
            ShowError("刷新失败：请检查地址与网络");
        }
        finally
        {
            RefreshCookieButton.IsEnabled = true;
        }
    }

    /// <summary>在线模式「验证 Cookie 是否有效」：调用 <c>POST /cookie/validate</c>。</summary>
    private async void ValidateCookieButton_Click(object sender, RoutedEventArgs e)
    {
        var url = EffectiveServerBaseUrl;
        var pat = PatBox.Password.Trim();
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(pat))
        {
            ShowError("请先填写服务端地址与 PAT");
            return;
        }

        ValidateCookieButton.IsEnabled = false;
        ClearError();
        try
        {
            using var api = new ApiClient(url, pat);
            var result = await api.ValidateCookieAsync();
            await new ContentDialog
            {
                Title = result.Valid ? "Cookie 有效" : "Cookie 无效",
                Content = result.Valid
                    ? $"B 站账号：{result.BiliUsername}（UID：{result.BiliUid}）"
                    : result.Message,
                CloseButtonText = "好",
                XamlRoot = RootGrid.XamlRoot,
            }.ShowAsync();
        }
        catch (ApiException ex)
        {
            ShowError($"验证失败：{ex.UserHint}");
        }
        catch
        {
            ShowError("验证失败：请检查地址与网络");
        }
        finally
        {
            ValidateCookieButton.IsEnabled = true;
        }
    }

    /// <summary>离线模式「刷新 Cookie」：本地执行 B 站 6 步续期协议并覆盖保存。</summary>
    private async void OfflineRefreshCookieButton_Click(object sender, RoutedEventArgs e)
    {
        var cookie = LocalCookieService.Load();
        if (cookie == null || !cookie.IsComplete)
        {
            ShowError("请先获取本地 Cookie");
            return;
        }

        OfflineRefreshCookieButton.IsEnabled = false;
        ClearError();
        try
        {
            using var auth = new BiliAuthService();
            var updated = await auth.RefreshAsync(cookie);
            LocalCookieService.Save(updated);
            UpdateCookieStatus();
            _ = UpdateOfflineAccountInfoAsync();

            await new ContentDialog
            {
                Title = "刷新成功",
                Content = "本地 Cookie 已自动续期。",
                CloseButtonText = "好",
                XamlRoot = RootGrid.XamlRoot,
            }.ShowAsync();
        }
        catch (BiliAuthException ex)
        {
            ShowError($"刷新失败：{ex.Message}");
        }
        catch
        {
            ShowError("刷新失败：网络异常，请稍后重试");
        }
        finally
        {
            OfflineRefreshCookieButton.IsEnabled = true;
        }
    }

    private async Task UploadCookieToCloudAsync(LocalCookie cookie)
    {
        var url = EffectiveServerBaseUrl;
        var pat = PatBox.Password.Trim();
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(pat))
        {
            ShowError("上传云端需要先填写服务端地址与 PAT");
            return;
        }

        try
        {
            using var api = new ApiClient(url, pat);
            await api.UploadCookieAsync(new CookieSubmitRequest
            {
                Sessdata = cookie.Sessdata,
                BiliJct = cookie.BiliJct,
                DedeUserId = cookie.DedeUserId,
                RefreshToken = cookie.RefreshToken,
                Sid = cookie.Sid,
            });
            ClearError();

            var ok = new ContentDialog
            {
                Title = "上传成功",
                Content = "Cookie 已上传到云端。",
                CloseButtonText = "好",
                XamlRoot = RootGrid.XamlRoot,
            };
            await ok.ShowAsync();
        }
        catch (ApiException ex)
        {
            ShowError($"上传失败：{ex.UserHint}");
        }
        catch
        {
            ShowError("上传失败：请检查地址与网络");
        }
    }

    private async void ImportCookieButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".bilicookie");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var file = await picker.PickSingleFileAsync();
        if (file == null)
            return;

        var cookie = LocalCookieService.Import(file.Path);
        if (cookie == null)
        {
            ShowError("导入失败：文件无效或与本机密钥不匹配");
            return;
        }

        LocalCookieService.Save(cookie);
        ClearError();
        UpdateCookieStatus();
        _ = UpdateOfflineAccountInfoAsync();

        // 导入后校验 Cookie 有效性（需求 8.6.4，本地 nav 接口判断）
        string resultText;
        try
        {
            using var auth = new BiliAuthService();
            var r = await auth.ValidateAsync(cookie);
            resultText = r.Valid
                ? $"导入成功，Cookie 有效（{r.Username}）"
                : $"导入成功，但 Cookie 无效：{r.Message}";
        }
        catch
        {
            resultText = "导入成功，有效性校验失败（网络异常）";
        }

        await new ContentDialog
        {
            Title = "导入结果",
            Content = resultText,
            CloseButtonText = "好",
            XamlRoot = RootGrid.XamlRoot,
        }.ShowAsync();
    }

    private async void ExportCookieButton_Click(object sender, RoutedEventArgs e)
    {
        LocalCookie? cookie;

        if (ModeOnline.IsChecked == true)
        {
            // 在线模式：从服务端拉取 Cookie 后导出
            var url = EffectiveServerBaseUrl;
            var pat = PatBox.Password.Trim();
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(pat))
            {
                ShowError("导出需要先填写服务端地址与 PAT");
                return;
            }

            try
            {
                using var api = new ApiClient(url, pat);
                var data = await api.GetClientCookieAsync();
                if (string.IsNullOrWhiteSpace(data.Cookie))
                {
                    ShowError("服务端暂无 Cookie 可导出");
                    return;
                }
                cookie = LocalCookie.FromCookieString(data.Cookie);
            }
            catch (ApiException ex)
            {
                ShowError($"导出失败：{ex.UserHint}");
                return;
            }
            catch
            {
                ShowError("导出失败：请检查地址与网络");
                return;
            }
        }
        else
        {
            cookie = LocalCookieService.Load();
            if (cookie == null || !cookie.IsComplete)
            {
                ShowError("暂无本地 Cookie 可导出");
                return;
            }
        }

        var picker = new FileSavePicker();
        picker.FileTypeChoices.Add("BiliCookie", new List<string> { ".bilicookie" });
        picker.SuggestedFileName = $"bilicookie_{DateTime.Now:yyyyMMdd}";
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var file = await picker.PickSaveFileAsync();
        if (file == null)
            return;

        LocalCookieService.Export(file.Path, cookie);
        ClearError();
    }

    // ---------- 清除所有数据 ----------

    private async void ClearAllDataButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            Title = "清除所有数据",
            Content = "将删除本地配置、Cookie、日志与 WebView2 数据并重启应用，恢复到首次引导状态。此操作不可撤销，是否继续？",
            PrimaryButtonText = "清除并重启",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            return;

        DataCleaner.ResetAndRestart();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => this.Close();

    // ---------- 登录窗口 ----------

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

    /// <summary>接入系统非客户区拖拽：顶栏作为标题栏拖拽手柄。</summary>
    private void SetupDragRegion()
    {
        _ncInputSource = InputNonClientPointerSource.GetForWindowId(
            Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this)));
        RootGrid.SizeChanged += (_, _) => UpdateDragRegion();
        UpdateDragRegion();
    }

    /// <summary>按顶栏几何范围更新可拖拽标题栏区域（物理像素）。</summary>
    private void UpdateDragRegion()
    {
        if (_ncInputSource == null) return;

        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        var width = AppWindow.ClientSize.Width;
        var height = (int)Math.Round(TitleBarHeightDip * scale);
        _ncInputSource.SetRegionRects(NonClientRegionKind.Caption,
            new[] { new RectInt32(0, 0, Math.Max(0, width), height) });
    }
}
