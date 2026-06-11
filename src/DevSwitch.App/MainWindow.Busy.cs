// =============================================================================
// 文件：MainWindow.Busy.cs
// 用途：MainWindow 的「忙碌动画覆盖层」控制逻辑。供长耗时操作（如数据迁移）
//       显示全窗口遮罩 + 加载动画 + 文案，避免用户误以为程序卡死。
// 创建日期：2026-06-11
// 语言/运行时：C# 12 / .NET 8 / WinUI 3（Windows App SDK）
// 说明：覆盖层 UI 由 MainWindow.xaml 中的 BusyOverlay 元素定义，本文件仅负责
//       显示/隐藏与文案更新的运行时控制。
// =============================================================================

using Microsoft.UI.Xaml; // Visibility 枚举

namespace DevSwitch.App;

// MainWindow 为分布在多个文件的 partial 密封类，此处仅补充忙碌覆盖层相关成员。
public sealed partial class MainWindow
{
    /// <summary>
    /// 显示忙碌覆盖层，并启动加载动画。可重复调用以更新标题与说明文案。
    /// </summary>
    /// <param name="title">覆盖层主标题（较大字号，居中显示）。</param>
    /// <param name="message">次要说明文字（灰色，居中，自动换行）。</param>
    private void ShowBusyOverlay(string title, string message)
    {
        // 防御性判空：理论上控件由 XAML 初始化，但若在控件就绪前被调用则安全返回，
        // 避免抛出 NullReferenceException 影响调用方流程。
        if (BusyOverlay is null || BusyProgressRing is null
            || BusyTitleText is null || BusyMessageText is null)
        {
            return;
        }

        // 更新文案。允许传入 null，统一回退为空字符串，避免 TextBlock 显示异常。
        BusyTitleText.Text = title ?? string.Empty;
        BusyMessageText.Text = message ?? string.Empty;

        // 启动加载动画圈。
        BusyProgressRing.IsActive = true;

        // 显示覆盖层。半透明背景天然拦截鼠标点击，阻断用户在操作进行中误触底层 UI。
        BusyOverlay.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 隐藏忙碌覆盖层并停止加载动画。操作完成或失败后调用以恢复界面交互。
    /// </summary>
    private void HideBusyOverlay()
    {
        // 防御性判空：与 ShowBusyOverlay 一致，控件未就绪时安全返回。
        if (BusyOverlay is null || BusyProgressRing is null)
        {
            return;
        }

        // 先停止动画，再隐藏覆盖层，停止 ProgressRing 的渲染循环以释放资源。
        BusyProgressRing.IsActive = false;
        BusyOverlay.Visibility = Visibility.Collapsed;
    }
}
