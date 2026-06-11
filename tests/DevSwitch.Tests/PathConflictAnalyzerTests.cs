// 文件用途：验证 PathConflictAnalyzer 的纯算法遮蔽检测在各种场景下的公开行为。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit
// NOTE: 合法授权学习使用，仅限本地环境。本测试不触碰真实文件系统，全部用内存数据驱动。

using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class PathConflictAnalyzerTests
{
    // DevSwitch 托管片段（模拟已展开 dataRoot）。
    private static readonly string[] ManagedSegments =
    {
        @"C:\Users\dev\AppData\Local\DevSwitch\current\java\bin",
        @"C:\Users\dev\AppData\Local\DevSwitch\current\maven\bin",
        @"C:\Users\dev\AppData\Local\DevSwitch\current\node",
        @"C:\Users\dev\AppData\Local\DevSwitch\current\go\bin",
    };

    [Fact]
    public void AnalyzeReturnsEmptyWhenNoExternalEntriesBeforeManaged()
    {
        // 托管片段排在最前，后面的外部条目不构成前序遮蔽。
        var pathEntries = new[]
        {
            ManagedSegments[0],
            ManagedSegments[2],
            @"C:\Program Files\Oracle\Java\javapath",
            @"C:\Windows\System32",
        };

        var report = PathConflictAnalyzer.Analyze(pathEntries, ManagedSegments);

        Assert.False(report.HasConflicts);
    }

    [Fact]
    public void AnalyzeDetectsOracleJavapathShadowingBeforeManaged()
    {
        // Oracle javapath 排在托管 java 片段之前，应被识别为 java 遮蔽，来源 oracle-javapath。
        var pathEntries = new[]
        {
            @"C:\Program Files\Common Files\Oracle\Java\javapath",
            ManagedSegments[0],
        };

        var report = PathConflictAnalyzer.Analyze(pathEntries, ManagedSegments);

        Assert.True(report.HasConflicts);
        var conflict = Assert.Single(report.Conflicts);
        Assert.Equal(SdkType.Java, conflict.SdkType);
        Assert.Equal("oracle-javapath", conflict.Source);
        Assert.Contains("java", conflict.Commands);
    }

    [Fact]
    public void AnalyzeDetectsExternalNodeAndGoShadowing()
    {
        var pathEntries = new[]
        {
            @"C:\Program Files\nodejs",
            @"C:\tools\go\bin",
            ManagedSegments[0],
            ManagedSegments[2],
            ManagedSegments[3],
        };

        var report = PathConflictAnalyzer.Analyze(pathEntries, ManagedSegments);

        Assert.True(report.HasConflicts);
        Assert.Contains(report.Conflicts, c => c.SdkType == SdkType.Node && c.Source == "external-node");
        Assert.Contains(report.Conflicts, c => c.SdkType == SdkType.Go && c.Source == "external-go");
    }

    [Fact]
    public void AnalyzeEffectivePathDetectsMachineJavaBeforeUserManaged()
    {
        // Windows 新进程有效 PATH 是 Machine 在前、User 在后；用户 PATH 置顶无法压过系统旧 JDK。
        var machinePath = new[]
        {
            @"D:\Programs\java\jdk\Java_8_win\bin",
            @"C:\Windows\System32",
        };
        var userPath = new[]
        {
            ManagedSegments[0],
            ManagedSegments[1],
        };

        var report = PathConflictAnalyzer.AnalyzeEffectivePath(machinePath, userPath, ManagedSegments);

        Assert.True(report.HasConflicts);
        var conflict = Assert.Single(report.Conflicts.Where(c => c.SdkType == SdkType.Java));
        Assert.Equal(PathConflictAnalyzer.MachineScope, conflict.Scope);
        Assert.Contains("Java_8_win", conflict.ShadowingEntry);
    }

    [Fact]
    public void AnalyzeIgnoresEntriesAfterManagedSegment()
    {
        // 外部 jdk 排在托管片段之后，不构成前序遮蔽。
        var pathEntries = new[]
        {
            ManagedSegments[0],
            @"D:\jdk-8\bin",
        };

        var report = PathConflictAnalyzer.Analyze(pathEntries, ManagedSegments);

        Assert.False(report.HasConflicts);
    }

    [Fact]
    public void AnalyzeTreatsAllMatchesAsConflictWhenNoManagedSegmentPresent()
    {
        // PATH 中完全没有托管片段时，所有命中条目都视为冲突（minManagedIndex = MaxValue）。
        var pathEntries = new[]
        {
            @"C:\Program Files\Common Files\Oracle\Java\javapath",
            @"C:\Program Files\nodejs",
        };

        var report = PathConflictAnalyzer.Analyze(pathEntries, ManagedSegments);

        Assert.True(report.HasConflicts);
        Assert.Equal(2, report.Conflicts.Count);
    }

    [Fact]
    public void AnalyzeUsesProbeWhenProvided()
    {
        // 提供探针时，按探针报告的实际命令文件判定，而不是路径名启发式。
        var externalDir = @"C:\custom\bin";
        var pathEntries = new[] { externalDir, ManagedSegments[0] };

        var report = PathConflictAnalyzer.Analyze(
            pathEntries,
            ManagedSegments,
            commandFilesProbe: dir => dir == externalDir
                ? new[] { "java", "javac" }
                : Array.Empty<string>());

        var conflict = Assert.Single(report.Conflicts);
        Assert.Equal(SdkType.Java, conflict.SdkType);
        Assert.Equal(new[] { "java", "javac" }, conflict.Commands);
    }

    [Fact]
    public void AnalyzeHandlesTrailingSeparatorWhenMatchingManagedSegment()
    {
        // 托管片段带尾部反斜杠，仍应被识别为同一片段，使其之前的外部条目成为前序冲突。
        var pathEntries = new[]
        {
            @"C:\Program Files\nodejs",
            ManagedSegments[2] + @"\",
        };

        var report = PathConflictAnalyzer.Analyze(pathEntries, ManagedSegments);

        Assert.True(report.HasConflicts);
        Assert.Contains(report.Conflicts, c => c.SdkType == SdkType.Node);
    }

    [Fact]
    public void AnalyzeReturnsEmptyForEmptyPath()
    {
        var report = PathConflictAnalyzer.Analyze(Array.Empty<string>(), ManagedSegments);

        Assert.False(report.HasConflicts);
        Assert.Same(PathConflictReport.Empty, report);
    }

    [Fact]
    public void BuildSuggestionMentionsManualCleanupAndDoesNotAutoFix()
    {
        var conflict = new PathConflict(
            SdkType.Java,
            @"C:\Program Files\Common Files\Oracle\Java\javapath",
            new[] { "java", "javac" },
            "oracle-javapath");

        var suggestion = PathConflictAnalyzer.BuildSuggestion(conflict);

        Assert.Contains("手动", suggestion);
        Assert.Contains("不会自动修改", suggestion);
        Assert.Contains("javapath", suggestion);
    }

    [Fact]
    public void BuildSuggestionForMachineConflictExplainsUserPathCannotOverride()
    {
        var conflict = new PathConflict(
            SdkType.Java,
            @"D:\Programs\java\jdk\Java_8_win\bin",
            new[] { "java", "javac" },
            "external-java",
            PathConflictAnalyzer.MachineScope);

        var suggestion = PathConflictAnalyzer.BuildSuggestion(conflict);

        Assert.Contains("系统 PATH", suggestion);
        Assert.Contains("用户 PATH", suggestion);
        Assert.Contains("无法覆盖", suggestion);
    }
}
