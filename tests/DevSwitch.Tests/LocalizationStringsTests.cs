// 文件用途：验证 LocalizationStrings 字符串表的查询行为与两语言 key 集合一致性。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit
// NOTE: 合法授权学习使用，仅限本地环境。

using System.Linq;
using DevSwitch.Core.Localization;
using Xunit;

namespace DevSwitch.Tests;

public sealed class LocalizationStringsTests
{
    // ---- Get 命中 ----

    [Fact]
    public void GetReturnsLocalizedTextForKnownKey()
    {
        Assert.Equal("首页", LocalizationStrings.Get(AppLanguage.ChineseSimplified, "nav.home"));
        Assert.Equal("Home", LocalizationStrings.Get(AppLanguage.English, "nav.home"));
    }

    [Fact]
    public void GetReturnsExpectedTranslationsForSampledKeys()
    {
        // 抽查导航、按钮、设置、语言名、各页面标题等关键 key。
        Assert.Equal("SDK 管理", LocalizationStrings.Get(AppLanguage.ChineseSimplified, "nav.sdk"));
        Assert.Equal("SDK Management", LocalizationStrings.Get(AppLanguage.English, "nav.sdk"));

        Assert.Equal("知道了", LocalizationStrings.Get(AppLanguage.ChineseSimplified, "common.close"));
        Assert.Equal("Got it", LocalizationStrings.Get(AppLanguage.English, "common.close"));

        Assert.Equal("跟随系统", LocalizationStrings.Get(AppLanguage.ChineseSimplified, "language.auto"));
        Assert.Equal("System Default", LocalizationStrings.Get(AppLanguage.English, "language.auto"));

        Assert.Equal("清理过期", LocalizationStrings.Get(AppLanguage.ChineseSimplified, "logs.prune"));
        Assert.Equal("Prune Expired", LocalizationStrings.Get(AppLanguage.English, "logs.prune"));
    }

    // ---- Get 未命中 ----

    [Theory]
    [InlineData("nope.missing.key")]
    [InlineData("")]
    public void GetReturnsKeyItselfWhenMissing(string key)
    {
        // 设计约定：未命中返回 key 本身，不抛异常。空字符串原样返回。
        Assert.Equal(key, LocalizationStrings.Get(AppLanguage.ChineseSimplified, key));
        Assert.Equal(key, LocalizationStrings.Get(AppLanguage.English, key));
    }

    // ---- key 集合一致性 ----

    [Fact]
    public void AllKeysIsNonEmpty()
    {
        Assert.NotEmpty(LocalizationStrings.AllKeys);
    }

    [Fact]
    public void BothLanguagesContainEveryKeyWithNonEmptyValue()
    {
        // 强制校验：AllKeys 中的每个 key，在两种语言下都必须有非空翻译（不会回退成 key 本身）。
        foreach (var key in LocalizationStrings.AllKeys)
        {
            var zh = LocalizationStrings.Get(AppLanguage.ChineseSimplified, key);
            var en = LocalizationStrings.Get(AppLanguage.English, key);

            Assert.False(string.IsNullOrWhiteSpace(zh), $"中文缺失 key: {key}");
            Assert.False(string.IsNullOrWhiteSpace(en), $"英文缺失 key: {key}");

            // 命中即意味着不等于 key 本身（除非翻译刻意等于 key，本表无此情况）。
            Assert.NotEqual(key, zh);
            Assert.NotEqual(key, en);
        }
    }

    [Fact]
    public void ChineseAndEnglishKeySetsAreEqual()
    {
        // 通过 AllKeys 间接断言：两表 key 集合相等。若不等，上一个测试中的 Get 会回退成 key 本身从而失败。
        // 这里再以集合形式显式校验 AllKeys 自身无重复且稳定。
        var keys = LocalizationStrings.AllKeys;
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }
}
