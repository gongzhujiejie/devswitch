// 文件用途：定义 SDK 分类汇总构建器（纯逻辑），按固定支持分类聚合输入行，产出总览页卡片数据。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：仅 BCL（System、System.Collections.Generic、System.Linq）。
// NOTE: 合法授权学习使用，仅限本地环境。无 IO、无副作用，便于 TDD 单测。

using System.Collections.Generic;
using System.Linq;

namespace DevSwitch.Core;

/// <summary>
/// SDK 分类汇总构建器：把扁平的版本行（<see cref="SdkSummaryInput"/>）聚合成
/// 固定支持分类（Java / Maven / Node.js / Go / Rust）的总览卡片数据。
/// </summary>
public static class SdkCategorySummaryBuilder
{
    /// <summary>
    /// 活跃状态判定基准：状态等于该值即视为「使用中」。
    /// NOTE: 抽成常量，避免散落的魔法字符串，便于与 App 层状态文案保持一致。
    /// </summary>
    private const string ActiveStatus = "使用中";

    /// <summary>无活跃项时 ActiveName 的占位文案。</summary>
    private const string NoActiveName = "未设置";

    /// <summary>
    /// 固定分类顺序：始终按此顺序产出所有支持分类，即使某类 0 个也要占位一条。
    /// NOTE: 顺序与总览页卡片排列一致，集中定义避免多处硬编码。
    /// </summary>
    private static readonly IReadOnlyList<string> CategoryOrder = new[] { "Java", "Maven", "Node.js", "Go", "Rust" };

    /// <summary>
    /// 各分类对应的 Segoe MDL2 Assets 图标 glyph。
    /// NOTE: 当前分类统一用 "\uE8B7" 占位，后续可按需替换为各自专属 glyph。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> CategoryGlyphs = new Dictionary<string, string>
    {
        ["Java"] = "\uE8B7",
        ["Maven"] = "\uE8B7",
        ["Node.js"] = "\uE8B7",
        ["Go"] = "\uE8B7",
        ["Rust"] = "\uE8B7",
    };

    /// <summary>
    /// 按固定支持分类聚合输入行，输出每类的汇总。
    /// </summary>
    /// <param name="rows">全部版本行；为 null 时按空集合处理。</param>
    /// <returns>始终包含所有支持分类，顺序固定 Java / Maven / Node.js / Go / Rust。</returns>
    public static IReadOnlyList<SdkCategorySummary> Build(IReadOnlyList<SdkSummaryInput> rows)
    {
        // 容错：null 输入视为空集合，保证调用方无需做空判断。
        var safeRows = rows ?? System.Array.Empty<SdkSummaryInput>();

        var result = new List<SdkCategorySummary>(CategoryOrder.Count);

        // 始终按固定顺序遍历支持分类，确保即使某类没有任何行也会产出一条占位汇总。
        foreach (var category in CategoryOrder)
        {
            // 仅筛选属于当前分类的行，避免跨类串台（例如 Java 行不计入 Maven）。
            var categoryRows = safeRows
                .Where(r => r is not null && r.Category == category)
                .ToList();

            var totalCount = categoryRows.Count;

            // 取第一条「使用中」的行作为活跃项；可能不存在。
            var activeRow = categoryRows.FirstOrDefault(r => r.Status == ActiveStatus);
            var hasActive = activeRow is not null;

            // 有活跃项则用其 Name，否则用占位文案 "未设置"。
            var activeName = hasActive ? activeRow!.Name : NoActiveName;

            // 依据三种情形生成 UI 友好摘要行。
            var summaryLine = BuildSummaryLine(totalCount, hasActive, activeName);

            result.Add(new SdkCategorySummary
            {
                Category = category,
                // 字典缺失时回退到统一占位 glyph，保证字段非空。
                IconGlyph = CategoryGlyphs.TryGetValue(category, out var glyph) ? glyph : "\uE8B7",
                TotalCount = totalCount,
                ActiveName = activeName,
                HasActive = hasActive,
                SummaryLine = summaryLine,
            });
        }

        return result;
    }

    /// <summary>
    /// 生成 UI 摘要行，三种情形：
    /// 1) 有活跃项：       "共 {n} 个版本 · 使用中 {activeName}"
    /// 2) 无活跃但 n&gt;0： "共 {n} 个版本 · 未设置使用版本"
    /// 3) n==0：            "尚未导入"
    /// </summary>
    private static string BuildSummaryLine(int totalCount, bool hasActive, string activeName)
    {
        if (totalCount == 0)
        {
            // 该类完全没有导入任何版本。
            return "尚未导入";
        }

        if (hasActive)
        {
            // 有版本且已设置使用中的版本，提示其名称。
            return $"共 {totalCount} 个版本 · 使用中 {activeName}";
        }

        // 有版本但未指定使用中版本，提示用户去设置。
        return $"共 {totalCount} 个版本 · 未设置使用版本";
    }
}
