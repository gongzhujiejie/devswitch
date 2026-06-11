// 文件用途：日志保留与单文件大小轮转服务。
//   - 按文件名内嵌日期（而非文件系统时间）判定日志是否过期，便于纯单测。
//   - 提供单文件大小轮转判定与归档执行。
// 创建日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：仅 BCL（System.IO、System.Globalization）
// NOTE: 合法授权学习使用，仅限本地环境。本服务只在传入的数据根目录范围内操作文件。

using System.Globalization;

namespace DevSwitch.Core;

/// <summary>
/// 日志保留与轮转服务（独立服务，由调用方组合到日志写入流程中）。
/// </summary>
/// <remarks>
/// 设计目标：把"日期解析、过期判定、轮转判定"全部做成纯函数，最优先单测；
/// 真正的文件删除 / 改名为异步 IO 方法。
/// 日志文件名约定：<c>&lt;channel&gt;-yyyyMMdd.log</c>（见 DevSwitchLogger）。
/// </remarks>
public sealed class LogRetentionService
{
    // 日志文件名内嵌日期的格式：8 位数字 yyyyMMdd。
    private const string DateFormat = "yyyyMMdd";

    // logs 子目录名，与 DevSwitchLogger 保持一致。
    private const string LogsDirectoryName = "logs";

    // 仅扫描 .log 文件，缩小匹配面，避免误删非日志文件。
    private const string LogSearchPattern = "*.log";

    /// <summary>
    /// 从日志文件名解析其内嵌日期（纯函数，最优先可测）。
    /// </summary>
    /// <param name="fileName">日志文件名或完整路径，例如 <c>app-20260609.log</c>。</param>
    /// <param name="date">解析得到的日期（仅日期部分有效）。</param>
    /// <returns>文件名符合 <c>&lt;channel&gt;-yyyyMMdd.log</c> 约定并成功解析返回 true，否则 false。</returns>
    public static bool TryParseLogDate(string? fileName, out DateOnly date)
    {
        date = default;

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        // 只取文件名部分，允许调用方传入完整路径。
        var name = Path.GetFileName(fileName);

        // 必须以 .log 结尾，且去掉扩展名后仍有内容。
        if (!name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stem = name[..^4]; // 去掉 ".log"

        // 取最后一个 '-' 之后的部分作为日期段：channel 名本身可能含 '-'，
        // 但日期固定在最后一段，因此用 LastIndexOf 更稳健。
        var dashIndex = stem.LastIndexOf('-');
        if (dashIndex <= 0 || dashIndex == stem.Length - 1)
        {
            // 没有 '-'，或 '-' 在首位（无 channel 名），或 '-' 在末尾（无日期段）。
            return false;
        }

        var datePart = stem[(dashIndex + 1)..];

        // 严格 8 位数字，避免把无关后缀误判为日期。
        if (datePart.Length != 8)
        {
            return false;
        }

        // 使用不变文化与精确格式解析，避免区域设置干扰。
        if (!DateTime.TryParseExact(
                datePart,
                DateFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return false;
        }

        date = DateOnly.FromDateTime(parsed);
        return true;
    }

    /// <summary>
    /// 判定某个日志日期是否已过保留期限（纯函数，最优先可测）。
    /// </summary>
    /// <param name="logDate">日志文件内嵌日期。</param>
    /// <param name="today">参照"今天"的日期（注入便于测试）。</param>
    /// <param name="retentionDays">保留天数；不大于 0 时按默认 14 天处理。</param>
    /// <returns>日志日期早于 (today - retentionDays) 返回 true（应删除）。</returns>
    public static bool IsExpired(DateOnly logDate, DateOnly today, int retentionDays = LogRetentionPolicy.DefaultRetentionDays)
    {
        if (retentionDays <= 0)
        {
            retentionDays = LogRetentionPolicy.DefaultRetentionDays;
        }

        // 保留窗口的最早允许日期：今天往前推 (retentionDays - 1) 天，
        // 这样"今天"算第 1 天，恰好保留最近 retentionDays 天。
        // 早于该日期的日志视为过期。
        var earliestAllowed = today.AddDays(-(retentionDays - 1));
        return logDate < earliestAllowed;
    }

    /// <summary>
    /// 清理 <paramref name="dataRoot"/>\logs 下超过保留天数的日志文件。
    /// </summary>
    /// <param name="dataRoot">DevSwitch 数据根目录。</param>
    /// <param name="retentionDays">保留天数，默认 14。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>包含被删除与被保留文件列表的结果。</returns>
    /// <exception cref="ArgumentException">数据根目录为空时抛出。</exception>
    public Task<LogPruneResult> PruneAsync(
        string dataRoot,
        int retentionDays = LogRetentionPolicy.DefaultRetentionDays,
        CancellationToken cancellationToken = default)
    {
        // 以本地"今天"为参照；过期判定本身是纯函数，方便单独测。
        return PruneAsync(dataRoot, DateOnly.FromDateTime(DateTime.Now), retentionDays, cancellationToken);
    }

    /// <summary>
    /// 清理实现：允许注入参照日期，便于在测试中固定"今天"。
    /// </summary>
    /// <param name="dataRoot">DevSwitch 数据根目录。</param>
    /// <param name="today">参照"今天"的日期。</param>
    /// <param name="retentionDays">保留天数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>清理结果。</returns>
    public async Task<LogPruneResult> PruneAsync(
        string dataRoot,
        DateOnly today,
        int retentionDays,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("Data root is required.", nameof(dataRoot));
        }

        var logsDirectory = Path.Combine(dataRoot, LogsDirectoryName);

        // logs 目录不存在视为无可清理，返回空结果。
        if (!Directory.Exists(logsDirectory))
        {
            return LogPruneResult.Empty;
        }

        var deleted = new List<string>();
        var retained = new List<string>();

        // 仅枚举顶层 .log 文件，不递归，避免触碰归档子目录或无关内容。
        var files = Directory.EnumerateFiles(logsDirectory, LogSearchPattern, SearchOption.TopDirectoryOnly);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 文件名不符合日期约定（非滚动日志，如轮转归档 app-20260609.1.log），不动它。
            if (!TryParseLogDate(file, out var logDate))
            {
                continue;
            }

            if (IsExpired(logDate, today, retentionDays))
            {
                // File.Delete 无原生异步 API，放入 Task.Run 避免阻塞调用线程。
                await Task.Run(() => File.Delete(file), cancellationToken).ConfigureAwait(false);
                deleted.Add(file);
            }
            else
            {
                retained.Add(file);
            }
        }

        return new LogPruneResult(deleted, retained);
    }

    /// <summary>
    /// 判定指定文件是否需要因超出大小上限而轮转（纯函数）。
    /// </summary>
    /// <param name="filePath">日志文件路径。</param>
    /// <param name="maxBytes">单文件大小上限，默认 20MB；不大于 0 时按默认处理。</param>
    /// <returns>文件存在且大小达到或超过上限返回 true。</returns>
    public static bool CheckRotation(string filePath, long maxBytes = LogRetentionPolicy.DefaultMaxBytes)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        if (maxBytes <= 0)
        {
            maxBytes = LogRetentionPolicy.DefaultMaxBytes;
        }

        var info = new FileInfo(filePath);

        // 文件不存在则无需轮转。
        if (!info.Exists)
        {
            return false;
        }

        // 达到或超过上限即需轮转，保证下一次写入从新文件开始。
        return info.Length >= maxBytes;
    }

    /// <summary>
    /// 将超限日志文件归档改名，使原文件名重新可写。
    /// </summary>
    /// <remarks>
    /// 归档策略：把 <c>app-20260609.log</c> 改名为 <c>app-20260609.&lt;n&gt;.log</c>，
    /// <c>n</c> 从 1 开始递增，找到第一个不存在的序号，避免覆盖既有归档。
    /// 改名后原路径不再存在，调用方下次写入会重建新文件。
    /// </remarks>
    /// <param name="filePath">需轮转的日志文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>归档后的新文件完整路径；若源文件不存在则返回 null。</returns>
    /// <exception cref="ArgumentException">文件路径为空时抛出。</exception>
    public async Task<string?> RotateAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path is required.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            return null;
        }

        var archivePath = BuildArchivePath(filePath);

        // File.Move 无异步重载，放入 Task.Run 保持调用线程不被阻塞。
        await Task.Run(() => File.Move(filePath, archivePath), cancellationToken).ConfigureAwait(false);

        return archivePath;
    }

    /// <summary>
    /// 计算归档目标路径（纯函数）：在原文件名与扩展名之间插入递增序号。
    /// </summary>
    /// <param name="filePath">原日志文件路径。</param>
    /// <returns>首个不冲突的归档路径，例如 <c>app-20260609.1.log</c>。</returns>
    public static string BuildArchivePath(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var directory = Path.GetDirectoryName(filePath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(filePath);
        var extension = Path.GetExtension(filePath); // 含前导 '.'，如 ".log"

        // 从 1 开始寻找第一个未占用的序号，确保不覆盖既有归档。
        for (var n = 1; ; n++)
        {
            cancellationCheckGuard(n);
            var candidateName = $"{stem}.{n}{extension}";
            var candidate = Path.Combine(directory, candidateName);
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    // 防御性上限：序号异常膨胀时抛出，避免极端情况下死循环。
    private static void cancellationCheckGuard(int n)
    {
        if (n > 100_000)
        {
            throw new InvalidOperationException("Too many rotated log archives.");
        }
    }
}
