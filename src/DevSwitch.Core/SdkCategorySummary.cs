// 文件用途：定义「SDK 分类汇总统计」的纯逻辑数据模型。
//          供 SDK 管理总览页按四大分类（Java / Maven / Node.js / Go）展示
//          每类 SDK 的数量、活跃版本与 UI 友好摘要。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：仅 BCL（System），不依赖 App 层（WinUI）类型，保证 Core 可单测。
// NOTE: 合法授权学习使用，仅限本地环境。本类型为纯数据/纯逻辑，无副作用。

namespace DevSwitch.Core;

/// <summary>
/// 汇总计算的「轻量输入行」。
/// 之所以不直接引用 App 层的 <c>SdkVersionRow</c>，是因为单元测试项目只引用 DevSwitch.Core，
/// 不引用 WinUI 的 DevSwitch.App；用最小三字段结构解耦，既可单测又便于 App 层投影。
/// </summary>
/// <param name="Category">分类名（"Java" / "Maven" / "Node.js" / "Go"）。</param>
/// <param name="Name">版本显示名（例如 "Temurin 21"）。</param>
/// <param name="Status">中文状态（"使用中" / "可用" / "未验证" / "不可用"）。活跃判定以 "使用中" 为准。</param>
public sealed record SdkSummaryInput(string Category, string Name, string Status);

/// <summary>
/// 单个 SDK 分类的汇总结果：分类名、图标 glyph、总数、活跃版本名、是否有活跃项及 UI 摘要行。
/// </summary>
public sealed class SdkCategorySummary
{
    /// <summary>分类名："Java" / "Maven" / "Node.js" / "Go"。</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>Segoe MDL2 Assets 图标 glyph（如 "\uE8B7"），供总览页卡片显示。</summary>
    public string IconGlyph { get; init; } = string.Empty;

    /// <summary>该分类下的版本总数（含所有状态）。</summary>
    public int TotalCount { get; init; }

    /// <summary>活跃（"使用中"）版本的显示名；若无活跃项则为 "未设置"。</summary>
    public string ActiveName { get; init; } = string.Empty;

    /// <summary>该分类是否存在 "使用中" 的项。</summary>
    public bool HasActive { get; init; }

    /// <summary>UI 友好摘要行，例如 "共 3 个版本 · 使用中 Temurin 21"。</summary>
    public string SummaryLine { get; init; } = string.Empty;
}
