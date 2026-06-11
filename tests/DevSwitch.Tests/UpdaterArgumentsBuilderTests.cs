// 文件用途：验证 UpdaterArgumentsBuilder 的命令行参数拼接、引号包裹与参数校验逻辑。
// 创建/修改日期：2026-06-10
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit、DevSwitch.Core
// NOTE: 合法授权学习使用，仅限本地环境。纯逻辑测试，不启动任何进程。

using System;
using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class UpdaterArgumentsBuilderTests
{
    [Fact]
    public void Build_BasicArguments_AreOrderedAndQuoted()
    {
        // 基本拼接：四段固定顺序，路径均带引号，无 --log。
        var args = UpdaterArgumentsBuilder.Build(
            sourceDir: @"C:\src",
            targetDir: @"C:\target",
            exePath: @"C:\app\DevSwitch.exe",
            pid: 1234);

        Assert.Equal(
            "--source \"C:\\src\" --target \"C:\\target\" --exe \"C:\\app\\DevSwitch.exe\" --pid 1234",
            args);
    }

    [Fact]
    public void Build_PathsWithSpaces_AreWrappedInQuotes()
    {
        // 含空格路径必须被双引号包裹，作为单个参数传递。
        var args = UpdaterArgumentsBuilder.Build(
            sourceDir: @"C:\my src",
            targetDir: @"C:\Program Files\DevSwitch",
            exePath: @"C:\Program Files\DevSwitch\app.exe",
            pid: 42);

        Assert.Contains("--source \"C:\\my src\"", args);
        Assert.Contains("--target \"C:\\Program Files\\DevSwitch\"", args);
        Assert.Contains("--exe \"C:\\Program Files\\DevSwitch\\app.exe\"", args);
    }

    [Fact]
    public void Build_WithoutLogPath_OmitsLogFlag()
    {
        var args = UpdaterArgumentsBuilder.Build(@"C:\s", @"C:\t", @"C:\e.exe", 1);
        Assert.DoesNotContain("--log", args);
    }

    [Fact]
    public void Build_WithLogPath_IncludesQuotedLogFlag()
    {
        var args = UpdaterArgumentsBuilder.Build(
            @"C:\s", @"C:\t", @"C:\e.exe", 1, logPath: @"C:\logs\update log.txt");

        Assert.Contains("--log \"C:\\logs\\update log.txt\"", args);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Build_BlankLogPath_OmitsLogFlag(string? logPath)
    {
        // 空/空白 logPath 不应附加 --log。
        var args = UpdaterArgumentsBuilder.Build(@"C:\s", @"C:\t", @"C:\e.exe", 1, logPath);
        Assert.DoesNotContain("--log", args);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Build_NonPositivePid_Throws(int pid)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => UpdaterArgumentsBuilder.Build(@"C:\s", @"C:\t", @"C:\e.exe", pid));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Build_BlankSourceDir_Throws(string? sourceDir)
    {
        Assert.Throws<ArgumentException>(
            () => UpdaterArgumentsBuilder.Build(sourceDir!, @"C:\t", @"C:\e.exe", 1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Build_BlankTargetDir_Throws(string? targetDir)
    {
        Assert.Throws<ArgumentException>(
            () => UpdaterArgumentsBuilder.Build(@"C:\s", targetDir!, @"C:\e.exe", 1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Build_BlankExePath_Throws(string? exePath)
    {
        Assert.Throws<ArgumentException>(
            () => UpdaterArgumentsBuilder.Build(@"C:\s", @"C:\t", exePath!, 1));
    }
}
