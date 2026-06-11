// 文件用途：验证 DevSwitch 本地滚动日志的公开写入行为。
// 创建日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit
// NOTE: 合法授权学习使用，仅限本地环境。本测试只写入临时目录。

using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class DevSwitchLoggerTests
{
    [Fact]
    public async Task WriteEventCreatesLogFileUnderDataRootLogsDirectory()
    {
        // NOTE: 通过公开日志接口验证用户可观察行为：写入事件后，数据根目录 logs 下出现日志文件。
        var dataRoot = CreateTemporaryDirectory();
        var logger = new DevSwitchLogger(dataRoot, "app");

        await logger.WriteEventAsync("helper ping smoke test passed");

        var logDirectory = Path.Combine(dataRoot, "logs");
        var logFiles = Directory.GetFiles(logDirectory, "app-*.log");

        var logFile = Assert.Single(logFiles);
        var content = await File.ReadAllTextAsync(logFile);
        Assert.Contains("helper ping smoke test passed", content);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
