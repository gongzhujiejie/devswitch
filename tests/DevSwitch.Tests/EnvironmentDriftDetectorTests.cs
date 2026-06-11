// 文件用途：验证 EnvironmentDriftDetector 的环境位置漂移判定与路径规范化行为。
// 创建日期：2026-06-10
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit
// NOTE: 合法授权学习使用，仅限本地环境。测试只使用显式字符串输入，不读真实注册表。

using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class EnvironmentDriftDetectorTests
{
    // ---------- Detect：未初始化场景 ----------

    [Fact]
    public void DetectReturnsNotInitializedWhenRegisteredHomeIsNull()
    {
        // 从未设置 DEVSWITCH_HOME：不算漂移，标记未初始化，RegisteredHome 为 null。
        var result = EnvironmentDriftDetector.Detect(@"C:\Tools\DevSwitch\data", null);

        Assert.False(result.IsInitialized);
        Assert.False(result.HasDrift);
        Assert.Null(result.RegisteredHome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void DetectTreatsBlankRegisteredHomeAsNotInitialized(string registered)
    {
        // 空字符串/空白同样视为“从未初始化”，与 null 行为一致。
        var result = EnvironmentDriftDetector.Detect(@"C:\Tools\DevSwitch\data", registered);

        Assert.False(result.IsInitialized);
        Assert.False(result.HasDrift);
        Assert.Null(result.RegisteredHome);
    }

    // ---------- Detect：一致场景（无漂移） ----------

    [Fact]
    public void DetectReportsNoDriftWhenPathsAreIdentical()
    {
        var path = @"I:\Tools\DevSwitch\data";

        var result = EnvironmentDriftDetector.Detect(path, path);

        Assert.True(result.IsInitialized);
        Assert.False(result.HasDrift);
    }

    [Fact]
    public void DetectReportsNoDriftWhenOnlyCaseDiffers()
    {
        // Windows 路径大小写不敏感：仅大小写不同应视为一致。
        var result = EnvironmentDriftDetector.Detect(@"I:\Tools\DevSwitch\data", @"i:\tools\devswitch\DATA");

        Assert.True(result.IsInitialized);
        Assert.False(result.HasDrift);
    }

    [Fact]
    public void DetectReportsNoDriftWhenOnlyTrailingSeparatorDiffers()
    {
        // 尾部分隔符差异不应被判为漂移。
        var result = EnvironmentDriftDetector.Detect(@"I:\Tools\DevSwitch\data", @"I:\Tools\DevSwitch\data\");

        Assert.True(result.IsInitialized);
        Assert.False(result.HasDrift);
    }

    [Fact]
    public void DetectReportsNoDriftWhenRegisteredHomeContainsDotSegments()
    {
        // 含 . / .. 的等价路径规范化后应一致。
        var result = EnvironmentDriftDetector.Detect(
            @"I:\Tools\DevSwitch\data",
            @"I:\Tools\Other\..\DevSwitch\.\data");

        Assert.True(result.IsInitialized);
        Assert.False(result.HasDrift);
    }

    // ---------- Detect：漂移场景 ----------

    [Fact]
    public void DetectReportsDriftWhenRegisteredHomePointsElsewhere()
    {
        // 工具目录从 I:\old 移到 I:\new，注册值仍指向旧路径 → 漂移。
        var current = @"I:\new\DevSwitch\data";
        var registered = @"I:\old\DevSwitch\data";

        var result = EnvironmentDriftDetector.Detect(current, registered);

        Assert.True(result.IsInitialized);
        Assert.True(result.HasDrift);
        // 结果须携带规范化后的当前数据根，供上层重写 DEVSWITCH_HOME。
        Assert.Equal(EnvironmentDriftDetector.Normalize(current), result.CurrentDataRoot);
        Assert.Equal(EnvironmentDriftDetector.Normalize(registered), result.RegisteredHome);
    }

    // ---------- Detect：健壮性 ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DetectThrowsWhenCurrentDataRootIsBlank(string? current)
    {
        Assert.Throws<ArgumentException>(() => EnvironmentDriftDetector.Detect(current!, @"C:\anything"));
    }

    [Fact]
    public void DetectDoesNotThrowOnMalformedRegisteredHome()
    {
        // 异常路径（非法字符）不应抛出；应尽量判定为漂移。
        var result = EnvironmentDriftDetector.Detect(@"I:\Tools\DevSwitch\data", "C:\\bad\u0000path");

        Assert.True(result.IsInitialized);
        Assert.True(result.HasDrift);
    }

    // ---------- Normalize 纯函数 ----------

    [Fact]
    public void NormalizeRemovesTrailingSeparator()
    {
        var normalized = EnvironmentDriftDetector.Normalize(@"C:\a\b\");

        Assert.Equal(@"C:\a\b", normalized);
    }

    [Fact]
    public void NormalizeResolvesDotAndDotDotSegments()
    {
        var normalized = EnvironmentDriftDetector.Normalize(@"C:\a\x\..\b\.\c");

        Assert.Equal(@"C:\a\b\c", normalized);
    }

    [Fact]
    public void NormalizeProducesCaseInsensitiveEquivalentPaths()
    {
        // Normalize 不改写大小写，但配合 OrdinalIgnoreCase 比较应等价。
        var a = EnvironmentDriftDetector.Normalize(@"C:\Tools\Data");
        var b = EnvironmentDriftDetector.Normalize(@"c:\tools\data");

        Assert.Equal(a, b, ignoreCase: true);
    }

    [Fact]
    public void NormalizeKeepsDriveRootSeparator()
    {
        // 退化为盘符时应补回根分隔符。
        var normalized = EnvironmentDriftDetector.Normalize(@"C:\");

        Assert.Equal(@"C:\", normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeThrowsOnBlankInput(string? input)
    {
        Assert.Throws<ArgumentException>(() => EnvironmentDriftDetector.Normalize(input!));
    }
}
