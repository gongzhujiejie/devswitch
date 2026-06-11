// 文件用途：验证 DataRootMigrationService 将旧数据根整体迁移到新目录的行为。
// 创建/修改日期：2026-06-10
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit
// NOTE: 合法授权学习使用，仅限本地环境。测试只使用系统临时目录，绝不删除源目录。

using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class DataRootMigrationServiceTests
{
    [Fact]
    public async Task MigrateCopiesNestedTreeAndKeepsSourceIntact()
    {
        // NOTE: 迁移后目标含相同文件且内容一致；源目录仍然存在（不删源，安全第一）。
        var source = CreateTemporaryDirectory();
        var target = Path.Combine(CreateTemporaryDirectory(), "NewRoot");
        var configDir = Path.Combine(source, "config");
        Directory.CreateDirectory(configDir);
        const string sdksJson = "{\"installed\":[\"jdk-21\",\"node-20\"]}";
        await File.WriteAllTextAsync(Path.Combine(configDir, "sdks.json"), sdksJson);
        Directory.CreateDirectory(Path.Combine(source, "logs"));
        await File.WriteAllTextAsync(Path.Combine(source, "logs", "app.log"), "line1\nline2");

        var service = new DataRootMigrationService();
        var result = await service.MigrateAsync(source, target);

        Assert.True(result.Success);
        Assert.Equal(2, result.CopiedFiles);
        Assert.Equal(sdksJson, await File.ReadAllTextAsync(Path.Combine(target, "config", "sdks.json")));
        Assert.True(File.Exists(Path.Combine(target, "logs", "app.log")));
        // 源仍在
        Assert.True(File.Exists(Path.Combine(configDir, "sdks.json")));
    }

    [Fact]
    public async Task MigrateReturnsZeroFilesWhenSourceEqualsTarget()
    {
        // NOTE: source 与 target 规范化后相同，无需迁移，返回 Success 且 CopiedFiles=0。
        var root = CreateTemporaryDirectory();
        var sameButDifferentForm = root + Path.DirectorySeparatorChar; // 同一目录的不同写法

        var service = new DataRootMigrationService();
        var result = await service.MigrateAsync(root, sameButDifferentForm);

        Assert.True(result.Success);
        Assert.Equal(0, result.CopiedFiles);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public async Task MigrateTreatsMissingSourceAsNothingToMigrate()
    {
        // NOTE: 源不存在视为“无数据可迁”，按约定 Success=true, CopiedFiles=0。
        var missingSource = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"));
        var target = CreateTemporaryDirectory();

        var service = new DataRootMigrationService();
        var result = await service.MigrateAsync(missingSource, target);

        Assert.True(result.Success);
        Assert.Equal(0, result.CopiedFiles);
    }

    [Fact]
    public async Task MigrateOverwritesExistingTargetFileWhenOverwriteTrue()
    {
        // NOTE: 目标已存在同名文件，默认 overwrite=true 应覆盖为源内容。
        var source = CreateTemporaryDirectory();
        var target = CreateTemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(source, "settings.json"), "NEW");
        await File.WriteAllTextAsync(Path.Combine(target, "settings.json"), "OLD");

        var service = new DataRootMigrationService();
        var result = await service.MigrateAsync(source, target, overwrite: true);

        Assert.True(result.Success);
        Assert.Equal("NEW", await File.ReadAllTextAsync(Path.Combine(target, "settings.json")));
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
