// 文件用途：验证 DataRootResolver.ResolveByMode 的按模式解析行为
//          （便携/固定/自定义，及各自的回退规则）。
// 创建/修改日期：2026-06-12
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit
// NOTE: 合法授权学习使用，仅限本地环境。测试只使用系统临时目录与显式输入。

using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class DataLocationResolverTests
{
    [Fact]
    public void ResolveByModePortableUsesAppDataDirectory()
    {
        // NOTE: 便携模式且 appDirectory 可写 → appDirectory\data。
        var appDirectory = CreateTemporaryDirectory();
        var localAppData = CreateTemporaryDirectory();

        var result = DataRootResolver.ResolveByMode(DataLocationConfig.Portable, appDirectory, localAppData);

        Assert.Equal(Path.Combine(appDirectory, "data"), result);
    }

    [Fact]
    public void ResolveByModePortableFallsBackToLocalAppDataWhenNotWritable()
    {
        // NOTE: 便携模式但 appDirectory 不可写（用文件路径模拟）→ 回退 LOCALAPPDATA\DevSwitch。
        var parent = CreateTemporaryDirectory();
        var unwritableAppDirectory = Path.Combine(parent, "app.lock");
        File.WriteAllText(unwritableAppDirectory, "x");
        var localAppData = CreateTemporaryDirectory();

        var result = DataRootResolver.ResolveByMode(DataLocationConfig.Portable, unwritableAppDirectory, localAppData);

        Assert.Equal(Path.Combine(localAppData, "DevSwitch"), result);
    }

    [Fact]
    public void ResolveByModeLocalAppDataIsFixedRegardlessOfAppDirectory()
    {
        // NOTE: 固定模式始终返回 LOCALAPPDATA\DevSwitch，与 appDirectory 是否可写无关。
        var appDirectory = CreateTemporaryDirectory();
        var localAppData = CreateTemporaryDirectory();

        var result = DataRootResolver.ResolveByMode(
            new DataLocationConfig(DataLocationMode.LocalAppData, null), appDirectory, localAppData);

        Assert.Equal(Path.Combine(localAppData, "DevSwitch"), result);
    }

    [Fact]
    public void ResolveByModeCustomUsesCustomPath()
    {
        // NOTE: 自定义模式返回配置中的绝对路径。
        var appDirectory = CreateTemporaryDirectory();
        var localAppData = CreateTemporaryDirectory();
        var customPath = CreateTemporaryDirectory();

        var result = DataRootResolver.ResolveByMode(
            new DataLocationConfig(DataLocationMode.Custom, customPath), appDirectory, localAppData);

        Assert.Equal(customPath, result);
    }

    [Fact]
    public void ResolveByModeCustomWithEmptyPathFallsBackToPortableRule()
    {
        // NOTE: 自定义模式但路径为空 → 回退便携规则（appDirectory 可写时 data）。
        var appDirectory = CreateTemporaryDirectory();
        var localAppData = CreateTemporaryDirectory();

        var result = DataRootResolver.ResolveByMode(
            new DataLocationConfig(DataLocationMode.Custom, null), appDirectory, localAppData);

        Assert.Equal(Path.Combine(appDirectory, "data"), result);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
