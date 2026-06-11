// 文件用途：日志「读取 + 解析 + 脱敏」服务，供 UI 层（LogsView）只读展示日志。
//   - 不负责写入 / 轮转 / 保留（那是 DevSwitchLogger / LogRetentionService 的职责）。
//   - 把「枚举通道、解析单行、读取最近 N 行」拆成纯函数 + 异步 IO，最优先可测。
//   - 读取到的每一行消息均经 LogSanitizer.Sanitize 脱敏，避免 Token / 密码 / 完整 PATH 泄露。
// 创建日期：2026-06-11
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：仅 BCL（System.IO、System.Globalization）+ 同程序集内 LogSanitizer
// NOTE: 合法授权学习使用，仅限本地环境。本服务只在传入的数据根目录范围内读取文件，从不写入。

using System.Globalization;

namespace DevSwitch.Core;

/// <summary>
/// 单条解析后的日志行。
/// </summary>
/// <param name="Timestamp">行首解析出的时间戳；解析失败为 <c>null</c>。</param>
/// <param name="Message">消息正文，已调用 <see cref="LogSanitizer.Sanitize(string?)"/> 脱敏。</param>
/// <param name="Channel">来源通道（如 app / helper / download）。</param>
/// <param name="RawLine">原始整行（未脱敏，仅供诊断；UI 不直接展示）。</param>
public sealed record LogEntry(DateTimeOffset? Timestamp, string Message, string Channel, string RawLine);

/// <summary>
/// 日志读取服务：扫描 <c>dataRoot\logs</c> 目录，解析并脱敏日志行供界面展示。
/// </summary>
/// <remarks>
/// 日志文件名约定（见 <see cref="DevSwitchLogger"/>）：<c>&lt;channel&gt;-yyyyMMdd.log</c>，
/// 轮转归档为 <c>&lt;channel&gt;-yyyyMMdd.&lt;n&gt;.log</c>。每行格式：<c>&lt;ISO8601时间&gt; &lt;消息&gt;</c>。
/// 设计取向：纯函数（<see cref="ListChannels"/>、<see cref="ParseLine"/>）最优先单测；
/// 文件读取为异步并带最大行数上限，避免巨大日志卡死 UI 线程。
/// </remarks>
public sealed class LogReaderService
{
    // logs 子目录名，与 DevSwitchLogger / LogRetentionService 保持一致。
    private const string LogsDirectoryName = "logs";

    // 仅扫描 .log 文件，缩小匹配面，避免读到无关文件。
    private const string LogSearchPattern = "*.log";

    // 默认最近行数上限：maxLines <= 0 时使用，兼顾信息量与 UI 流畅度。
    private const int DefaultMaxLines = 500;

    // 文件名内嵌日期格式：8 位 yyyyMMdd。
    private const int DatePartLength = 8;

    /// <summary>
    /// 扫描 <c>dataRoot\logs</c> 目录，返回去重并按字母排序的通道列表（纯逻辑，便于测试）。
    /// </summary>
    /// <param name="dataRoot">DevSwitch 数据根目录。</param>
    /// <returns>通道名列表；目录不存在或无匹配文件时返回空列表。</returns>
    /// <exception cref="ArgumentException">数据根目录为空白时抛出。</exception>
    public IReadOnlyList<string> ListChannels(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("Data root is required.", nameof(dataRoot));
        }

        var logsDirectory = Path.Combine(dataRoot, LogsDirectoryName);

        // 目录不存在视为无通道，返回空列表（UI 据此显示空状态）。
        if (!Directory.Exists(logsDirectory))
        {
            return Array.Empty<string>();
        }

        // 仅枚举顶层 .log 文件，不递归，避免触碰子目录。
        var channels = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(logsDirectory, LogSearchPattern, SearchOption.TopDirectoryOnly))
        {
            // 从文件名提取通道；提取失败（不符合命名约定）则跳过。
            if (TryExtractChannel(file, out var channel))
            {
                channels.Add(channel);
            }
        }

        // SortedSet 已按序去重；转成数组返回不可变快照。
        return channels.ToArray();
    }

    /// <summary>
    /// 解析单行日志（纯函数，重点可测）。
    /// </summary>
    /// <param name="line">原始日志行。</param>
    /// <param name="channel">该行来源通道。</param>
    /// <returns>
    /// 解析后的 <see cref="LogEntry"/>：行首到第一个空格尝试解析为时间戳；
    /// 成功则消息为其后部分，失败则时间戳为 <c>null</c> 且消息为整行。消息均经脱敏。
    /// </returns>
    public static LogEntry ParseLine(string line, string channel)
    {
        // 防御：null 视为空行，避免后续 IndexOf 抛异常。
        line ??= string.Empty;
        channel ??= string.Empty;

        // 以第一个空格切分「时间戳」与「消息」两部分。
        var spaceIndex = line.IndexOf(' ');

        if (spaceIndex > 0)
        {
            var timestampPart = line[..spaceIndex];
            var messagePart = line[(spaceIndex + 1)..];

            // 尝试按不变文化解析行首时间戳；保留原始偏移（不强制转 UTC）。
            if (DateTimeOffset.TryParse(
                    timestampPart,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var timestamp))
            {
                // 解析成功：消息部分脱敏后返回，时间戳单独保留。
                return new LogEntry(timestamp, LogSanitizer.Sanitize(messagePart), channel, line);
            }
        }

        // 无空格 / 时间戳解析失败：整行视为消息（脱敏），时间戳置空。
        return new LogEntry(null, LogSanitizer.Sanitize(line), channel, line);
    }

    /// <summary>
    /// 异步读取指定通道（<c>null</c>=全部通道）的最近 <paramref name="maxLines"/> 行日志，按时间倒序返回。
    /// </summary>
    /// <param name="dataRoot">DevSwitch 数据根目录。</param>
    /// <param name="channel">目标通道；<c>null</c> 或空白表示全部通道。</param>
    /// <param name="maxLines">最大返回行数；不大于 0 时按默认 500 处理。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按时间倒序（最新在前）排列、数量不超过 <paramref name="maxLines"/> 的日志条目。</returns>
    /// <exception cref="ArgumentException">数据根目录为空白时抛出。</exception>
    /// <remarks>
    /// 性能说明：当前实现读取匹配文件的全部行后再取末尾若干行——这受 <paramref name="maxLines"/>
    /// 上限保护（默认 500），返回集合被截断，避免把超大文件整体灌入 UI 导致卡顿。
    /// </remarks>
    public async Task<IReadOnlyList<LogEntry>> ReadRecentAsync(
        string dataRoot,
        string? channel,
        int maxLines,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("Data root is required.", nameof(dataRoot));
        }

        // maxLines <= 0 回落到默认上限，保证 UI 始终有可控的行数。
        if (maxLines <= 0)
        {
            maxLines = DefaultMaxLines;
        }

        var logsDirectory = Path.Combine(dataRoot, LogsDirectoryName);

        // 目录不存在视为无日志，返回空集合。
        if (!Directory.Exists(logsDirectory))
        {
            return Array.Empty<LogEntry>();
        }

        // 是否限定单一通道：channel 空白表示「全部」。
        var filterChannel = string.IsNullOrWhiteSpace(channel) ? null : channel.Trim();

        var entries = new List<LogEntry>();

        // 枚举顶层 .log（含当天与轮转归档），逐文件读取并解析。
        foreach (var file in Directory.EnumerateFiles(logsDirectory, LogSearchPattern, SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 文件名不符合命名约定则跳过（无法判定通道）。
            if (!TryExtractChannel(file, out var fileChannel))
            {
                continue;
            }

            // 指定通道时只读匹配文件；全部通道时读取所有。
            if (filterChannel is not null
                && !string.Equals(fileChannel, filterChannel, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 异步读取整文件行；空行也解析（解析后消息为空），保持与源文件一致。
            var lines = await File.ReadAllLinesAsync(file, cancellationToken).ConfigureAwait(false);
            foreach (var line in lines)
            {
                entries.Add(ParseLine(line, fileChannel));
            }
        }

        // 按时间倒序（最新在前）；无时间戳的行（Timestamp=null）排到最后，避免顶到最前。
        // 使用 OrderByDescending 保持同时间戳行的相对稳定顺序。
        var ordered = entries
            .OrderByDescending(e => e.Timestamp ?? DateTimeOffset.MinValue)
            .Take(maxLines)
            .ToArray();

        return ordered;
    }

    /// <summary>
    /// 从日志文件名提取通道名（纯函数）。
    /// </summary>
    /// <param name="fileName">日志文件名或完整路径，如 <c>app-20260611.log</c> 或 <c>app-20260611.1.log</c>。</param>
    /// <param name="channel">解析得到的通道名。</param>
    /// <returns>文件名符合 <c>&lt;channel&gt;-yyyyMMdd[.n].log</c> 约定并成功提取返回 true。</returns>
    /// <remarks>
    /// 提取步骤：去 <c>.log</c> → 去可能的 <c>.n</c> 轮转序号 → 取最后一个 <c>'-'</c> 之前部分为通道，
    /// 并校验 <c>'-'</c> 之后为 8 位日期，避免把无关文件名误判为通道。
    /// </remarks>
    public static bool TryExtractChannel(string? fileName, out string channel)
    {
        channel = string.Empty;

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var name = Path.GetFileName(fileName);

        // 必须以 .log 结尾。
        if (!name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stem = name[..^4]; // 去掉 ".log"

        // 处理轮转归档：stem 形如 "app-20260611.1"，去掉末尾 ".<digits>" 序号段。
        var lastDot = stem.LastIndexOf('.');
        if (lastDot > 0 && lastDot < stem.Length - 1)
        {
            var suffix = stem[(lastDot + 1)..];
            if (suffix.All(char.IsDigit))
            {
                stem = stem[..lastDot]; // 还原为 "app-20260611"
            }
        }

        // 取最后一个 '-' 之后为日期段，之前为通道（通道名本身可含 '-'）。
        var dashIndex = stem.LastIndexOf('-');
        if (dashIndex <= 0 || dashIndex == stem.Length - 1)
        {
            return false;
        }

        var datePart = stem[(dashIndex + 1)..];

        // 严格 8 位数字日期，避免误判。
        if (datePart.Length != DatePartLength || !datePart.All(char.IsDigit))
        {
            return false;
        }

        channel = stem[..dashIndex];
        return true;
    }
}
