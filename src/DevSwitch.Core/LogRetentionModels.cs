// 文件用途：定义日志保留 / 轮转服务使用的结果模型与常量。
// 创建日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：仅 BCL（System）
// NOTE: 合法授权学习使用，仅限本地环境。

namespace DevSwitch.Core;

/// <summary>
/// 日志保留 / 轮转相关的默认策略常量。
/// </summary>
/// <remarks>
/// 取值依据：docs/architecture/design.md 第 13 节、docs/product/requirements.md 第 14 节。
/// </remarks>
public static class LogRetentionPolicy
{
    /// <summary>默认日志保留天数（14 天）。</summary>
    public const int DefaultRetentionDays = 14;

    /// <summary>默认单文件大小上限（20 MB），超过则触发轮转。</summary>
    public const long DefaultMaxBytes = 20L * 1024 * 1024;
}

/// <summary>
/// 日志清理结果：记录被删除与被保留的日志文件。
/// </summary>
/// <param name="DeletedFiles">本次清理被删除的日志文件完整路径集合。</param>
/// <param name="RetainedFiles">未达保留期限、被保留的日志文件完整路径集合。</param>
public sealed record LogPruneResult(
    IReadOnlyList<string> DeletedFiles,
    IReadOnlyList<string> RetainedFiles)
{
    /// <summary>空结果（无 logs 目录或无匹配文件时返回）。</summary>
    public static LogPruneResult Empty { get; } =
        new(Array.Empty<string>(), Array.Empty<string>());
}
