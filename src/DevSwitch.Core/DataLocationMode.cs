// 文件用途：定义 DevSwitch「数据位置模式」枚举与引导配置模型，
//          供用户在“便携(应用目录)/固定(C盘 LocalAppData)/自定义路径”之间显式选择。
// 创建/修改日期：2026-06-12
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：仅 BCL（无外部依赖）
// NOTE: 合法授权学习使用，仅限本地环境。

namespace DevSwitch.Core;

/// <summary>
/// 数据位置模式：决定 DevSwitch 数据根目录的归属策略。
/// </summary>
public enum DataLocationMode
{
    /// <summary>
    /// 便携模式：数据存放于应用同目录 <c>data\</c>。
    /// 移动工具目录时数据随之迁移（绿色便携）。
    /// </summary>
    Portable,

    /// <summary>
    /// 固定模式：数据固定在 <c>%LOCALAPPDATA%\DevSwitch</c>（C 盘）。
    /// 移动工具目录时数据不动，环境配置始终有效。
    /// </summary>
    LocalAppData,

    /// <summary>
    /// 自定义模式：数据存放于用户指定的绝对路径。
    /// </summary>
    Custom
}

/// <summary>
/// 引导配置模型：记录数据位置模式与（仅 Custom 模式需要的）自定义路径。
/// 持久化于 exe 同目录的引导文件，便携友好，不依赖数据根本身。
/// </summary>
/// <param name="Mode">数据位置模式。</param>
/// <param name="CustomPath">仅当 <see cref="DataLocationMode.Custom"/> 时有意义的绝对路径；其余模式为 null。</param>
public sealed record DataLocationConfig(DataLocationMode Mode, string? CustomPath)
{
    /// <summary>
    /// 默认配置：便携模式（无自定义路径）。缺少引导文件时采用此默认。
    /// </summary>
    public static DataLocationConfig Portable { get; } = new(DataLocationMode.Portable, null);
}
