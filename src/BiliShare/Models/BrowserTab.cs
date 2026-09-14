using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BiliShare.Models;

/// <summary>
/// 单个浏览器标签页的数据与对应 UI 元素。
/// 所有标签共享同一个 WebView2 环境，WebView 控件叠加在 WebViewHost 网格中，仅激活页可见。
/// </summary>
public class BrowserTab
{
    public int Sequence { get; init; }

    public required WebView2 WebView { get; init; }

    /// <summary>标签栏中的滑尺项（边框容器）。</summary>
    public required Border TabButtonView { get; set; }

    /// <summary>标签栏中显示的标题文本。</summary>
    public required TextBlock TitleView { get; set; }

    public string Title { get; set; } = "新标签";

    public bool IsActive { get; set; }

    /// <summary>最近一次是否正在进行的全屏元素。</summary>
    public bool InsideFullScreen { get; set; }
}