// 文件用途：验证 SdkCategorySummaryBuilder 的纯逻辑聚合行为（空输入、活跃判定、串类隔离、固定顺序等）。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit。
// NOTE: 合法授权学习使用，仅限本地环境。纯内存测试，无任何 IO。

using System.Collections.Generic;
using System.Linq;
using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class SdkCategorySummaryBuilderTests
{
    // 固定期望顺序，多个用例复用。
    private static readonly string[] ExpectedOrder = { "Java", "Maven", "Node.js", "Go" };

    [Fact]
    public void EmptyInputProducesFourPlaceholderSummaries()
    {
        // 空输入：四类全部产出占位汇总。
        var result = SdkCategorySummaryBuilder.Build(System.Array.Empty<SdkSummaryInput>());

        Assert.Equal(4, result.Count);
        Assert.All(result, s =>
        {
            Assert.Equal(0, s.TotalCount);
            Assert.Equal("未设置", s.ActiveName);
            Assert.False(s.HasActive);
            Assert.Equal("尚未导入", s.SummaryLine);
        });
    }

    [Fact]
    public void NullInputIsTreatedAsEmpty()
    {
        // null 输入应等价于空集合，仍返回 4 条占位。
        var result = SdkCategorySummaryBuilder.Build(null!);

        Assert.Equal(4, result.Count);
        Assert.All(result, s => Assert.Equal("尚未导入", s.SummaryLine));
    }

    [Fact]
    public void CategoryWithActiveVersionReportsActiveName()
    {
        // 某类有多个版本且其中一个 "使用中"。
        var rows = new List<SdkSummaryInput>
        {
            new("Java", "Temurin 17", "可用"),
            new("Java", "Temurin 21", "使用中"),
            new("Java", "Temurin 11", "未验证"),
        };

        var result = SdkCategorySummaryBuilder.Build(rows);
        var java = result.Single(s => s.Category == "Java");

        Assert.Equal(3, java.TotalCount);
        Assert.True(java.HasActive);
        Assert.Equal("Temurin 21", java.ActiveName);
        Assert.Equal("共 3 个版本 · 使用中 Temurin 21", java.SummaryLine);
        Assert.Contains("使用中", java.SummaryLine);
    }

    [Fact]
    public void CategoryWithVersionsButNoActiveReportsUnset()
    {
        // 某类有版本但没有 "使用中"。
        var rows = new List<SdkSummaryInput>
        {
            new("Maven", "Maven 3.9.6", "可用"),
            new("Maven", "Maven 3.8.8", "未验证"),
        };

        var result = SdkCategorySummaryBuilder.Build(rows);
        var maven = result.Single(s => s.Category == "Maven");

        Assert.Equal(2, maven.TotalCount);
        Assert.False(maven.HasActive);
        Assert.Equal("未设置", maven.ActiveName);
        Assert.Equal("共 2 个版本 · 未设置使用版本", maven.SummaryLine);
        Assert.Contains("未设置使用版本", maven.SummaryLine);
    }

    [Fact]
    public void AlwaysReturnsFourCategoriesInFixedOrder()
    {
        // 即使只提供部分分类的数据，也始终按固定顺序返回 4 条。
        var rows = new List<SdkSummaryInput>
        {
            new("Go", "Go 1.22", "使用中"),
        };

        var result = SdkCategorySummaryBuilder.Build(rows);

        Assert.Equal(ExpectedOrder, result.Select(s => s.Category).ToArray());
    }

    [Fact]
    public void RowsDoNotLeakAcrossCategories()
    {
        // Java 的行不能计入 Maven，分类隔离。
        var rows = new List<SdkSummaryInput>
        {
            new("Java", "Temurin 21", "使用中"),
            new("Java", "Temurin 17", "可用"),
            new("Node.js", "Node 20", "使用中"),
        };

        var result = SdkCategorySummaryBuilder.Build(rows);

        var java = result.Single(s => s.Category == "Java");
        var maven = result.Single(s => s.Category == "Maven");
        var node = result.Single(s => s.Category == "Node.js");
        var go = result.Single(s => s.Category == "Go");

        Assert.Equal(2, java.TotalCount);
        Assert.Equal(0, maven.TotalCount);      // Maven 完全不受 Java/Node 影响。
        Assert.Equal("尚未导入", maven.SummaryLine);
        Assert.Equal(1, node.TotalCount);
        Assert.True(node.HasActive);
        Assert.Equal("Node 20", node.ActiveName);
        Assert.Equal(0, go.TotalCount);
    }

    [Fact]
    public void EverySummaryHasNonEmptyIconGlyph()
    {
        // 每条汇总都应带有非空 IconGlyph，保证卡片可渲染图标。
        var result = SdkCategorySummaryBuilder.Build(System.Array.Empty<SdkSummaryInput>());

        Assert.All(result, s => Assert.False(string.IsNullOrEmpty(s.IconGlyph)));
    }
}
