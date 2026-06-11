// 文件用途：验证 DevSwitch 数据根目录解析的公开行为。
// 创建日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit
// NOTE: 合法授权学习使用，仅限本地环境。本测试只使用临时目录和显式环境输入。

using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class DataRootResolverTests
{
    [Fact]
    public void ResolveDataRootUsesLocalAppDataWhenPortableDataDirectoryDoesNotExist()
    {
        // NOTE: 通过公开 DataRootResolver 接口验证用户可观察行为：没有便携 data 目录时使用默认本地数据目录。
        // 测试显式传入 localAppData，避免依赖当前机器真实环境变量，保证行为稳定。
        var appDirectory = CreateTemporaryDirectory();
        var localAppData = CreateTemporaryDirectory();

        var result = DataRootResolver.Resolve(appDirectory, localAppData);

        Assert.Equal(Path.Combine(localAppData, "DevSwitch"), result);
    }

    [Fact]
    public void ResolveDataRootUsesPortableDataDirectoryWhenItExistsNextToApp()
    {
        // NOTE: 便携模式是用户可观察行为：exe 同目录存在 data 时，不再使用 LOCALAPPDATA。
        var appDirectory = CreateTemporaryDirectory();
        var localAppData = CreateTemporaryDirectory();
        var portableDataDirectory = Path.Combine(appDirectory, "data");
        Directory.CreateDirectory(portableDataDirectory);

        var result = DataRootResolver.Resolve(appDirectory, localAppData);

        Assert.Equal(portableDataDirectory, result);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
