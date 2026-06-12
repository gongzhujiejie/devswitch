// 文件用途：验证 SDK 所在位置资源管理器参数构造逻辑。
// 创建/修改日期：2026-06-12
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit、DevSwitch.Core
// NOTE: 合法授权学习使用，仅限本地环境。本测试不启动 explorer.exe。

using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class SdkLocationExplorerTests
{
    [Fact]
    public void TryCreateSelectArgumentsReturnsExplorerSelectArgumentForExistingDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"), "nodejs");
        Directory.CreateDirectory(root);

        var result = SdkLocationExplorer.TryCreateSelectArguments(root, out var fullPath, out var arguments, out var errorMessage);

        Assert.True(result);
        Assert.Equal(Path.GetFullPath(root), fullPath);
        Assert.Equal($"/select,\"{Path.GetFullPath(root)}\"", arguments);
        Assert.Null(errorMessage);
    }

    [Fact]
    public void TryCreateSelectArgumentsRejectsMissingDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"), "missing");

        var result = SdkLocationExplorer.TryCreateSelectArguments(root, out var fullPath, out var arguments, out var errorMessage);

        Assert.False(result);
        Assert.Equal(Path.GetFullPath(root), fullPath);
        Assert.Null(arguments);
        Assert.NotNull(errorMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryCreateSelectArgumentsRejectsEmptyPath(string? path)
    {
        var result = SdkLocationExplorer.TryCreateSelectArguments(path, out var fullPath, out var arguments, out var errorMessage);

        Assert.False(result);
        Assert.Null(fullPath);
        Assert.Null(arguments);
        Assert.NotNull(errorMessage);
    }
}
