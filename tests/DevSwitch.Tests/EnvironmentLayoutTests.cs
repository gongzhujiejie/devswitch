// 文件用途：验证 DevSwitch 用户环境变量布局纯算法（默认变量集、托管 PATH 片段、PATH 合并/移除）。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit
// NOTE: 合法授权学习使用，仅限本地环境。本测试为纯函数单测，不触碰注册表或文件系统。

using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class EnvironmentLayoutTests
{
    [Fact]
    public void BuildShimsPathEntryReturnsSingleShimsDirectory()
    {
        const string dataRoot = @"C:\Users\dev\AppData\Local\DevSwitch";

        var shims = EnvironmentLayout.BuildShimsPathEntry(dataRoot);

        // shim 单目录方案：系统 PATH 只放这一条即可覆盖所有命令。
        Assert.Equal(@"C:\Users\dev\AppData\Local\DevSwitch\shims", shims);
    }

    [Fact]
    public void BuildShimsPathEntryRejectsEmptyDataRoot()
    {
        Assert.Throws<System.ArgumentException>(() => EnvironmentLayout.BuildShimsPathEntry("  "));
    }

    [Fact]
    public void BuildDefaultVariablesProducesDesignDocumentSet()
    {
        // 修复后：使用解析后的绝对 dataRoot，变量值为绝对路径（不再含 % 占位符），
        // 因为 Windows 环境变量展开只展开一层，嵌套 %DEVSWITCH_HOME% 无法生效。
        const string dataRoot = @"C:\Users\dev\AppData\Local\DevSwitch";

        var variables = EnvironmentLayout.BuildDefaultVariables(dataRoot);

        Assert.Collection(
            variables,
            v => AssertVariable(v, "DEVSWITCH_HOME", dataRoot),
            v => AssertVariable(v, "JAVA_HOME", @"C:\Users\dev\AppData\Local\DevSwitch\current\java"),
            v => AssertVariable(v, "MAVEN_HOME", @"C:\Users\dev\AppData\Local\DevSwitch\current\maven"),
            v => AssertVariable(v, "GOROOT", @"C:\Users\dev\AppData\Local\DevSwitch\current\go"));

        // 绝对路径不得残留任何嵌套占位符。
        Assert.All(variables.Skip(1), v => Assert.DoesNotContain("%", v.Value));
    }

    [Fact]
    public void BuildDefaultVariablesAddsCompatibilityVariablesWhenRequested()
    {
        const string dataRoot = @"C:\Users\dev\AppData\Local\DevSwitch";

        var variables = EnvironmentLayout.BuildDefaultVariables(
            dataRoot,
            new EnvironmentCompatibilityOptions(SetJdkHome: true, SetM2Home: true));

        Assert.Contains(variables, v => v is { Name: "JDK_HOME", Value: @"C:\Users\dev\AppData\Local\DevSwitch\current\java" });
        Assert.Contains(variables, v => v is { Name: "M2_HOME", Value: @"C:\Users\dev\AppData\Local\DevSwitch\current\maven" });
        // 默认 4 个 + 2 个兼容变量。
        Assert.Equal(6, variables.Count);
    }

    [Fact]
    public void BuildDefaultVariablesRejectsEmptyHome()
    {
        Assert.Throws<ArgumentException>(() => EnvironmentLayout.BuildDefaultVariables("  "));
    }

    [Fact]
    public void BuildManagedPathEntriesAbsoluteMatchesDesignDocumentOrder()
    {
        // 绝对路径版本：四个片段基于 dataRoot，顺序 java、maven、node、go。
        const string dataRoot = @"C:\Users\dev\AppData\Local\DevSwitch";

        var entries = EnvironmentLayout.BuildManagedPathEntries(dataRoot);

        Assert.Equal(
            new[]
            {
                @"C:\Users\dev\AppData\Local\DevSwitch\current\java\bin",
                @"C:\Users\dev\AppData\Local\DevSwitch\current\maven\bin",
                @"C:\Users\dev\AppData\Local\DevSwitch\current\node",
                @"C:\Users\dev\AppData\Local\DevSwitch\current\go\bin",
                @"C:\Users\dev\AppData\Local\DevSwitch\current\rust\bin",
            },
            entries);

        // 绝对路径片段不得残留占位符。
        Assert.All(entries, e => Assert.DoesNotContain("%", e));
    }

    [Fact]
    public void BuildManagedPathEntriesAbsoluteRejectsEmptyHome()
    {
        Assert.Throws<ArgumentException>(() => EnvironmentLayout.BuildManagedPathEntries("  "));
    }

    [Fact]
    public void BuildManagedPathEntriesLegacyPlaceholderMatchesDesignDocumentOrder()
    {
        // 遗留无参占位符版本仍保留（向后兼容 reset 移除链路），返回占位符形式。
        var entries = EnvironmentLayout.BuildManagedPathEntries();

        Assert.Equal(
            new[]
            {
                @"%DEVSWITCH_HOME%\current\java\bin",
                @"%DEVSWITCH_HOME%\current\maven\bin",
                @"%DEVSWITCH_HOME%\current\node",
                @"%DEVSWITCH_HOME%\current\go\bin",
                @"%DEVSWITCH_HOME%\current\rust\bin",
            },
            entries);
    }

    [Fact]
    public void MergeAppendsOnlyMissingEntriesAndPreservesUserOrder()
    {
        // 用户已有两个外部条目 + 已含一个托管片段。
        var existing = new[]
        {
            @"C:\Windows\System32",
            @"%DEVSWITCH_HOME%\current\java\bin",
            @"C:\Tools\bin",
        };
        var managed = EnvironmentLayout.BuildManagedPathEntries();

        var result = EnvironmentLayout.MergeManagedPathEntries(existing, managed);

        // 已存在的 java\bin 不重复添加；其余四个追加到末尾。
        Assert.True(result.Changed);
        Assert.Equal(
            new[]
            {
                @"%DEVSWITCH_HOME%\current\maven\bin",
                @"%DEVSWITCH_HOME%\current\node",
                @"%DEVSWITCH_HOME%\current\go\bin",
                @"%DEVSWITCH_HOME%\current\rust\bin",
            },
            result.Added);

        // 用户原有条目及顺序完整保留，新增项追加在末尾。
        Assert.Equal(
            new[]
            {
                @"C:\Windows\System32",
                @"%DEVSWITCH_HOME%\current\java\bin",
                @"C:\Tools\bin",
                @"%DEVSWITCH_HOME%\current\maven\bin",
                @"%DEVSWITCH_HOME%\current\node",
                @"%DEVSWITCH_HOME%\current\go\bin",
                @"%DEVSWITCH_HOME%\current\rust\bin",
            },
            result.Entries);
    }

    [Fact]
    public void MergeIsCaseInsensitiveAndIgnoresTrailingSeparators()
    {
        // 大小写不同、尾部带反斜杠的等价条目应被视为已存在，不重复添加。
        var existing = new[] { @"%devswitch_home%\CURRENT\Java\Bin\" };
        var managed = new[] { @"%DEVSWITCH_HOME%\current\java\bin" };

        var result = EnvironmentLayout.MergeManagedPathEntries(existing, managed);

        Assert.False(result.Changed);
        Assert.Empty(result.Added);
        Assert.Single(result.Entries);
    }

    [Fact]
    public void MergeNeverRemovesOrReordersUserEntries()
    {
        // 即便用户 PATH 顺序异常（托管片段在前、外部条目在后），合并也不重排已有条目。
        var existing = new[]
        {
            @"%DEVSWITCH_HOME%\current\node",
            @"C:\external\go\bin",
            @"C:\Windows",
        };
        var managed = EnvironmentLayout.BuildManagedPathEntries();

        var result = EnvironmentLayout.MergeManagedPathEntries(existing, managed);

        // 前三项原样保留。
        Assert.Equal(existing, result.Entries.Take(3).ToArray());
        // node 已存在不重复；C:\external\go\bin 与托管 go\bin 不同路径，仍需追加托管 go\bin。
        Assert.Contains(@"%DEVSWITCH_HOME%\current\go\bin", result.Added);
        Assert.DoesNotContain(@"%DEVSWITCH_HOME%\current\node", result.Added);
    }

    [Fact]
    public void MergeDeduplicatesRepeatedRequestedEntries()
    {
        var existing = Array.Empty<string>();
        var managed = new[]
        {
            @"%DEVSWITCH_HOME%\current\java\bin",
            @"%DEVSWITCH_HOME%\current\java\bin\",
        };

        var result = EnvironmentLayout.MergeManagedPathEntries(existing, managed);

        // 同一请求内的等价重复片段只追加一次。
        Assert.Single(result.Added);
    }

    [Fact]
    public void RemoveOnlyDeletesExactManagedEntriesAndKeepsOthers()
    {
        var existing = new[]
        {
            @"C:\Windows\System32",
            @"%DEVSWITCH_HOME%\current\java\bin",
            @"C:\external\jdk\bin",
            @"%DEVSWITCH_HOME%\current\go\bin\",
        };
        var managed = EnvironmentLayout.BuildManagedPathEntries();

        var result = EnvironmentLayout.RemoveManagedPathEntries(existing, managed);

        Assert.True(result.Changed);
        // 仅移除完全匹配的两个托管条目（含大小写/尾分隔符等价）。
        Assert.Equal(2, result.Removed.Count);
        Assert.Equal(
            new[]
            {
                @"C:\Windows\System32",
                @"C:\external\jdk\bin",
            },
            result.Entries);
    }

    [Fact]
    public void RemoveDoesNotTouchExternalEntriesThatAreNotManaged()
    {
        var existing = new[] { @"C:\external\go\bin", @"C:\Windows" };
        var managed = EnvironmentLayout.BuildManagedPathEntries();

        var result = EnvironmentLayout.RemoveManagedPathEntries(existing, managed);

        Assert.False(result.Changed);
        Assert.Equal(existing, result.Entries);
    }

    [Fact]
    public void PrependPlacesManagedEntriesFirstAndRemovesDuplicates()
    {
        // 用户已有外部条目，且托管 java\bin 残留在末尾（被其它项遮蔽的典型场景）。
        var existing = new[]
        {
            @"C:\Windows\System32",
            @"C:\external\jdk\bin",
            @"%DEVSWITCH_HOME%\current\java\bin",
        };
        var managed = EnvironmentLayout.BuildManagedPathEntries();

        var result = EnvironmentLayout.MergeManagedPathEntriesPrepend(existing, managed);

        Assert.True(result.Changed);
        // 托管片段（去重保序）排到最前，原末尾的 java\bin 被移除避免重复。
        Assert.Equal(
            new[]
            {
                @"%DEVSWITCH_HOME%\current\java\bin",
                @"%DEVSWITCH_HOME%\current\maven\bin",
                @"%DEVSWITCH_HOME%\current\node",
                @"%DEVSWITCH_HOME%\current\go\bin",
                @"%DEVSWITCH_HOME%\current\rust\bin",
            },
            result.Added);
        Assert.Equal(
            new[]
            {
                @"%DEVSWITCH_HOME%\current\java\bin",
                @"%DEVSWITCH_HOME%\current\maven\bin",
                @"%DEVSWITCH_HOME%\current\node",
                @"%DEVSWITCH_HOME%\current\go\bin",
                @"%DEVSWITCH_HOME%\current\rust\bin",
                @"C:\Windows\System32",
                @"C:\external\jdk\bin",
            },
            result.Entries);
    }

    [Fact]
    public void PrependPreservesOrderOfRemainingUserEntries()
    {
        // 非托管用户条目保序保留在托管片段之后。
        var existing = new[]
        {
            @"C:\a",
            @"C:\b",
            @"C:\c",
        };
        var managed = new[] { @"%DEVSWITCH_HOME%\current\java\bin" };

        var result = EnvironmentLayout.MergeManagedPathEntriesPrepend(existing, managed);

        Assert.Equal(
            new[]
            {
                @"%DEVSWITCH_HOME%\current\java\bin",
                @"C:\a",
                @"C:\b",
                @"C:\c",
            },
            result.Entries);
    }

    [Fact]
    public void PrependWithEmptyExistingReturnsManagedOnly()
    {
        var existing = Array.Empty<string>();
        var managed = EnvironmentLayout.BuildManagedPathEntries();

        var result = EnvironmentLayout.MergeManagedPathEntriesPrepend(existing, managed);

        Assert.Equal(managed, result.Entries);
        Assert.Equal(managed, result.Added);
    }

    [Fact]
    public void PrependMovesManagedEntryFromTailToFront()
    {
        // managed 已在 existing 末尾：被移到最前，不重复出现。
        var existing = new[]
        {
            @"C:\Windows",
            @"%devswitch_home%\CURRENT\Java\Bin\",
        };
        var managed = new[] { @"%DEVSWITCH_HOME%\current\java\bin" };

        var result = EnvironmentLayout.MergeManagedPathEntriesPrepend(existing, managed);

        // 大小写/尾分隔符等价的旧条目被移除，托管片段以请求形式置顶。
        Assert.Equal(
            new[]
            {
                @"%DEVSWITCH_HOME%\current\java\bin",
                @"C:\Windows",
            },
            result.Entries);
        Assert.Single(result.Added);
    }

    [Fact]
    public void PrependDeduplicatesRepeatedRequestedEntries()
    {
        // 同一请求内的等价重复片段只置顶一次。
        var existing = new[] { @"C:\Windows" };
        var managed = new[]
        {
            @"%DEVSWITCH_HOME%\current\java\bin",
            @"%DEVSWITCH_HOME%\current\java\bin\",
        };

        var result = EnvironmentLayout.MergeManagedPathEntriesPrepend(existing, managed);

        Assert.Single(result.Added);
        Assert.Equal(
            new[]
            {
                @"%DEVSWITCH_HOME%\current\java\bin",
                @"C:\Windows",
            },
            result.Entries);
    }

    [Fact]
    public void PrependPreservesEmptyUserEntries()
    {
        // 空条目（连续分号）不参与去重，原样保留在尾部。
        var existing = new[] { @"C:\Windows", string.Empty, @"C:\Tools" };
        var managed = new[] { @"%DEVSWITCH_HOME%\current\java\bin" };

        var result = EnvironmentLayout.MergeManagedPathEntriesPrepend(existing, managed);

        Assert.Equal(
            new[]
            {
                @"%DEVSWITCH_HOME%\current\java\bin",
                @"C:\Windows",
                string.Empty,
                @"C:\Tools",
            },
            result.Entries);
    }

    [Fact]
    public void SplitAndJoinRoundTripPreservesRawStructure()
    {
        const string raw = @"C:\Windows;;%DEVSWITCH_HOME%\current\java\bin";

        var parts = EnvironmentLayout.SplitPath(raw);
        var joined = EnvironmentLayout.JoinPath(parts);

        // 空条目（连续分号）也忠实保留，往返不丢失结构。
        Assert.Equal(3, parts.Count);
        Assert.Equal(raw, joined);
    }

    [Fact]
    public void SplitHandlesNullAndEmpty()
    {
        Assert.Empty(EnvironmentLayout.SplitPath(null));
        Assert.Empty(EnvironmentLayout.SplitPath(string.Empty));
    }

    private static void AssertVariable(EnvironmentVariable variable, string name, string value)
    {
        Assert.Equal(name, variable.Name);
        Assert.Equal(value, variable.Value);
    }
}
