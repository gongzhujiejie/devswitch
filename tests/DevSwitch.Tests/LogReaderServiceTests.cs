// 文件用途：验证 LogReaderService 的通道枚举、单行解析、脱敏与最近行读取行为。
// 创建日期：2026-06-11
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit
// NOTE: 合法授权学习使用，仅限本地环境。本测试只读写临时目录。

using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class LogReaderServiceTests
{
    // ---------- 纯函数：通道枚举 ----------

    [Fact]
    public void ListChannelsDeduplicatesAndSortsChannelsFromFileNames()
    {
        var dataRoot = CreateTemporaryDirectory();
        var logsDir = Path.Combine(dataRoot, "logs");
        Directory.CreateDirectory(logsDir);

        // 同一通道多日期 / 含轮转归档应去重；不同通道应各保留一份。
        WriteFile(logsDir, "app-20260610.log", "x");
        WriteFile(logsDir, "app-20260611.log", "x");
        WriteFile(logsDir, "app-20260611.1.log", "x"); // 轮转归档，仍归入 app
        WriteFile(logsDir, "helper-20260611.log", "x");
        WriteFile(logsDir, "download-20260611.log", "x");
        WriteFile(logsDir, "readme.txt", "x");          // 非 .log，忽略
        WriteFile(logsDir, "20260611.log", "x");        // 无通道段，忽略

        var service = new LogReaderService();
        var channels = service.ListChannels(dataRoot);

        // 去重 + 字母排序：app / download / helper。
        Assert.Equal(new[] { "app", "download", "helper" }, channels);
    }

    [Fact]
    public void ListChannelsReturnsEmptyWhenLogsDirectoryMissing()
    {
        var dataRoot = CreateTemporaryDirectory(); // 不创建 logs 子目录

        var service = new LogReaderService();

        Assert.Empty(service.ListChannels(dataRoot));
    }

    [Fact]
    public void ListChannelsThrowsOnEmptyDataRoot()
    {
        var service = new LogReaderService();

        Assert.Throws<ArgumentException>(() => service.ListChannels("  "));
    }

    // ---------- 纯函数：单行解析 ----------

    [Fact]
    public void ParseLineSplitsTimestampAndMessage()
    {
        var line = "2026-06-11T10:30:00.000+08:00 switch java ok";

        var entry = LogReaderService.ParseLine(line, "app");

        Assert.NotNull(entry.Timestamp);
        Assert.Equal("switch java ok", entry.Message);
        Assert.Equal("app", entry.Channel);
        Assert.Equal(line, entry.RawLine);
    }

    [Fact]
    public void ParseLineSetsNullTimestampForInvalidTimestamp()
    {
        // 行首不是合法时间戳：整行作为消息，时间戳为 null。
        var line = "this is not a timestamped line";

        var entry = LogReaderService.ParseLine(line, "app");

        Assert.Null(entry.Timestamp);
        Assert.Equal("this is not a timestamped line", entry.Message);
    }

    [Fact]
    public void ParseLineSanitizesSecretsInMessage()
    {
        // 含 token 的行：脱敏后不应保留原始 token，且出现打码标记。
        var line = "2026-01-01T00:00:00Z token=abcdef123456";

        var entry = LogReaderService.ParseLine(line, "app");

        Assert.NotNull(entry.Timestamp);
        Assert.DoesNotContain("abcdef123456", entry.Message);
        Assert.Contains("***", entry.Message);
        // 原始整行保留未脱敏，便于诊断（UI 不直接展示 RawLine）。
        Assert.Contains("abcdef123456", entry.RawLine);
    }

    // ---------- 异步：最近行读取 ----------

    [Fact]
    public async Task ReadRecentReturnsEmptyWhenDirectoryMissing()
    {
        var dataRoot = CreateTemporaryDirectory(); // 无 logs 目录

        var service = new LogReaderService();
        var entries = await service.ReadRecentAsync(dataRoot, channel: null, maxLines: 500, CancellationToken.None);

        Assert.Empty(entries);
    }

    [Fact]
    public async Task ReadRecentReturnsNewestFirstAndRespectsMaxLines()
    {
        var dataRoot = CreateTemporaryDirectory();
        var logsDir = Path.Combine(dataRoot, "logs");
        Directory.CreateDirectory(logsDir);

        // 写入 5 行递增时间戳；maxLines=3 应只取最新 3 行且倒序。
        var lines = string.Join(Environment.NewLine, new[]
        {
            "2026-06-11T10:00:00Z line-1",
            "2026-06-11T10:01:00Z line-2",
            "2026-06-11T10:02:00Z line-3",
            "2026-06-11T10:03:00Z line-4",
            "2026-06-11T10:04:00Z line-5",
        });
        WriteFile(logsDir, "app-20260611.log", lines);

        var service = new LogReaderService();
        var entries = await service.ReadRecentAsync(dataRoot, channel: "app", maxLines: 3, CancellationToken.None);

        // 数量受 maxLines 限制。
        Assert.Equal(3, entries.Count);
        // 倒序：最新（line-5）在前，line-3 在末。
        Assert.Equal("line-5", entries[0].Message);
        Assert.Equal("line-4", entries[1].Message);
        Assert.Equal("line-3", entries[2].Message);
    }

    [Fact]
    public async Task ReadRecentFiltersByChannel()
    {
        var dataRoot = CreateTemporaryDirectory();
        var logsDir = Path.Combine(dataRoot, "logs");
        Directory.CreateDirectory(logsDir);

        WriteFile(logsDir, "app-20260611.log", "2026-06-11T10:00:00Z app-line");
        WriteFile(logsDir, "helper-20260611.log", "2026-06-11T10:01:00Z helper-line");

        var service = new LogReaderService();

        // 指定 app：只含 app 通道。
        var appOnly = await service.ReadRecentAsync(dataRoot, "app", 500, CancellationToken.None);
        Assert.Single(appOnly);
        Assert.Equal("app", appOnly[0].Channel);

        // null：两个通道都读到。
        var all = await service.ReadRecentAsync(dataRoot, null, 500, CancellationToken.None);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task ReadRecentUsesDefaultWhenMaxLinesNonPositive()
    {
        var dataRoot = CreateTemporaryDirectory();
        var logsDir = Path.Combine(dataRoot, "logs");
        Directory.CreateDirectory(logsDir);

        // 写入 600 行；maxLines<=0 应回落默认 500，结果被截断到 500。
        var lines = string.Join(
            Environment.NewLine,
            Enumerable.Range(1, 600).Select(i => $"2026-06-11T10:00:00Z line-{i}"));
        WriteFile(logsDir, "app-20260611.log", lines);

        var service = new LogReaderService();
        var entries = await service.ReadRecentAsync(dataRoot, "app", maxLines: 0, CancellationToken.None);

        Assert.Equal(500, entries.Count);
    }

    [Fact]
    public async Task ReadRecentThrowsOnEmptyDataRoot()
    {
        var service = new LogReaderService();

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.ReadRecentAsync("  ", null, 500, CancellationToken.None));
    }

    // ---------- 测试辅助 ----------

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteFile(string directory, string name, string content)
    {
        File.WriteAllText(Path.Combine(directory, name), content);
    }
}
