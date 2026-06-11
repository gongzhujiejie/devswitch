// 文件用途：验证 SemanticVersionComparer 的版本比较与降序排序行为。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit、DevSwitch.Sources
// NOTE: 合法授权学习使用，仅限本地环境。

using System.Linq;
using DevSwitch.Sources;
using Xunit;

namespace DevSwitch.Tests;

public sealed class SourcesSemanticVersionComparerTests
{
    private static readonly SemanticVersionComparer Cmp = SemanticVersionComparer.Instance;

    [Theory]
    [InlineData("1.2.0", "1.10.0")]   // 数值比较：2 < 10
    [InlineData("1.9.0", "1.10.0")]
    [InlineData("v9.0.0", "v10.0.0")] // 去前缀后数值比较
    [InlineData("go1.21.0", "go1.22.0")]
    [InlineData("21.0.4+7", "21.0.5+1")]
    public void CompareOrdersLowerVersionBeforeHigher(string lower, string higher)
    {
        Assert.True(Cmp.Compare(lower, higher) < 0);
        Assert.True(Cmp.Compare(higher, lower) > 0);
    }

    [Fact]
    public void CompareTreatsMissingTrailingSegmentsAsZero()
    {
        // "1.2" 等价于 "1.2.0"。
        Assert.Equal(0, Cmp.Compare("1.2", "1.2.0"));
    }

    [Fact]
    public void CompareRanksReleaseHigherThanPrerelease()
    {
        // 正式版 4.0.0 应大于预发布 4.0.0-rc-1。
        Assert.True(Cmp.Compare("4.0.0", "4.0.0-rc-1") > 0);
    }

    [Fact]
    public void CompareHandlesNullAndEmptyWithoutThrowing()
    {
        Assert.Equal(0, Cmp.Compare(null, null));
        Assert.Equal(0, Cmp.Compare("", ""));
        Assert.True(Cmp.Compare(null, "1.0.0") < 0);
        Assert.True(Cmp.Compare("1.0.0", null) > 0);
    }

    [Fact]
    public void OrderByDescendingProducesNewestFirst()
    {
        var input = new[] { "1.2.0", "1.10.0", "1.9.0", "2.0.0" };

        var sorted = input.OrderByDescending(v => v, Cmp).ToArray();

        Assert.Equal(new[] { "2.0.0", "1.10.0", "1.9.0", "1.2.0" }, sorted);
    }
}
