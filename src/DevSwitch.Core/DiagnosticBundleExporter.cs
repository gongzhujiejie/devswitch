// 文件用途：构造并导出 DevSwitch 诊断包——脱敏后的日志摘要、配置摘要与诊断结果，
//           严禁包含完整 PATH、完整环境变量、Token/认证信息或用户目录全量扫描。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.IO.Compression、System.Text.Json、System.Text.RegularExpressions
// NOTE: 合法授权学习使用，仅限本地环境。导出前对所有文本再次脱敏。

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevSwitch.Core;

/// <summary>
/// 待写入诊断包的单个文件（内存内容，写盘前可统一审查）。
/// </summary>
/// <param name="RelativePath">在诊断包内的相对路径，例如 report.json、logs-summary.txt。</param>
/// <param name="Content">已脱敏的文本内容。</param>
public sealed record DiagnosticBundleEntry(string RelativePath, string Content);

/// <summary>
/// 诊断包内容（纯数据，与写盘行为分离，便于单测）。
/// </summary>
/// <param name="Entries">诊断包内全部条目。</param>
public sealed record DiagnosticBundleContent(IReadOnlyList<DiagnosticBundleEntry> Entries);

/// <summary>
/// 文本脱敏器：把可能含敏感信息的文本打码后输出。
/// </summary>
/// <remarks>
/// 设计目标：可纯单测。给定含敏感串的文本，输出已打码版本：
/// - 隐藏用户目录（C:\Users\&lt;name&gt; 中的用户名替换为 &lt;user&gt;）。
/// - 打码 Token / Bearer / key / password / secret 等键值。
/// - PATH 这类长列表交由调用方先裁剪，仅保留 DevSwitch 相关条目（见 <see cref="FilterDevSwitchPathEntries"/>）。
/// </remarks>
public static class DiagnosticSanitizer
{
    // 匹配 Windows 用户目录中的用户名段：C:\Users\<name> 或 /Users/<name>
    private static readonly Regex UserDirectoryRegex = new(
        @"(?<prefix>[A-Za-z]:\\Users\\|/Users/)(?<user>[^\\/\r\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 匹配常见敏感键值对：token=xxx、"password": "xxx"、api_key: xxx 等。
    // NOTE: key 两侧可能带引号（JSON 形如 "password"），分隔符前后也可能有引号与空白，
    // 因此 key 后允许可选结束引号，sep 允许冒号/等号并吸收周围引号与空白。
    private static readonly Regex SecretKeyValueRegex = new(
        @"(?<key>(?:authorization|token|access[_-]?token|refresh[_-]?token|secret|password|passwd|pwd|api[_-]?key|client[_-]?secret))(?<keyq>[""']?)(?<sep>\s*[:=]\s*|\s+)(?<quote>[""']?)(?<value>[^""'\s,;]+)(?<endquote>[""']?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 匹配 Bearer / Basic 认证头中的凭据。
    private static readonly Regex BearerRegex = new(
        @"(?<scheme>Bearer|Basic)\s+(?<cred>[A-Za-z0-9\-\._~\+/=]{8,})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 对单段文本脱敏。
    /// </summary>
    /// <param name="text">原始文本；为空时返回空字符串。</param>
    /// <returns>已打码文本。</returns>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // 顺序：先打码键值对，再打码认证凭据，最后隐藏用户名。
        // NOTE: 先处理键值对会把 "Authorization: Bearer xxx" 的 value 当作 "Bearer"，
        // 这并不能隐藏真正的凭据；因此 value 命中 Bearer/Basic 方案词时跳过键值替换，
        // 交给随后的 BearerRegex 输出 "Bearer ***"。
        var result = SecretKeyValueRegex.Replace(text, match =>
        {
            var value = match.Groups["value"].Value;
            if (value.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
                || value.Equals("Basic", StringComparison.OrdinalIgnoreCase))
            {
                // 不替换，保留原样，让 BearerRegex 接管整段凭据。
                return match.Value;
            }

            // 保留 key 与分隔符，仅打码 value，方便排错时仍能看出存在该字段。
            var keyQuote = match.Groups["keyq"].Value;
            var quote = match.Groups["quote"].Value;
            var endQuote = match.Groups["endquote"].Value;
            return $"{match.Groups["key"].Value}{keyQuote}{match.Groups["sep"].Value}{quote}***{endQuote}";
        });

        result = BearerRegex.Replace(result, match => $"{match.Groups["scheme"].Value} ***");

        result = UserDirectoryRegex.Replace(result, match => $"{match.Groups["prefix"].Value}<user>");

        return result;
    }

    /// <summary>
    /// 从完整 PATH 条目中只保留 DevSwitch 相关条目，丢弃其余用户/系统条目。
    /// </summary>
    /// <param name="pathEntries">完整 PATH 条目集合。</param>
    /// <returns>仅含 DevSwitch 相关条目的列表（已脱敏用户名）。</returns>
    public static IReadOnlyList<string> FilterDevSwitchPathEntries(IReadOnlyList<string> pathEntries)
    {
        ArgumentNullException.ThrowIfNull(pathEntries);

        // NOTE: 诊断包严禁包含完整 PATH，只保留含 DevSwitch 标识的托管片段，再脱敏用户名。
        return pathEntries
            .Where(entry => !string.IsNullOrWhiteSpace(entry)
                && entry.Contains("DevSwitch", StringComparison.OrdinalIgnoreCase))
            .Select(Sanitize)
            .ToArray();
    }
}

/// <summary>
/// 诊断包导出器：构造脱敏内容（纯函数）并写入磁盘（zip 或目录）。
/// </summary>
public static class DiagnosticBundleExporter
{
    private static readonly JsonSerializerOptions ReportSerializerOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// 构造诊断包内容（纯函数，不写盘）。
    /// </summary>
    /// <param name="report">Doctor 诊断报告。</param>
    /// <param name="settings">当前配置；仅提取摘要字段，不包含敏感数据。</param>
    /// <param name="logExcerpt">日志原文摘录（例如最近若干行）；将被脱敏。</param>
    /// <param name="devSwitchPathEntries">仅 DevSwitch 相关的 PATH 条目（调用方应已用 FilterDevSwitchPathEntries 过滤）。</param>
    /// <returns>诊断包内容。</returns>
    public static DiagnosticBundleContent BuildContent(
        DoctorReport report,
        DevSwitchSettings settings,
        string? logExcerpt,
        IReadOnlyList<string>? devSwitchPathEntries = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(settings);

        var entries = new List<DiagnosticBundleEntry>();

        // 1) 诊断结果：序列化为 JSON，detail 已在 Doctor 内脱敏，这里再兜底脱敏一次。
        var reportEntries = report.Results.Select(result => new
        {
            result.Id,
            result.Title,
            Severity = result.Severity.ToString(),
            Detail = DiagnosticSanitizer.Sanitize(result.Detail),
            Suggestion = DiagnosticSanitizer.Sanitize(result.Suggestion),
        });

        var reportJson = JsonSerializer.Serialize(new
        {
            generatedAt = report.GeneratedAt,
            overallSeverity = report.OverallSeverity.ToString(),
            counts = report.Counts.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value),
            results = reportEntries,
        }, ReportSerializerOptions);

        entries.Add(new DiagnosticBundleEntry("report.json", reportJson));

        // 2) 配置摘要：只取非敏感字段；dataRoot 经过脱敏隐藏用户名。
        var configSummary = JsonSerializer.Serialize(new
        {
            schemaVersion = settings.SchemaVersion,
            dataRoot = DiagnosticSanitizer.Sanitize(settings.DataRoot),
            language = settings.Language,
            download = new
            {
                settings.Download.Parallelism,
                settings.Download.KeepArchives,
                // preferredMirror 可能含自定义 URL，脱敏后保留。
                preferredMirror = DiagnosticSanitizer.Sanitize(settings.Download.PreferredMirror),
            },
            compatibility = new
            {
                settings.Compatibility.SetJdkHome,
                settings.Compatibility.SetM2Home,
            },
            update = new
            {
                settings.Update.Source,
                settings.Update.FallbackSource,
            },
            // 仅保留 DevSwitch 相关 PATH 条目，绝不写完整 PATH。
            devSwitchPathEntries = devSwitchPathEntries ?? Array.Empty<string>(),
        }, ReportSerializerOptions);

        entries.Add(new DiagnosticBundleEntry("config-summary.json", configSummary));

        // 3) 日志摘要：脱敏后写入，避免 Token/路径泄露。
        entries.Add(new DiagnosticBundleEntry(
            "logs-summary.txt",
            DiagnosticSanitizer.Sanitize(logExcerpt) ?? string.Empty));

        return new DiagnosticBundleContent(entries);
    }

    /// <summary>
    /// 将诊断包内容写入 zip 文件。
    /// </summary>
    /// <param name="content">诊断包内容。</param>
    /// <param name="zipFilePath">目标 zip 路径；父目录会被自动创建。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public static async Task WriteZipAsync(
        DiagnosticBundleContent content,
        string zipFilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(zipFilePath))
        {
            throw new ArgumentException("Zip file path is required.", nameof(zipFilePath));
        }

        var directory = Path.GetDirectoryName(zipFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // NOTE: 显式覆盖已存在文件，避免追加进旧诊断包导致内容混杂。
        // useAsync: true 启用异步 IO，与下方异步 entry 写入配套，减少线程阻塞。
        await using var fileStream = new FileStream(
            zipFilePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create);

        foreach (var entry in content.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var zipEntry = archive.CreateEntry(entry.RelativePath, CompressionLevel.Optimal);
            await using var entryStream = zipEntry.Open();
            var bytes = Encoding.UTF8.GetBytes(entry.Content);
            // NOTE: 库代码补 ConfigureAwait(false)，不捕获调用方同步上下文。
            await entryStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 将诊断包内容写入目录（每个条目一个文件）。
    /// </summary>
    /// <param name="content">诊断包内容。</param>
    /// <param name="targetDirectory">目标目录；会被自动创建。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public static async Task WriteDirectoryAsync(
        DiagnosticBundleContent content,
        string targetDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            throw new ArgumentException("Target directory is required.", nameof(targetDirectory));
        }

        Directory.CreateDirectory(targetDirectory);

        foreach (var entry in content.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filePath = Path.Combine(targetDirectory, entry.RelativePath);
            var entryDir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(entryDir))
            {
                Directory.CreateDirectory(entryDir);
            }

            // NOTE: 库代码补 ConfigureAwait(false)，不捕获调用方同步上下文。
            await File.WriteAllTextAsync(filePath, entry.Content, cancellationToken).ConfigureAwait(false);
        }
    }
}
