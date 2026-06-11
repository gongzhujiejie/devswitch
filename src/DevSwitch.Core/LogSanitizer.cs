// 文件用途：日志行专用脱敏器。复用 DiagnosticSanitizer 的脱敏正则，
//   并补充日志行专属处理（如 PATH= 超长行截断、仅保留 DevSwitch 相关段）。
// 创建日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：仅 BCL（System）
// NOTE: 合法授权学习使用，仅限本地环境。
// NOTE: 不修改 DiagnosticBundleExporter.cs；这里仅委托调用其 DiagnosticSanitizer。

namespace DevSwitch.Core;

/// <summary>
/// 日志行脱敏器（纯函数，最重点可测）。
/// </summary>
/// <remarks>
/// 职责分层：
/// - 通用敏感串（Token / Bearer / api_key / password / secret / 用户名段）复用
///   <see cref="DiagnosticSanitizer.Sanitize(string?)"/>，避免重复维护正则。
/// - 日志专属：把形如 <c>PATH=...</c> 的超长行截断，只保留含 DevSwitch 的条目，
///   不把完整环境变量 / 完整 PATH 落盘。
/// </remarks>
public static class LogSanitizer
{
    // PATH 行的分隔符：Windows 用 ';'。
    private const char PathEntrySeparator = ';';

    // 被识别为 PATH 风格变量的键（不区分大小写）。
    private static readonly string[] PathLikeKeys = { "PATH", "Path" };

    /// <summary>
    /// 对单行日志脱敏。
    /// </summary>
    /// <param name="line">原始日志行；为空时返回空字符串。</param>
    /// <returns>脱敏后的日志行。</returns>
    public static string Sanitize(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return string.Empty;
        }

        // 1) 先做 PATH= 超长行处理：仅保留 DevSwitch 相关条目，避免泄露完整环境。
        var collapsed = CollapsePathAssignments(line);

        // 2) 再复用诊断脱敏：打码 Token/Bearer/key/password/secret，隐藏用户名段。
        return DiagnosticSanitizer.Sanitize(collapsed);
    }

    /// <summary>
    /// 把行内的 <c>PATH=...</c> 赋值压缩为仅含 DevSwitch 相关条目（纯函数）。
    /// </summary>
    /// <param name="line">原始行。</param>
    /// <returns>处理后的行；不含 PATH 赋值时原样返回。</returns>
    /// <remarks>
    /// 识别规则：以 <c>PATH=</c> 或 <c>Path=</c> 开头（允许前导空白），其后到行尾视为 PATH 值。
    /// 处理：按 ';' 拆分，仅保留含 "DevSwitch" 的条目；若全部被丢弃，输出占位说明，
    /// 避免完整 PATH 出现在日志中。
    /// </remarks>
    public static string CollapsePathAssignments(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return line;
        }

        // 去掉前导空白后匹配 "PATH=" / "Path="；保留原前导空白以维持日志格式。
        var leadingWhitespaceLength = 0;
        while (leadingWhitespaceLength < line.Length && char.IsWhiteSpace(line[leadingWhitespaceLength]))
        {
            leadingWhitespaceLength++;
        }

        var leading = line[..leadingWhitespaceLength];
        var rest = line[leadingWhitespaceLength..];

        foreach (var key in PathLikeKeys)
        {
            var prefix = key + "=";
            if (rest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var value = rest[prefix.Length..];

                // 仅保留含 DevSwitch 的条目，丢弃其余用户 / 系统条目。
                var kept = value
                    .Split(PathEntrySeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(entry => entry.Contains("DevSwitch", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                var collapsedValue = kept.Length > 0
                    ? string.Join(PathEntrySeparator, kept) + ";<truncated>"
                    : "<truncated>";

                return $"{leading}{rest[..prefix.Length]}{collapsedValue}";
            }
        }

        return line;
    }
}
