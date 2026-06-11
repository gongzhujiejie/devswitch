// =============================================================================
// 文件: MainWindow.Dialogs.cs
// 用途: 为 DevSwitch 主窗口提供「三选一对话框」助手。
//       数据位置迁移确认场景需要区分三种用户意图：
//         · 迁移并切换 (Primary)
//         · 仅切换     (Secondary)
//         · 取消退出   (Cancel)
//       原有的两按钮对话框无法表达「取消」，本助手补齐第三种结果。
// 日期: 2026-06-11
// 语言版本要求: C# 12 / .NET 8 (WinUI 3)
// 依赖: Microsoft.UI.Xaml / Microsoft.UI.Xaml.Controls 提供的 ContentDialog。
// 说明: 本文件为 MainWindow 的 partial 拆分，仅新增对话框逻辑，不改动既有成员。
// =============================================================================

using System.Threading.Tasks;          // Task<T> 异步返回类型
using Microsoft.UI.Xaml;               // TextWrapping、Window 等核心类型
using Microsoft.UI.Xaml.Controls;      // ContentDialog、TextBlock、ContentDialogButton

namespace DevSwitch.App;

/// <summary>
/// MainWindow 的对话框助手分部。集中存放多按钮对话框相关逻辑。
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>
    /// 三按钮对话框的用户选择结果。
    /// 用于在「迁移并切换 / 仅切换 / 取消」这类三选一场景下表达用户意图。
    /// </summary>
    private enum ThreeChoiceResult
    {
        /// <summary>用户点了主操作（如「迁移并切换」）。</summary>
        Primary,

        /// <summary>用户点了次操作（如「仅切换」）。</summary>
        Secondary,

        /// <summary>用户点了取消、关闭对话框（Esc / 点 X / 点取消）。</summary>
        Cancel,
    }

    /// <summary>
    /// 显示三按钮对话框：主操作 / 次操作 / 取消，并返回用户选择。
    /// </summary>
    /// <param name="title">对话框标题。</param>
    /// <param name="content">正文内容，自动换行显示。</param>
    /// <param name="primaryText">主操作按钮文案（如「迁移并切换」）。</param>
    /// <param name="secondaryText">次操作按钮文案（如「仅切换」）。</param>
    /// <param name="closeText">取消按钮文案（如「取消」）；用户按 Esc 或关闭也视为 Cancel。</param>
    /// <returns>
    /// 用户的三选一结果：
    /// <see cref="ThreeChoiceResult.Primary"/>、
    /// <see cref="ThreeChoiceResult.Secondary"/> 或
    /// <see cref="ThreeChoiceResult.Cancel"/>。
    /// </returns>
    private async Task<ThreeChoiceResult> ShowThreeChoiceDialogAsync(
        string title, string content, string primaryText, string secondaryText, string closeText)
    {
        // 防御性检查：XamlRoot 为空说明窗口尚未完成可视化树构建，
        // 此时无法承载 ContentDialog，按「取消」语义安全返回，避免抛异常。
        if (RootGrid.XamlRoot is null)
        {
            return ThreeChoiceResult.Cancel;
        }

        // 构造三按钮对话框。默认按钮设为 Close，保证回车/默认焦点落在取消上，
        // 防止用户误触执行破坏性的主操作。
        var dialog = new ContentDialog
        {
            Title = title,
            // 正文用 TextWrapping.Wrap，长文本自动换行，避免对话框被撑宽。
            Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primaryText,
            SecondaryButtonText = secondaryText,
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot,
        };

        // 弹出对话框并等待用户操作完成。
        var r = await dialog.ShowAsync();

        // 将 WinUI 的 ContentDialogResult 映射为本助手的三选一语义。
        // None（含 Esc、点 X、点取消按钮）统一归为 Cancel。
        return r switch
        {
            ContentDialogResult.Primary => ThreeChoiceResult.Primary,
            ContentDialogResult.Secondary => ThreeChoiceResult.Secondary,
            _ => ThreeChoiceResult.Cancel,
        };
    }
}
