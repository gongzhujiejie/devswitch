// 文件用途：定义 WinUI SDK 管理页表格行绑定模型。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：DevSwitch.Core

using DevSwitch.Core;

namespace DevSwitch.App.Models;

/// <summary>
/// SDK 版本列表行模型。
/// </summary>
/// <remarks>
/// 该模型是 GUI 绑定层 DTO，来源于真实 sdks.json 的投影，不直接代表持久化文件结构。
/// </remarks>
public sealed class SdkVersionRow
{
    /// <summary>
    /// SDK 记录稳定 ID，用于后续切换、详情或刷新定位。
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// SDK 类型，供切换服务识别 Java/Maven/Node/Go。
    /// </summary>
    public SdkType Type { get; init; } = SdkType.Unknown;

    /// <summary>
    /// SDK 所属分类，例如 Java、Maven、Node.js、Go。
    /// ViewModel 会按该字段过滤右侧版本列表。
    /// </summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>
    /// 展示名称，例如 Temurin 17、Node.js 22 LTS。
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// 展示版本号；本地导入未验证时可能是 unknown。
    /// </summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>
    /// 来源文案：托管或外部。
    /// </summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>
    /// SDK 根目录路径。
    /// </summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>
    /// 状态文案：使用中、可用、未验证、不可用。
    /// </summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>
    /// 操作文案，例如 当前、切换、验证、查看原因。
    /// </summary>
    public string Operation { get; init; } = string.Empty;

    /// <summary>
    /// 是否允许点击“切换”类操作。
    /// </summary>
    public bool CanSwitch { get; init; }
}
