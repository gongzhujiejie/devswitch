// 文件用途：验证 SDK 列表"状态"筛选下拉对应的纯逻辑过滤行为。
// 创建/修改日期：2026-06-10
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit
// NOTE: 合法授权学习使用，仅限本地环境。
//       MainWindowViewModel 位于 net8.0-windows10 的 WinUI 工程中，本测试项目（net8.0）无法直接引用；
//       VM 内部的过滤等价于对 SdkVersionRow.Status 文案调用 SdkStatusFilterMatcher.Matches，
//       这里直接覆盖匹配器与文案契约，等价于覆盖 VM 的过滤决策。

using System.Collections.Generic;
using System.Linq;
using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

/// <summary>
/// 用一个轻量本地 record 模拟 VM 中 SdkVersionRow 的过滤所需字段（Id、Status）。
/// </summary>
public sealed class SdkStatusFilterTests
{
    /// <summary>
    /// 模拟 VM 行：仅暴露过滤需要的字段，独立于 WinUI 程序集。
    /// </summary>
    private sealed record FakeRow(string Id, string Status);

    /// <summary>
    /// 覆盖场景 a：过滤切到「不可用」时只剩 Status=="不可用" 的行。
    /// </summary>
    [Fact]
    public void FilterUnavailableKeepsOnlyUnavailableRows()
    {
        var rows = new List<FakeRow>
        {
            new("java-active", "使用中"),
            new("java-usable", "可用"),
            new("java-broken", "不可用"),
            new("java-fresh", "未验证"),
        };

        var visible = rows
            .Where(r => SdkStatusFilterMatcher.Matches(r.Status, SdkStatusFilter.Unavailable))
            .ToList();

        var only = Assert.Single(visible);
        Assert.Equal("java-broken", only.Id);
        Assert.Equal("不可用", only.Status);
    }

    /// <summary>
    /// 覆盖场景 b：一行从「可用」回写为「不可用」后，再以「可用」过滤时该行不应再出现；
    /// 同一份过滤切到「不可用」时该行立即出现。这等价于 VM ApplyCommandVerificationResult 后的视图变化。
    /// </summary>
    [Fact]
    public void RowFlippedToUnavailableLeavesAvailableFilter()
    {
        // 初始：3 行均为"可用"。"可用"过滤下应全部命中。
        var rows = new List<FakeRow>
        {
            new("a", "可用"),
            new("b", "可用"),
            new("c", "可用"),
        };

        var visibleBefore = rows
            .Where(r => SdkStatusFilterMatcher.Matches(r.Status, SdkStatusFilter.Available))
            .Select(r => r.Id)
            .ToList();
        Assert.Equal(new[] { "a", "b", "c" }, visibleBefore);

        // 模拟 VM 把 b 验证失败回写为"不可用"。
        int idx = rows.FindIndex(r => r.Id == "b");
        rows[idx] = rows[idx] with { Status = "不可用" };

        // "可用"过滤下 b 应消失。
        var visibleAfter = rows
            .Where(r => SdkStatusFilterMatcher.Matches(r.Status, SdkStatusFilter.Available))
            .Select(r => r.Id)
            .ToList();
        Assert.Equal(new[] { "a", "c" }, visibleAfter);

        // 切到"不可用"过滤下 b 应即时出现。
        var unavailable = rows
            .Where(r => SdkStatusFilterMatcher.Matches(r.Status, SdkStatusFilter.Unavailable))
            .Select(r => r.Id)
            .ToList();
        Assert.Equal(new[] { "b" }, unavailable);
    }

    /// <summary>
    /// 覆盖场景 c：默认「全部」过滤包含所有行（含使用中、可用、不可用、未验证、未知文案）。
    /// </summary>
    [Fact]
    public void FilterAllReturnsAllRowsRegardlessOfStatus()
    {
        var rows = new List<FakeRow>
        {
            new("active", "使用中"),
            new("usable", "可用"),
            new("broken", "不可用"),
            new("fresh", "未验证"),
            new("weird", "未知"),
            new("blank", string.Empty),
        };

        var visible = rows
            .Where(r => SdkStatusFilterMatcher.Matches(r.Status, SdkStatusFilter.All))
            .Select(r => r.Id)
            .ToList();

        Assert.Equal(new[] { "active", "usable", "broken", "fresh", "weird", "blank" }, visible);
    }

    /// <summary>
    /// 校验 ComboBox 中文文案到枚举的解析（与 XAML 中四项一致），未知值回退到 All。
    /// </summary>
    [Theory]
    [InlineData("全部", SdkStatusFilter.All)]
    [InlineData("使用中", SdkStatusFilter.InUse)]
    [InlineData("可用", SdkStatusFilter.Available)]
    [InlineData("不可用", SdkStatusFilter.Unavailable)]
    [InlineData("", SdkStatusFilter.All)]
    [InlineData(null, SdkStatusFilter.All)]
    [InlineData("乱填", SdkStatusFilter.All)]
    public void ParseFromComboBoxTextHandlesAllKnownLabelsAndFallback(string? text, SdkStatusFilter expected)
    {
        var actual = SdkStatusFilterMatcher.ParseFromComboBoxText(text);
        Assert.Equal(expected, actual);
    }
}
