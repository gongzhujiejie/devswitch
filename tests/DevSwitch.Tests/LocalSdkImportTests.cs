// 文件用途：验证本地 SDK 导入服务的公开行为。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit
// NOTE: 合法授权学习使用，仅限本地环境。本测试只构造临时 SDK 目录，不访问真实系统 SDK。

using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class LocalSdkImportTests
{
    [Fact]
    public async Task ImportLocalJavaBinDirectoryRegistersParentRootAsExternalSdk()
    {
        // NOTE: 产品需求要求误选 JDK bin 时提示/使用父目录；这里通过导入结果和 sdks.json 记录验证公开行为。
        var dataRoot = CreateTemporaryDirectory();
        var jdkRoot = CreateTemporaryDirectory();
        var binPath = Path.Combine(jdkRoot, "bin");
        Directory.CreateDirectory(binPath);
        await File.WriteAllTextAsync(Path.Combine(jdkRoot, "release"), "JAVA_VERSION=\"21\"");
        await File.WriteAllTextAsync(Path.Combine(binPath, "java.exe"), string.Empty);
        await File.WriteAllTextAsync(Path.Combine(binPath, "javac.exe"), string.Empty);
        var service = new LocalSdkImportService(dataRoot);

        var result = await service.ImportLocalAsync(binPath, customName: "本地 JDK 21");

        Assert.True(result.Success);
        Assert.NotNull(result.Record);
        Assert.Equal(SdkType.Java, result.Record.Type);
        Assert.Equal(SdkSourceKind.External, result.Record.Source);
        Assert.Equal(SdkRecordStatus.Usable, result.Record.Status);
        Assert.Equal(jdkRoot, result.Record.Path);
        Assert.Equal("本地 JDK 21", result.Record.Name);

        var catalog = await new SdkCatalogStore().LoadOrCreateAsync(dataRoot);
        var stored = Assert.Single(catalog.Items);
        Assert.Equal(result.Record, stored);
    }

    [Fact]
    public async Task ImportLocalJavaResolvesVersionFromReleaseFileInsteadOfUnknown()
    {
        // NOTE: 回归原 bug——导入后 Version 不再硬编码 "unknown"，而是从 release 文件解析真实版本。
        var dataRoot = CreateTemporaryDirectory();
        var jdkRoot = CreateTemporaryDirectory();
        var binPath = Path.Combine(jdkRoot, "bin");
        Directory.CreateDirectory(binPath);
        await File.WriteAllTextAsync(
            Path.Combine(jdkRoot, "release"),
            "JAVA_VERSION=\"17.0.11\"\nOS_NAME=\"Windows\"");
        await File.WriteAllTextAsync(Path.Combine(binPath, "java.exe"), string.Empty);
        await File.WriteAllTextAsync(Path.Combine(binPath, "javac.exe"), string.Empty);
        var service = new LocalSdkImportService(dataRoot);

        var result = await service.ImportLocalAsync(jdkRoot);

        Assert.True(result.Success);
        Assert.NotNull(result.Record);
        Assert.Equal("17.0.11", result.Record.Version);
        Assert.NotEqual("unknown", result.Record.Version);
        // 无自定义名称时附加版本号，便于 UI 区分多个同类型 SDK。
        Assert.Equal("Java 17.0.11", result.Record.Name);
    }

    [Fact]
    public async Task ImportLocalNodeRootRegistersUsableExternalSdk()
    {
        // NOTE: Node.js Windows zip 根目录直接包含 node.exe、npm.cmd、npx.cmd，导入后应登记为外部可用 SDK。
        var dataRoot = CreateTemporaryDirectory();
        var nodeRoot = CreateTemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(nodeRoot, "node.exe"), string.Empty);
        await File.WriteAllTextAsync(Path.Combine(nodeRoot, "npm.cmd"), string.Empty);
        await File.WriteAllTextAsync(Path.Combine(nodeRoot, "npx.cmd"), string.Empty);
        var service = new LocalSdkImportService(dataRoot);

        var result = await service.ImportLocalAsync(nodeRoot);

        Assert.True(result.Success);
        Assert.NotNull(result.Record);
        Assert.Equal(SdkType.Node, result.Record.Type);
        Assert.Equal("Node.js", result.Record.Name);
        Assert.Equal(SdkArchitecture.Unknown, result.Record.Architecture);
        Assert.Equal(nodeRoot, result.Record.Path);
    }

    [Fact]
    public async Task ImportUnsupportedDirectoryReturnsUnavailableResultWithoutPersistingRecord()
    {
        // NOTE: 用户选到不支持目录时应得到可解释结果，而不是把无效路径写入 SDK 目录污染 UI。
        var dataRoot = CreateTemporaryDirectory();
        var unknownRoot = CreateTemporaryDirectory();
        var service = new LocalSdkImportService(dataRoot);

        var result = await service.ImportLocalAsync(unknownRoot);

        Assert.False(result.Success);
        Assert.Null(result.Record);
        Assert.Equal(SdkType.Unknown, result.Detection.Type);
        Assert.Equal(SdkStatus.Unavailable, result.Detection.Status);

        var catalog = await new SdkCatalogStore().LoadOrCreateAsync(dataRoot);
        Assert.Empty(catalog.Items);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
