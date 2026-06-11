// 文件用途：验证 DataRootResolver.ResolveEffective 的引导/便携/兜底解析行为。
// 创建/修改日期：2026-06-10
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit
// NOTE: 合法授权学习使用，仅限本地环境。测试只使用系统临时目录与显式输入，不触碰真实用户环境。

using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class DataRootResolverEffectiveTests
{
    [Fact]
    public void ResolveEffectiveDefaultsToPortableDataWhenAppDirectoryWritableAndNoBootstrap()
    {
        // NOTE: 默认绿色便携行为：appDirectory 可写且无 dataroot.txt 时，
        // 即使 data 目录尚未创建，也应返回 appDirectory\data（由上层负责创建）。
        var appDirectory = CreateTemporaryDirectory();
        var localAppData = CreateTemporaryDirectory();

        var result = DataRootResolver.ResolveEffective(appDirectory, localAppData);

        Assert.Equal(Path.Combine(appDirectory, "data"), result);
        Assert.False(Directory.Exists(result)); // 解析器不负责创建目录
    }

    [Fact]
    public void ResolveEffectiveUsesCustomRootFromBootstrapFile()
    {
        // NOTE: dataroot.txt 指定了有效自定义路径时优先采用，覆盖默认 data 目录。
        var appDirectory = CreateTemporaryDirectory();
        var localAppData = CreateTemporaryDirectory();
        var customRoot = CreateTemporaryDirectory();
        DataRootBootstrap.WriteCustomDataRoot(appDirectory, customRoot);

        var result = DataRootResolver.ResolveEffective(appDirectory, localAppData);

        Assert.Equal(customRoot, result);
    }

    [Fact]
    public void ResolveEffectiveFallsBackToLocalAppDataWhenAppDirectoryNotWritable()
    {
        // NOTE: appDirectory 不可写（这里用“指向一个已存在文件的路径”模拟）时，
        // 退回 LOCALAPPDATA\DevSwitch 作为 C 盘兜底。
        var parent = CreateTemporaryDirectory();
        var unwritableAppDirectory = Path.Combine(parent, "app.lock");
        File.WriteAllText(unwritableAppDirectory, "x"); // 这是文件而非目录，无法在其“内部”创建探测文件
        var localAppData = CreateTemporaryDirectory();

        var result = DataRootResolver.ResolveEffective(unwritableAppDirectory, localAppData);

        Assert.Equal(Path.Combine(localAppData, "DevSwitch"), result);
    }

    [Fact]
    public void ResolveEffectiveIgnoresEmptyBootstrapAndUsesPortableDefault()
    {
        // NOTE: dataroot.txt 存在但内容为空白时视为无效，回到默认便携 data 目录。
        var appDirectory = CreateTemporaryDirectory();
        var localAppData = CreateTemporaryDirectory();
        File.WriteAllText(Path.Combine(appDirectory, DataRootBootstrap.BootstrapFileName), "   \r\n");

        var result = DataRootResolver.ResolveEffective(appDirectory, localAppData);

        Assert.Equal(Path.Combine(appDirectory, "data"), result);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
