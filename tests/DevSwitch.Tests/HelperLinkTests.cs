// 文件用途：通过真实 helper 进程验证 current link inspect/create/remove 的公开行为。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit、System.Text.Json
// NOTE: 合法授权学习使用，仅限本地环境。本测试只操作临时目录下的 junction/link。

using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class HelperLinkTests
{
    [Fact]
    public async Task InspectLinkReportsMissingForAbsentCurrentPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var currentPath = Path.Combine(HelperProcessTestSupport.CreateTemporaryDirectory(), "current", "java");
        var result = await InvokeLinkHelperAsync("inspect-missing", "inspectLink", new { currentPath });
        var response = result.DeserializeResponse();

        Assert.Equal(0, result.ExitCode);
        Assert.True(response.Success);
        Assert.NotNull(response.Details);
        Assert.False(response.Details.Value.GetProperty("exists").GetBoolean());
        Assert.Equal("missing", response.Details.Value.GetProperty("linkType").GetString());
        Assert.False(response.Details.Value.GetProperty("isReparsePoint").GetBoolean());
    }

    [Fact]
    public async Task InspectLinkReportsRealDirectoryAndDoesNotTreatItAsLink()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var currentPath = Path.Combine(HelperProcessTestSupport.CreateTemporaryDirectory(), "current", "java");
        Directory.CreateDirectory(currentPath);
        await File.WriteAllTextAsync(Path.Combine(currentPath, "sentinel.txt"), "keep");

        var result = await InvokeLinkHelperAsync("inspect-real-directory", "inspectLink", new { currentPath });
        var response = result.DeserializeResponse();

        Assert.Equal(0, result.ExitCode);
        Assert.True(response.Success);
        Assert.NotNull(response.Details);
        Assert.True(response.Details.Value.GetProperty("exists").GetBoolean());
        Assert.True(response.Details.Value.GetProperty("isDirectory").GetBoolean());
        Assert.False(response.Details.Value.GetProperty("isReparsePoint").GetBoolean());
        Assert.Equal("real-directory", response.Details.Value.GetProperty("linkType").GetString());
    }

    [Fact]
    public async Task CreateCurrentLinkCreatesJunctionByDefault()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = HelperProcessTestSupport.CreateTemporaryDirectory();
        var targetPath = Path.Combine(tempRoot, "sdks", "java", "jdk-21");
        var currentPath = Path.Combine(tempRoot, "current", "java");
        Directory.CreateDirectory(targetPath);
        await File.WriteAllTextAsync(Path.Combine(targetPath, "sentinel.txt"), "target");

        var result = await InvokeLinkHelperAsync("create-junction", "createCurrentLink", new { currentPath, targetPath, linkPreference = "junction-first" });
        var response = result.DeserializeResponse();
        var inspect = (await InvokeLinkHelperAsync("inspect-created", "inspectLink", new { currentPath })).DeserializeResponse();

        Assert.Equal(0, result.ExitCode);
        Assert.True(response.Success);
        Assert.Equal("junction", response.Details!.Value.GetProperty("linkType").GetString());
        Assert.True(File.Exists(Path.Combine(currentPath, "sentinel.txt")));
        Assert.True(inspect.Success);
        Assert.Equal("junction", inspect.Details!.Value.GetProperty("linkType").GetString());
        Assert.Equal(Path.GetFullPath(targetPath), Path.GetFullPath(inspect.Details.Value.GetProperty("targetPath").GetString()!));
    }

    [Fact]
    public async Task CreateCurrentLinkFailsWhenTargetDirectoryDoesNotExist()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = HelperProcessTestSupport.CreateTemporaryDirectory();
        var currentPath = Path.Combine(tempRoot, "current", "java");
        var targetPath = Path.Combine(tempRoot, "missing", "jdk");

        var result = await InvokeLinkHelperAsync("create-missing-target", "createCurrentLink", new { currentPath, targetPath });
        var response = result.DeserializeResponse();

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(response.Success);
        Assert.Equal("target-not-found", response.ErrorCode);
        Assert.False(Directory.Exists(currentPath));
    }

    [Fact]
    public async Task CreateCurrentLinkRefusesToOverwriteRealDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = HelperProcessTestSupport.CreateTemporaryDirectory();
        var currentPath = Path.Combine(tempRoot, "current", "java");
        var targetPath = Path.Combine(tempRoot, "sdks", "java", "jdk-21");
        Directory.CreateDirectory(currentPath);
        Directory.CreateDirectory(targetPath);
        await File.WriteAllTextAsync(Path.Combine(currentPath, "sentinel.txt"), "keep");

        var result = await InvokeLinkHelperAsync("create-over-real", "createCurrentLink", new { currentPath, targetPath });
        var response = result.DeserializeResponse();

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(response.Success);
        Assert.Equal("unsafe-existing-directory", response.ErrorCode);
        Assert.True(File.Exists(Path.Combine(currentPath, "sentinel.txt")));
    }

    [Fact]
    public async Task RemoveCurrentLinkRemovesJunctionButKeepsTargetDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = HelperProcessTestSupport.CreateTemporaryDirectory();
        var targetPath = Path.Combine(tempRoot, "sdks", "java", "jdk-21");
        var currentPath = Path.Combine(tempRoot, "current", "java");
        Directory.CreateDirectory(targetPath);
        await File.WriteAllTextAsync(Path.Combine(targetPath, "sentinel.txt"), "target");
        await InvokeLinkHelperAsync("create-before-remove", "createCurrentLink", new { currentPath, targetPath });

        var result = await InvokeLinkHelperAsync("remove-junction", "removeCurrentLink", new { currentPath });
        var response = result.DeserializeResponse();

        Assert.Equal(0, result.ExitCode);
        Assert.True(response.Success);
        Assert.Equal("junction", response.Details!.Value.GetProperty("linkType").GetString());
        Assert.True(response.Details.Value.GetProperty("removed").GetBoolean());
        Assert.False(Directory.Exists(currentPath));
        Assert.True(File.Exists(Path.Combine(targetPath, "sentinel.txt")));
    }

    [Fact]
    public async Task RemoveCurrentLinkRefusesRealDirectoryAndKeepsContents()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var currentPath = Path.Combine(HelperProcessTestSupport.CreateTemporaryDirectory(), "current", "java");
        Directory.CreateDirectory(currentPath);
        await File.WriteAllTextAsync(Path.Combine(currentPath, "sentinel.txt"), "keep");

        var result = await InvokeLinkHelperAsync("remove-real-directory", "removeCurrentLink", new { currentPath });
        var response = result.DeserializeResponse();

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(response.Success);
        Assert.Equal("unsafe-real-directory", response.ErrorCode);
        Assert.True(Directory.Exists(currentPath));
        Assert.True(File.Exists(Path.Combine(currentPath, "sentinel.txt")));
    }

    [Fact]
    public async Task RemoveCurrentLinkTreatsMissingAsNoop()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var currentPath = Path.Combine(HelperProcessTestSupport.CreateTemporaryDirectory(), "current", "java");

        var result = await InvokeLinkHelperAsync("remove-missing", "removeCurrentLink", new { currentPath });
        var response = result.DeserializeResponse();

        Assert.Equal(0, result.ExitCode);
        Assert.True(response.Success);
        Assert.NotNull(response.Details);
        Assert.False(response.Details.Value.GetProperty("removed").GetBoolean());
        Assert.Equal("missing", response.Details.Value.GetProperty("linkType").GetString());
    }

    private static Task<HelperProcessResult> InvokeLinkHelperAsync(string requestId, string operation, object payload)
    {
        return HelperProcessTestSupport.InvokeHelperAsync(new HelperRequest(requestId, operation, payload));
    }
}
