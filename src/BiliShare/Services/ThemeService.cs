using System.Collections.Generic;
using BiliShare.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace BiliShare.Services;

/// <summary>
/// 界面主题应用服务。基于「根元素级 RequestedTheme」实现：
/// <c>System→ElementTheme.Default</c>（跟随系统）、<c>Light/Dark</c> 为固定值。
/// 运行时切换可靠，且能联动 ThemeDictionaries 与系统控件。
/// </summary>
public static class ThemeService
{
    private static readonly object Gate = new();
    private static readonly List<FrameworkElement> Roots = new();

    /// <summary>把配置枚举映射为元素级主题。</summary>
    public static ElementTheme ToElementTheme(AppTheme theme) => theme switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    /// <summary>窗口构造时注册根元素，使其参与全局主题切换。</summary>
    public static void Attach(FrameworkElement root)
    {
        if (root is null)
            return;
        lock (Gate)
        {
            if (!Roots.Contains(root))
                Roots.Add(root);
        }
        ApplyFromAny(root);
    }

    /// <summary>窗口关闭时注销根元素。</summary>
    public static void Detach(FrameworkElement root)
    {
        if (root is null)
            return;
        lock (Gate)
        {
            Roots.Remove(root);
        }
    }

    /// <summary>把当前配置主题应用到全部已注册窗口根。</summary>
    public static void Apply(AppConfig config)
    {
        var elementTheme = ToElementTheme(config.Theme);
        lock (Gate)
        {
            foreach (var root in Roots)
                root.RequestedTheme = elementTheme;
        }
    }

    /// <summary>设置页即时预览：把指定主题立即应用到全部已注册窗口根。</summary>
    public static void Preview(AppTheme theme)
    {
        var elementTheme = ToElementTheme(theme);
        lock (Gate)
        {
            foreach (var root in Roots)
                root.RequestedTheme = elementTheme;
        }
    }

    private static void ApplyFromAny(FrameworkElement root)
    {
        // 注册时按已保存配置应用一次，避免新窗口与其它窗口主题不一致。
        var config = ConfigService.Load();
        root.RequestedTheme = ToElementTheme(config.Theme);
    }

    /// <summary>
    /// 按指定根元素的实际主题，从 <see cref="ThemeDictionaries"/> 解析命名画刷。
    /// 由于画刷已移入主题字典，<c>Application.Current.Resources[key]</c> 无法直接命中；
    /// 需先确定生效主题（根元素 Light/Dark，或跟随系统的应用主题），再取其字典。
    /// </summary>
    public static Brush Brush(string key, FrameworkElement? root = null)
    {
        var theme = ElementTheme.Default;
        if (root is { RequestedTheme: not ElementTheme.Default })
            theme = root.RequestedTheme;
        else if (Application.Current != null)
            theme = Application.Current.RequestedTheme switch
            {
                ApplicationTheme.Dark => ElementTheme.Dark,
                _ => ElementTheme.Light,
            };

        var dictName = theme == ElementTheme.Dark ? "Dark" : "Light";
        if (Application.Current?.Resources.ThemeDictionaries is { } dicts
            && dicts.TryGetValue(dictName, out var dictObj)
            && dictObj is ResourceDictionary dict
            && dict.TryGetValue(key, out var val)
            && val is Brush brush)
            return brush;

        return RootFallbackBrush();
    }

    private static Brush RootFallbackBrush() =>
        new SolidColorBrush(Color.FromArgb(0xFF, 0x1C, 0x1B, 0x19));
}
