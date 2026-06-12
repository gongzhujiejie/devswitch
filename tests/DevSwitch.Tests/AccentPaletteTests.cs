// 文件用途：验证 DevSwitch 强调色调色板 AccentPalette 的定义完整性与容错解析行为。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit
// NOTE: 合法授权学习使用，仅限本地环境。本测试为纯逻辑断言，不触碰文件系统或环境。

using System.Linq;
using System.Text.RegularExpressions;
using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class AccentPaletteTests
{
    // # + 恰好 6 位十六进制，大小写均可。
    private static readonly Regex HexPattern = new("^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled);

    [Fact]
    public void AllContainsSixOptionsWithUniqueKeys()
    {
        // 调色板必须恰好 6 套，且 key 不重复，否则设置页色块会出现重复/缺失。
        Assert.Equal(6, AccentPalette.All.Count);

        var keys = AccentPalette.All.Select(o => o.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void AllContainsExpectedKeys()
    {
        // 锁定 6 个约定 key，主代理 UI 依赖这些标识。
        var expected = new[] { "azure", "violet", "emerald", "amber", "rose", "sky" };
        var actual = AccentPalette.All.Select(o => o.Key);
        Assert.Equal(expected.OrderBy(k => k), actual.OrderBy(k => k));
    }

    [Fact]
    public void ResolveVioletReturnsCorrectOptionAndHex()
    {
        var option = AccentPalette.Resolve("violet");

        Assert.Equal("violet", option.Key);
        Assert.Equal("紫罗兰", option.DisplayNameZh);
        Assert.Equal("Violet", option.DisplayNameEn);
        Assert.Equal("#7C3AED", option.Accent);
        Assert.Equal("#6D28D9", option.AccentText);
        Assert.Equal("#F5F3FF", option.AccentSubtle);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonexistent")]
    public void ResolveUnknownOrEmptyFallsBackToAzure(string? key)
    {
        // 容错：null/空/未知一律回退默认 azure，且主色保持 #2563EB（老用户视觉不变）。
        var option = AccentPalette.Resolve(key);

        Assert.Equal("azure", option.Key);
        Assert.Equal(AccentPalette.DefaultKey, option.Key);
        Assert.Equal("#2563EB", option.Accent);
    }

    [Fact]
    public void ResolveIsCaseInsensitive()
    {
        // 大小写漂移的持久化值仍应命中对应色，而非回退默认。
        var option = AccentPalette.Resolve("EMERALD");
        Assert.Equal("emerald", option.Key);
    }

    [Fact]
    public void AllHexValuesAreWellFormed()
    {
        // 三档颜色都必须是合法的 #RRGGBB，避免运行时换色解析失败。
        foreach (var option in AccentPalette.All)
        {
            Assert.Matches(HexPattern, option.Accent);
            Assert.Matches(HexPattern, option.AccentText);
            Assert.Matches(HexPattern, option.AccentSubtle);
        }
    }
}
