// 文件用途：验证 DataRootBootstrap 读写 exe 同目录引导文件 dataroot.txt 的行为。
// 创建/修改日期：2026-06-10
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit
// NOTE: 合法授权学习使用，仅限本地环境。测试只使用系统临时目录。

using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class DataRootBootstrapTests
{
    [Fact]
    public void WriteThenReadReturnsSameCustomRoot()
    {
        // NOTE: 写入后应能原样读回自定义数据根。
        var appDirectory = CreateTemporaryDirectory();
        var customRoot = Path.Combine(CreateTemporaryDirectory(), "MyData");

        DataRootBootstrap.WriteCustomDataRoot(appDirectory, customRoot);
        var readBack = DataRootBootstrap.ReadCustomDataRoot(appDirectory);

        Assert.Equal(customRoot, readBack);
    }

    [Fact]
    public void ReadReturnsNullWhenBootstrapFileMissing()
    {
        // NOTE: 没有 dataroot.txt 时读回 null（使用默认）。
        var appDirectory = CreateTemporaryDirectory();

        Assert.Null(DataRootBootstrap.ReadCustomDataRoot(appDirectory));
    }

    [Fact]
    public void WriteNullClearsBootstrapFileAndReadReturnsNull()
    {
        // NOTE: 写入 null 表示清除，删除 dataroot.txt，回到默认。
        var appDirectory = CreateTemporaryDirectory();
        DataRootBootstrap.WriteCustomDataRoot(appDirectory, @"C:\SomeData");

        DataRootBootstrap.WriteCustomDataRoot(appDirectory, null);

        Assert.Null(DataRootBootstrap.ReadCustomDataRoot(appDirectory));
        Assert.False(File.Exists(Path.Combine(appDirectory, DataRootBootstrap.BootstrapFileName)));
    }

    [Fact]
    public void WriteEmptyStringClearsBootstrapFile()
    {
        // NOTE: 空字符串等同于清除。
        var appDirectory = CreateTemporaryDirectory();
        DataRootBootstrap.WriteCustomDataRoot(appDirectory, @"C:\SomeData");

        DataRootBootstrap.WriteCustomDataRoot(appDirectory, "   ");

        Assert.Null(DataRootBootstrap.ReadCustomDataRoot(appDirectory));
    }

    [Fact]
    public void ReadTrimsWhitespaceAndSurroundingQuotes()
    {
        // NOTE: 用户可能把带空格的路径用引号包起来，读取时应正确解析。
        var appDirectory = CreateTemporaryDirectory();
        var bootstrapPath = Path.Combine(appDirectory, DataRootBootstrap.BootstrapFileName);
        File.WriteAllText(bootstrapPath, "  \"D:\\Dev Data\\DevSwitch\"  \r\n");

        var result = DataRootBootstrap.ReadCustomDataRoot(appDirectory);

        Assert.Equal(@"D:\Dev Data\DevSwitch", result);
    }

    [Fact]
    public void ReadReturnsNullWhenContentIsBlank()
    {
        // NOTE: 文件存在但全是空白，视为无自定义路径。
        var appDirectory = CreateTemporaryDirectory();
        File.WriteAllText(Path.Combine(appDirectory, DataRootBootstrap.BootstrapFileName), "\r\n   \t");

        Assert.Null(DataRootBootstrap.ReadCustomDataRoot(appDirectory));
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
