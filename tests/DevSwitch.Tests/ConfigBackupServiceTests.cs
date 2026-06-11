// 文件用途：验证 ConfigBackupService 的备份路径构造（纯函数）与真实文件备份行为。
// 创建/修改日期：2026-06-10
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit
// NOTE: 合法授权学习使用，仅限本地环境。测试只写入系统临时目录，不读取真实用户配置。

using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class ConfigBackupServiceTests
{
    [Fact]
    public void BuildBackupPathEmbedsFileNameOldVersionAndTimestamp()
    {
        // NOTE: 备份路径是纯函数，最优先单测：文件名、旧版本、时间戳都应可预测地出现在结果里。
        var dataRoot = CreateTemporaryDirectory();
        var source = Path.Combine(dataRoot, "config", "settings.json");
        var timestamp = new DateTimeOffset(2026, 6, 10, 9, 15, 0, 123, TimeSpan.Zero);

        var backupPath = ConfigBackupService.BuildBackupPath(dataRoot, source, oldVersion: 1, timestamp);

        var expectedDirectory = Path.Combine(dataRoot, "backups", "config");
        Assert.Equal(expectedDirectory, Path.GetDirectoryName(backupPath));
        Assert.Equal("settings.json.1.20260610T091500123Z.bak", Path.GetFileName(backupPath));
    }

    [Fact]
    public async Task BackupAsyncCopiesContentAndKeepsSourceFileIntact()
    {
        // NOTE: 备份后原文件必须仍在且内容不变，备份内容必须与原文件逐字节一致。
        var dataRoot = CreateTemporaryDirectory();
        var configDir = Path.Combine(dataRoot, "config");
        Directory.CreateDirectory(configDir);
        var source = Path.Combine(configDir, "settings.json");
        const string original = "{\"schemaVersion\":1,\"language\":\"zh-CN\"}";
        await File.WriteAllTextAsync(source, original);

        var service = new ConfigBackupService();
        var backupPath = await service.BackupAsync(
            dataRoot, source, oldVersion: 1, new DateTimeOffset(2026, 6, 10, 9, 0, 0, TimeSpan.Zero));

        Assert.True(File.Exists(source));
        Assert.Equal(original, await File.ReadAllTextAsync(source));
        Assert.True(File.Exists(backupPath));
        Assert.Equal(original, await File.ReadAllTextAsync(backupPath));
    }

    [Fact]
    public async Task BackupAsyncThrowsWhenSourceMissing()
    {
        // NOTE: 缺源文件时备份无意义，应明确抛 FileNotFoundException 而不是静默生成空备份。
        var dataRoot = CreateTemporaryDirectory();
        var missing = Path.Combine(dataRoot, "config", "settings.json");

        var service = new ConfigBackupService();
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => service.BackupAsync(dataRoot, missing, 1, DateTimeOffset.UtcNow));
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
