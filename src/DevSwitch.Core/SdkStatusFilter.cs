// 文件用途：定义 SDK 列表「状态」筛选下拉的纯逻辑枚举与匹配器，供 ViewModel 与单元测试复用。
// 创建/修改日期：2026-06-10
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：仅 BCL
// NOTE: 合法授权学习使用，仅限本地环境。本文件不做 IO，仅维护映射规则，便于在 net8.0 测试项目里直接覆盖。

namespace DevSwitch.Core;

/// <summary>
/// SDK 列表右上角「状态」筛选下拉对应的离散过滤值。
/// </summary>
public enum SdkStatusFilter
{
    /// <summary>
    /// 全部：不做过滤，所有行均显示。
    /// </summary>
    All,

    /// <summary>
    /// 使用中：仅显示当前活跃 SDK（Status="使用中"）。
    /// </summary>
    InUse,

    /// <summary>
    /// 可用：已登记且结构可执行，但当前未被激活（Status="可用"）。
    /// </summary>
    Available,

    /// <summary>
    /// 不可用：路径丢失 / 关键二进制不存在 / 命令验证失败（Status="不可用"）。
    /// </summary>
    Unavailable,
}

/// <summary>
/// SDK 状态文案与 <see cref="SdkStatusFilter"/> 的匹配规则。
/// </summary>
/// <remarks>
/// 与 <c>SdkCatalogViewService.MapStatus</c> 输出的中文文案保持一致：
/// 使用中 / 可用 / 不可用 / 未验证 / 未知。其中「未验证」「未知」不归入任何具体过滤组，
/// 仅在「全部」过滤下展示，避免把未验证状态错算成「可用」。
/// </remarks>
public static class SdkStatusFilterMatcher
{
    // 集中在此声明状态文案常量，避免 VM/UI/测试三处分别字面量化造成不同步。
    private const string InUseText = "使用中";
    private const string AvailableText = "可用";
    private const string UnavailableText = "不可用";

    /// <summary>
    /// 判断给定状态文案在指定过滤器下是否应该展示。
    /// </summary>
    /// <param name="statusText">行的状态文案，例如「使用中」「可用」「不可用」「未验证」。</param>
    /// <param name="filter">当前生效的过滤值。</param>
    /// <returns>true 表示在该过滤下应可见；false 表示应隐藏。</returns>
    public static bool Matches(string? statusText, SdkStatusFilter filter)
    {
        // All：放行所有行（含未验证、未知文案），保证默认视图等价于关闭过滤。
        if (filter == SdkStatusFilter.All)
        {
            return true;
        }

        // 空文案不归入任何具体分组：在非 All 过滤下统一隐藏，避免误匹配。
        if (string.IsNullOrEmpty(statusText))
        {
            return false;
        }

        return filter switch
        {
            SdkStatusFilter.InUse => string.Equals(statusText, InUseText, StringComparison.Ordinal),
            SdkStatusFilter.Available => string.Equals(statusText, AvailableText, StringComparison.Ordinal),
            SdkStatusFilter.Unavailable => string.Equals(statusText, UnavailableText, StringComparison.Ordinal),
            _ => true,
        };
    }

    /// <summary>
    /// 把 ComboBox 选中的中文文案（"全部" / "使用中" / "可用" / "不可用"）解析为枚举。
    /// 未知文案统一回退到 <see cref="SdkStatusFilter.All"/>，保持与 PlaceholderText 默认行为一致。
    /// </summary>
    /// <param name="text">下拉项的中文文案；可为 null。</param>
    /// <returns>解析后的过滤枚举。</returns>
    public static SdkStatusFilter ParseFromComboBoxText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return SdkStatusFilter.All;
        }

        return text.Trim() switch
        {
            "全部" => SdkStatusFilter.All,
            InUseText => SdkStatusFilter.InUse,
            AvailableText => SdkStatusFilter.Available,
            UnavailableText => SdkStatusFilter.Unavailable,
            _ => SdkStatusFilter.All,
        };
    }
}
