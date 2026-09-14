using BiliShare.Services;
using BiliShare.Views;
using Microsoft.UI.Xaml;

namespace BiliShare;

public partial class App : Application
{
    private Window? _window;
    private SetupWindow? _setupWindow;

    public App()
    {
        InitializeComponent();
        WireCrashLogger();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        PerformPendingCleanup();

        var config = ConfigService.Load();

        if (config.IsInitialized)
        {
            _window = new MainWindow();
            _window.Activate();
        }
        else
        {
            _setupWindow = new SetupWindow(config);
            _setupWindow.Completed += () =>
            {
                _setupWindow = null;
                _window = new MainWindow();
                _window.Activate();
            };
            _setupWindow.Activate();
        }
    }

    /// <summary>「清除所有数据」后：在新实例启动早期清理运行期被锁定的 WebView2 用户数据目录。</summary>
    private static void PerformPendingCleanup()
    {
        if (!File.Exists(Paths.CleanupMarkerFile))
            return;

        try
        {
            // 旧进程刚退出，短暂重试以等待 WebView2 子进程释放文件锁
            for (var i = 0; i < 10; i++)
            {
                try
                {
                    if (Directory.Exists(Paths.WebView2UserDataDir))
                        Directory.Delete(Paths.WebView2UserDataDir, true);
                    break;
                }
                catch
                {
                    System.Threading.Thread.Sleep(200);
                }
            }
            File.Delete(Paths.CleanupMarkerFile);
        }
        catch
        {
        }
    }

    /// <summary>全局异常记录：写入软件根目录 data/logs/crash.log（便于诊断启动/运行时崩溃，不写入系统目录）。</summary>
    private void WireCrashLogger()
    {
        UnhandledException += (_, e) => LogCrash($"Application.UnhandledException\n{e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash($"AppDomain.UnhandledException msg={e.ExceptionObject}");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash($"UnobservedTaskException\n{e.Exception}");
            e.SetObserved();
        };
    }

    private static void LogCrash(string message)
    {
        try
        {
            Paths.EnsureDirectories();
            File.AppendAllText(
                Paths.CrashLogFile,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\r\n\r\n");
        }
        catch { }
    }
}