// 文件用途：验证 DataRootBootstrap 的「数据位置模式」配置读写（ReadConfig/WriteConfig）
//          以及与旧纯路径格式的向后兼容。
// 创建/修改日期：2026-06-12
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit
// NOTE: 合法授权学习使用，仅限本地环境。测试只使用系统临时目录与显式输入。

using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class DataLocationBootstrapTests
{
    [Fact]
    public void ReadConfigDefaultsToPortableWhenFileMissing()
    {
        // NOTE: 没有引导文件时，默认便携模式。
        var appDirectory = CreateTemporaryDirectory();

        var config = DataRootBootstrap.ReadConfig(appDirectory);

        Assert.Equal(DataLocationMode.Portable, config.Mode);
        Assert.Null(config.CustomPath);
    }

    [Fact]
    public void ReadConfigTreatsLegacyPlainPathAsCustom()
    {
        // NOTE: 旧格式（首行不是 mode= 的纯路径）应识别为 Custom 模式 + 该路径。
        var appDirectory = CreateTemporaryDirectory();
        File.WriteAllText(
            Path.Combine(appDirectory, DataRootBootstrap.BootstrapFileName),
            @"D:\Legacy\DevSwitch");

        var config = DataRootBootstrap.ReadConfig(appDirectory);

        Assert.Equal(DataLocationMode.Custom, config.Mode);
        Assert.Equal(@"D:\Legacy\DevSwitch", config.CustomPath);
    }

    [Fact]
    public void ReadConfigParsesLocalAppDataMode()
    {
        // NOTE: 新格式 mode=localappdata → 固定模式，无自定义路径。
        var appDirectory = CreateTemporaryDirectory();
        File.WriteAllText(
            Path.Combine(appDirectory, DataRootBootstrap.BootstrapFileName),
            "mode=localappdata\r\n");

        var config = DataRootBootstrap.ReadConfig(appDirectory);

        Assert.Equal(DataLocationMode.LocalAppData, config.Mode);
        Assert.Null(config.CustomPath);
    }

    [Fact]
    public void ReadConfigParsesCustomModeWithPath()
    {
        // NOTE: 新格式 mode=custom + path=... → 自定义模式 + 路径。
        var appDirectory = CreateTemporaryDirectory();
        File.WriteAllText(
            Path.Combine(appDirectory, DataRootBootstrap.BootstrapFileName),
            "mode=custom\r\npath=D:\\Dev\\Data\r\n");

        var config = DataRootBootstrap.ReadConfig(appDirectory);

        Assert.Equal(DataLocationMode.Custom, config.Mode);
        Assert.Equal(@"D:\Dev\Data", config.CustomPath);
    }

    [Fact]
    public void ReadConfigParsesPortableMode()
    {
        // NOTE: 新格式 mode=portable → 便携模式。
        var appDirectory = CreateTemporaryDirectory();
        File.WriteAllText(
            Path.Combine(appDirectory, DataRootBootstrap.BootstrapFileName),
            "mode=portable\r\n");

        var config = DataRootBootstrap.ReadConfig(appDirectory);

        Assert.Equal(DataLocationMode.Portable, config.Mode);
        Assert.Null(config.CustomPath);
    }

    [Fact]
    public void WriteThenReadLocalAppDataRoundTrips()
    {
        // NOTE: 写入固定模式后应原样读回 LocalAppData。
        var appDirectory = CreateTemporaryDirectory();

        DataRootBootstrap.WriteConfig(appDirectory, new DataLocationConfig(DataLocationMode.LocalAppData, null));
        var config = DataRootBootstrap.ReadConfig(appDirectory);

        Assert.Equal(DataLocationMode.LocalAppData, config.Mode);
        Assert.Null(config.CustomPath);
    }

    [Fact]
    public void WriteThenReadCustomRoundTrips()
    {
        // NOTE: 写入自定义模式 + 路径后应原样读回。
        var appDirectory = CreateTemporaryDirectory();
        var customPath = @"D:\Custom Data\DevSwitch";

        DataRootBootstrap.WriteConfig(appDirectory, new DataLocationConfig(DataLocationMode.Custom, customPath));
        var config = DataRootBootstrap.ReadConfig(appDirectory);

        Assert.Equal(DataLocationMode.Custom, config.Mode);
        Assert.Equal(customPath, config.CustomPath);
    }

    [Fact]
    public void WriteThenReadPortableRoundTrips()
    {
        // NOTE: 写入便携模式后应原样读回 Portable。
        var appDirectory = CreateTemporaryDirectory();

        DataRootBootstrap.WriteConfig(appDirectory, new DataLocationConfig(DataLocationMode.Portable, null));
        var config = DataRootBootstrap.ReadConfig(appDirectory);

        Assert.Equal(DataLocationMode.Portable, config.Mode);
        Assert.Null(config.CustomPath);
    }

    [Fact]
    public void WritePortableRemovesBootstrapFileSoLegacyConsumersSeeDefault()
    {
        // NOTE: 便携模式作为默认态，写入后引导文件应被清除（与 WriteCustomDataRoot(null) 语义一致）。
        var appDirectory = CreateTemporaryDirectory();
        DataRootBootstrap.WriteConfig(appDirectory, new DataLocationConfig(DataLocationMode.Custom, @"C:\X"));

        DataRootBootstrap.WriteConfig(appDirectory, DataLocationConfig.Portable);

        Assert.False(File.Exists(Path.Combine(appDirectory, DataRootBootstrap.BootstrapFileName)));
    }

    [Fact]
    public void ReadCustomDataRootReturnsPathOnlyInCustomMode()
    {
        // NOTE: 对外旧接口语义：仅 Custom 模式返回路径，其它模式返回 null。
        var appDirectory = CreateTemporaryDirectory();

        DataRootBootstrap.WriteConfig(appDirectory, new DataLocationConfig(DataLocationMode.LocalAppData, null));
        Assert.Null(DataRootBootstrap.ReadCustomDataRoot(appDirectory));

        DataRootBootstrap.WriteConfig(appDirectory, new DataLocationConfig(DataLocationMode.Custom, @"D:\C"));
        Assert.Equal(@"D:\C", DataRootBootstrap.ReadCustomDataRoot(appDirectory));
    }

    [Fact]
    public void WriteCustomDataRootNonNullEqualsCustomMode()
    {
        // NOTE: WriteCustomDataRoot(非空) 应等价于写入 Custom 模式。
        var appDirectory = CreateTemporaryDirectory();

        DataRootBootstrap.WriteCustomDataRoot(appDirectory, @"D:\Foo");
        var config = DataRootBootstrap.ReadConfig(appDirectory);

        Assert.Equal(DataLocationMode.Custom, config.Mode);
        Assert.Equal(@"D:\Foo", config.CustomPath);
    }

    [Fact]
    public void WriteCustomDataRootNullEqualsPortableMode()
    {
        // NOTE: WriteCustomDataRoot(null) 应等价于恢复 Portable 模式。
        var appDirectory = CreateTemporaryDirectory();
        DataRootBootstrap.WriteCustomDataRoot(appDirectory, @"D:\Foo");

        DataRootBootstrap.WriteCustomDataRoot(appDirectory, null);
        var config = DataRootBootstrap.ReadConfig(appDirectory);

        Assert.Equal(DataLocationMode.Portable, config.Mode);
        Assert.Null(config.CustomPath);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
