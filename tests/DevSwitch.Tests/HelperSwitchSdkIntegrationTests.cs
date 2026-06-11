// 文件用途：通过真实 helper 进程验证 switchSdk current link 原子切换与回滚行为。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit
// NOTE: 合法授权学习使用，仅限本地环境。本测试只在临时目录中创建/删除 junction。

using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class HelperSwitchSdkIntegrationTests
{
    [Fact]
    public async Task SwitchSdkCreatesCurrentLinkWhenCurrentPathIsMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = HelperProcessTestSupport.CreateTemporaryDirectory();
        var currentPath = Path.Combine(tempRoot, "current", "java");
        var targetPath = Path.Combine(tempRoot, "sdks", "jdk-21");
        Directory.CreateDirectory(targetPath);
        await File.WriteAllTextAsync(Path.Combine(targetPath, "sentinel.txt"), "target");

        var result = await InvokeSwitchAsync("switch-missing", currentPath, targetPath);
        var response = result.DeserializeResponse();
        var inspect = (await InvokeHelperAsync("inspect-after-missing", "inspectLink", new { currentPath })).DeserializeResponse();

        Assert.Equal(0, result.ExitCode);
        Assert.True(response.Success);
        Assert.Equal("junction", inspect.Details!.Value.GetProperty("linkType").GetString());
        Assert.Equal(Path.GetFullPath(targetPath), Path.GetFullPath(inspect.Details.Value.GetProperty("targetPath").GetString()!));
        Assert.True(File.Exists(Path.Combine(currentPath, "sentinel.txt")));
    }

    [Fact]
    public async Task SwitchSdkHealsEmptyPlainCurrentDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = HelperProcessTestSupport.CreateTemporaryDirectory();
        var currentPath = Path.Combine(tempRoot, "current", "java");
        var targetPath = Path.Combine(tempRoot, "sdks", "jdk-21");
        Directory.CreateDirectory(currentPath);
        Directory.CreateDirectory(targetPath);
        await File.WriteAllTextAsync(Path.Combine(targetPath, "sentinel.txt"), "target");

        var result = await InvokeSwitchAsync("switch-heal-empty-real-dir", currentPath, targetPath);
        var response = result.DeserializeResponse();
        var inspect = (await InvokeHelperAsync("inspect-after-heal-empty", "inspectLink", new { currentPath })).DeserializeResponse();

        Assert.Equal(0, result.ExitCode);
        Assert.True(response.Success);
        Assert.Equal("junction", inspect.Details!.Value.GetProperty("linkType").GetString());
        Assert.Equal(Path.GetFullPath(targetPath), Path.GetFullPath(inspect.Details.Value.GetProperty("targetPath").GetString()!));
        Assert.True(File.Exists(Path.Combine(currentPath, "sentinel.txt")));
    }

    [Fact]
    public async Task SwitchSdkReplacesManagedCurrentLinkFromAToB()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = HelperProcessTestSupport.CreateTemporaryDirectory();
        var currentPath = Path.Combine(tempRoot, "current", "java");
        var targetA = Path.Combine(tempRoot, "sdks", "jdk-17");
        var targetB = Path.Combine(tempRoot, "sdks", "jdk-21");
        Directory.CreateDirectory(targetA);
        Directory.CreateDirectory(targetB);
        await File.WriteAllTextAsync(Path.Combine(targetA, "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(targetB, "b.txt"), "b");
        await InvokeHelperAsync("create-a", "createCurrentLink", new { currentPath, targetPath = targetA });

        var result = await InvokeSwitchAsync("switch-a-to-b", currentPath, targetB);
        var response = result.DeserializeResponse();
        var inspect = (await InvokeHelperAsync("inspect-after-b", "inspectLink", new { currentPath })).DeserializeResponse();

        Assert.Equal(0, result.ExitCode);
        Assert.True(response.Success);
        Assert.Equal(Path.GetFullPath(targetB), Path.GetFullPath(inspect.Details!.Value.GetProperty("targetPath").GetString()!));
        Assert.True(File.Exists(Path.Combine(targetA, "a.txt")));
        Assert.True(File.Exists(Path.Combine(targetB, "b.txt")));
        Assert.True(File.Exists(Path.Combine(currentPath, "b.txt")));
    }

    [Fact]
    public async Task SwitchSdkKeepsPreviousCurrentLinkWhenTargetPathIsInvalid()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = HelperProcessTestSupport.CreateTemporaryDirectory();
        var currentPath = Path.Combine(tempRoot, "current", "java");
        var targetA = Path.Combine(tempRoot, "sdks", "jdk-17");
        var missingTarget = Path.Combine(tempRoot, "sdks", "missing-jdk");
        Directory.CreateDirectory(targetA);
        await File.WriteAllTextAsync(Path.Combine(targetA, "a.txt"), "a");
        await InvokeHelperAsync("create-a-invalid-target", "createCurrentLink", new { currentPath, targetPath = targetA });

        var result = await InvokeSwitchAsync("switch-invalid-target", currentPath, missingTarget);
        var response = result.DeserializeResponse();
        var inspect = (await InvokeHelperAsync("inspect-after-invalid", "inspectLink", new { currentPath })).DeserializeResponse();

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(response.Success);
        Assert.Equal("target-path-missing", response.ErrorCode);
        Assert.Equal(Path.GetFullPath(targetA), Path.GetFullPath(inspect.Details!.Value.GetProperty("targetPath").GetString()!));
        Assert.True(File.Exists(Path.Combine(currentPath, "a.txt")));
    }

    [Fact]
    public async Task SwitchSdkRefusesToDeletePlainCurrentDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = HelperProcessTestSupport.CreateTemporaryDirectory();
        var currentPath = Path.Combine(tempRoot, "current", "java");
        var targetB = Path.Combine(tempRoot, "sdks", "jdk-21");
        Directory.CreateDirectory(currentPath);
        Directory.CreateDirectory(targetB);
        await File.WriteAllTextAsync(Path.Combine(currentPath, "ordinary.txt"), "keep");

        var result = await InvokeSwitchAsync("switch-refuse-real-dir", currentPath, targetB);
        var response = result.DeserializeResponse();

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(response.Success);
        Assert.Equal("current-path-not-managed-link", response.ErrorCode);
        Assert.True(Directory.Exists(currentPath));
        Assert.True(File.Exists(Path.Combine(currentPath, "ordinary.txt")));
    }

    private static Task<HelperProcessResult> InvokeSwitchAsync(string requestId, string currentPath, string targetPath)
    {
        return InvokeHelperAsync(requestId, "switchSdk", new { sdkType = "java", currentPath, targetPath, linkPreference = "junction-first" });
    }

    private static Task<HelperProcessResult> InvokeHelperAsync(string requestId, string operation, object payload)
    {
        return HelperProcessTestSupport.InvokeHelperAsync(new HelperRequest(requestId, operation, payload));
    }
}
