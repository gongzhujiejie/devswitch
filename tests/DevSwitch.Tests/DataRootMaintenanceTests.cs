// 文件用途：验证 DataRootMaintenance.PurgeUpdatesStagingDirectory 能可靠清空历史更新包暂存。
// 创建/修改日期：2026-06-11
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit、System.IO
// NOTE: 合法授权学习使用，仅限本地环境。本测试只操作临时目录，不触碰真实数据根。

using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class DataRootMaintenanceTests
{
    [Fact]
    public void PurgeRemovesAllVersionDirectoriesAndKeepsUpdatesRoot()
    {
        // 准备：模拟 dataRoot\updates 下有 3 个历史版本目录，每个含 zip + extracted 子目录。
        var dataRoot = CreateTempDataRoot();
        var updatesRoot = Path.Combine(dataRoot, "updates");
        foreach (var version in new[] { "v0.2.0", "v0.2.4", "v0.2.5" })
        {
            var versionDir = Path.Combine(updatesRoot, version);
            Directory.CreateDirectory(Path.Combine(versionDir, "extracted"));
            File.WriteAllBytes(Path.Combine(versionDir, "update.zip"), new byte[] { 1, 2, 3 });
            File.WriteAllText(Path.Combine(versionDir, "extracted", "DevSwitch.App.dll"), "binary");
        }

        DataRootMaintenance.PurgeUpdatesStagingDirectory(dataRoot);

        // 期望：updates 目录本身保留（避免下一次自更新无处落盘），但里面的子目录与文件全清空。
        Assert.True(Directory.Exists(updatesRoot));
        Assert.Empty(Directory.GetFileSystemEntries(updatesRoot));
    }

    [Fact]
    public void PurgeIsNoOpWhenUpdatesDirectoryDoesNotExist()
    {
        // 首次启动尚未发生过自更新时，updates 目录不存在；清理应静默通过。
        var dataRoot = CreateTempDataRoot();
        DataRootMaintenance.PurgeUpdatesStagingDirectory(dataRoot);

        // 不创建空 updates 目录，避免无谓 IO；调用方下次自更新时会按需创建。
        Assert.False(Directory.Exists(Path.Combine(dataRoot, "updates")));
    }

    [Fact]
    public void PurgeIgnoresNullOrWhitespaceDataRoot()
    {
        // 极端容错：startup 日志里偶尔出现 dataRoot 解析失败回退为空字符串，
        // 这里要保证不抛异常、不影响主启动流程。
        DataRootMaintenance.PurgeUpdatesStagingDirectory(null!);
        DataRootMaintenance.PurgeUpdatesStagingDirectory(string.Empty);
        DataRootMaintenance.PurgeUpdatesStagingDirectory("   ");
    }

    [Fact]
    public void PurgeKeepsSiblingDirectoriesUnderDataRoot()
    {
        // 边界检查：清理只针对 updates 子目录，绝不波及同级 config / sdks / logs 等用户数据目录。
        var dataRoot = CreateTempDataRoot();
        var configDir = Path.Combine(dataRoot, "config");
        var sdksDir = Path.Combine(dataRoot, "sdks", "java", "jdk-21");
        var updatesDir = Path.Combine(dataRoot, "updates", "v0.2.5");

        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(sdksDir);
        Directory.CreateDirectory(updatesDir);
        File.WriteAllText(Path.Combine(configDir, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(sdksDir, "release"), "JAVA_VERSION=\"21\"");

        DataRootMaintenance.PurgeUpdatesStagingDirectory(dataRoot);

        Assert.True(File.Exists(Path.Combine(configDir, "settings.json")));
        Assert.True(File.Exists(Path.Combine(sdksDir, "release")));
        Assert.False(Directory.Exists(updatesDir));
    }

    [Fact]
    public void PurgeDirectoryContentsClearsFilesAndSubdirectoriesButKeepsRoot()
    {
        // 直接验证底层 PurgeDirectoryContents：与 PurgeUpdatesStagingDirectory 共用同一份逻辑。
        var dir = CreateTempDataRoot();
        File.WriteAllText(Path.Combine(dir, "a.txt"), "a");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "sub", "b.txt"), "b");

        DataRootMaintenance.PurgeDirectoryContents(dir);

        Assert.True(Directory.Exists(dir));
        Assert.Empty(Directory.GetFileSystemEntries(dir));
    }

    private static string CreateTempDataRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
