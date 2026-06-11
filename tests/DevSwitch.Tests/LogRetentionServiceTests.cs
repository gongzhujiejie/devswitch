// 文件用途：验证 LogRetentionService 的日期解析、过期判定、清理与轮转行为。
// 创建日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit
// NOTE: 合法授权学习使用，仅限本地环境。本测试只读写临时目录。

using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class LogRetentionServiceTests
{
    // ---------- 纯函数：文件名日期解析 ----------

    [Theory]
    [InlineData("app-20260609.log", 2026, 6, 9)]
    [InlineData("helper-20251231.log", 2025, 12, 31)]
    [InlineData("download-20260101.log", 2026, 1, 1)]
    [InlineData(@"C:\data\logs\app-20260609.log", 2026, 6, 9)] // 接受完整路径
    public void TryParseLogDateParsesValidNames(string fileName, int year, int month, int day)
    {
        var ok = LogRetentionService.TryParseLogDate(fileName, out var date);

        Assert.True(ok);
        Assert.Equal(new DateOnly(year, month, day), date);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("notes.txt")]                 // 非 .log
    [InlineData("app.log")]                   // 无日期段
    [InlineData("app-2026069.log")]           // 7 位，非 8 位
    [InlineData("app-20261301.log")]          // 非法月份
    [InlineData("20260609.log")]              // 无 channel 段（'-' 在首位等价情况）
    [InlineData("app-20260609.1.log")]        // 轮转归档名，不应被当作日期日志
    public void TryParseLogDateRejectsInvalidNames(string? fileName)
    {
        var ok = LogRetentionService.TryParseLogDate(fileName, out _);

        Assert.False(ok);
    }

    // ---------- 纯函数：过期判定 ----------

    [Fact]
    public void IsExpiredReturnsFalseForTodayWithin14Days()
    {
        var today = new DateOnly(2026, 6, 14);

        // 最近 14 天窗口：[2026-06-01, 2026-06-14]
        Assert.False(LogRetentionService.IsExpired(new DateOnly(2026, 6, 14), today, 14)); // 今天
        Assert.False(LogRetentionService.IsExpired(new DateOnly(2026, 6, 1), today, 14));  // 窗口最早一天
    }

    [Fact]
    public void IsExpiredReturnsTrueForOlderThanRetention()
    {
        var today = new DateOnly(2026, 6, 14);

        // 2026-05-31 在 14 天窗口之外，应过期
        Assert.True(LogRetentionService.IsExpired(new DateOnly(2026, 5, 31), today, 14));
        Assert.True(LogRetentionService.IsExpired(new DateOnly(2026, 1, 1), today, 14));
    }

    [Fact]
    public void IsExpiredTreatsNonPositiveRetentionAsDefault()
    {
        var today = new DateOnly(2026, 6, 14);

        // retentionDays <= 0 时按默认 14 天处理。
        Assert.True(LogRetentionService.IsExpired(new DateOnly(2026, 5, 31), today, 0));
        Assert.False(LogRetentionService.IsExpired(new DateOnly(2026, 6, 2), today, -5));
    }

    // ---------- 异步：清理 ----------

    [Fact]
    public async Task PruneDeletesExpiredAndKeepsRecentAndIgnoresNonLog()
    {
        var dataRoot = CreateTemporaryDirectory();
        var logsDir = Path.Combine(dataRoot, "logs");
        Directory.CreateDirectory(logsDir);

        var today = new DateOnly(2026, 6, 14);

        // 过期日志（14 天外）
        var expired1 = await WriteFileAsync(logsDir, "app-20260501.log", "old");
        var expired2 = await WriteFileAsync(logsDir, "helper-20260520.log", "old");
        // 保留日志（14 天内）
        var recent1 = await WriteFileAsync(logsDir, "app-20260614.log", "new");
        var recent2 = await WriteFileAsync(logsDir, "download-20260601.log", "new");
        // 非日志文件 / 归档文件：不应被触碰
        var nonLog = await WriteFileAsync(logsDir, "readme.txt", "keep");
        var archive = await WriteFileAsync(logsDir, "app-20260501.1.log", "archive");

        var service = new LogRetentionService();
        var result = await service.PruneAsync(dataRoot, today, 14);

        // 过期被删
        Assert.False(File.Exists(expired1));
        Assert.False(File.Exists(expired2));
        Assert.Contains(expired1, result.DeletedFiles);
        Assert.Contains(expired2, result.DeletedFiles);

        // 近期保留
        Assert.True(File.Exists(recent1));
        Assert.True(File.Exists(recent2));
        Assert.Contains(recent1, result.RetainedFiles);
        Assert.Contains(recent2, result.RetainedFiles);

        // 非日志 / 归档不动，也不出现在结果里
        Assert.True(File.Exists(nonLog));
        Assert.True(File.Exists(archive));
        Assert.DoesNotContain(nonLog, result.DeletedFiles);
        Assert.DoesNotContain(archive, result.DeletedFiles);
    }

    [Fact]
    public async Task PruneReturnsEmptyWhenLogsDirectoryMissing()
    {
        var dataRoot = CreateTemporaryDirectory(); // 不创建 logs 子目录

        var service = new LogRetentionService();
        var result = await service.PruneAsync(dataRoot, new DateOnly(2026, 6, 14), 14);

        Assert.Empty(result.DeletedFiles);
        Assert.Empty(result.RetainedFiles);
    }

    [Fact]
    public async Task PruneThrowsOnEmptyDataRoot()
    {
        var service = new LogRetentionService();

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.PruneAsync("  ", new DateOnly(2026, 6, 14), 14));
    }

    // ---------- 纯函数：轮转判定 ----------

    [Fact]
    public void CheckRotationDetectsOversizedFile()
    {
        var dataRoot = CreateTemporaryDirectory();
        var file = Path.Combine(dataRoot, "big.log");

        // 写入超过自定义上限的数据。
        File.WriteAllBytes(file, new byte[2048]);

        Assert.True(LogRetentionService.CheckRotation(file, maxBytes: 1024));
        Assert.False(LogRetentionService.CheckRotation(file, maxBytes: 4096));
    }

    [Fact]
    public void CheckRotationReturnsFalseForMissingOrEmptyPath()
    {
        var missing = Path.Combine(CreateTemporaryDirectory(), "nope.log");

        Assert.False(LogRetentionService.CheckRotation(missing, 1024));
        Assert.False(LogRetentionService.CheckRotation("", 1024));
    }

    [Fact]
    public void CheckRotationUses20MbDefault()
    {
        var dataRoot = CreateTemporaryDirectory();
        var file = Path.Combine(dataRoot, "default.log");
        File.WriteAllBytes(file, new byte[1024]); // 远小于 20MB

        Assert.False(LogRetentionService.CheckRotation(file));
    }

    // ---------- 异步：轮转执行 ----------

    [Fact]
    public async Task RotateRenamesFileAndFreesOriginalName()
    {
        var dataRoot = CreateTemporaryDirectory();
        var file = Path.Combine(dataRoot, "app-20260609.log");
        await File.WriteAllTextAsync(file, "content");

        var service = new LogRetentionService();
        var archive = await service.RotateAsync(file);

        Assert.NotNull(archive);
        Assert.False(File.Exists(file));          // 原文件名释放，可重新写入
        Assert.True(File.Exists(archive));        // 归档文件存在
        Assert.EndsWith("app-20260609.1.log", archive);
        Assert.Equal("content", await File.ReadAllTextAsync(archive!));
    }

    [Fact]
    public async Task RotateIncrementsArchiveIndexWhenPriorArchivesExist()
    {
        var dataRoot = CreateTemporaryDirectory();
        var file = Path.Combine(dataRoot, "app-20260609.log");
        await File.WriteAllTextAsync(file, "v2");
        // 预置已有归档 .1，使本次应生成 .2
        await File.WriteAllTextAsync(Path.Combine(dataRoot, "app-20260609.1.log"), "v1");

        var service = new LogRetentionService();
        var archive = await service.RotateAsync(file);

        Assert.NotNull(archive);
        Assert.EndsWith("app-20260609.2.log", archive);
    }

    [Fact]
    public async Task RotateReturnsNullWhenFileMissing()
    {
        var service = new LogRetentionService();
        var archive = await service.RotateAsync(Path.Combine(CreateTemporaryDirectory(), "ghost.log"));

        Assert.Null(archive);
    }

    [Fact]
    public void BuildArchivePathInsertsIndexBeforeExtension()
    {
        var dataRoot = CreateTemporaryDirectory();
        var file = Path.Combine(dataRoot, "helper-20260609.log");

        var archive = LogRetentionService.BuildArchivePath(file);

        Assert.EndsWith("helper-20260609.1.log", archive);
    }

    // ---------- 测试辅助 ----------

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<string> WriteFileAsync(string directory, string name, string content)
    {
        var path = Path.Combine(directory, name);
        await File.WriteAllTextAsync(path, content);
        return path;
    }
}
