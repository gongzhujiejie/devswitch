// 文件用途：提供 DevSwitch 本地日志写入能力，将事件写入数据根目录 logs 下的日志文件。
// 创建日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.IO

namespace DevSwitch.Core;

/// <summary>
/// DevSwitch 本地日志写入器。
/// </summary>
public sealed class DevSwitchLogger
{
    private readonly string dataRoot;
    private readonly string channel;

    /// <summary>
    /// 创建日志写入器。
    /// </summary>
    /// <param name="dataRoot">DevSwitch 数据根目录。</param>
    /// <param name="channel">日志通道，例如 app、helper 或 download。</param>
    /// <exception cref="ArgumentException">参数为空时抛出。</exception>
    public DevSwitchLogger(string dataRoot, string channel)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("Data root is required.", nameof(dataRoot));
        }

        if (string.IsNullOrWhiteSpace(channel))
        {
            throw new ArgumentException("Log channel is required.", nameof(channel));
        }

        this.dataRoot = dataRoot;
        this.channel = channel;
    }

    /// <summary>
    /// 写入一条日志事件。
    /// </summary>
    /// <param name="message">要记录的事件文本。</param>
    /// <returns>异步写入任务。</returns>
    public async Task WriteEventAsync(string message)
    {
        // NOTE: M0 先实现最小可观察行为：写入后生成 app-yyyyMMdd.log。
        // 后续再通过测试驱动滚动、脱敏和保留策略。
        var logDirectory = Path.Combine(dataRoot, "logs");
        Directory.CreateDirectory(logDirectory);

        var logFile = Path.Combine(logDirectory, $"{channel}-{DateTimeOffset.Now:yyyyMMdd}.log");
        var line = $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}";

        await File.AppendAllTextAsync(logFile, line);
    }
}
