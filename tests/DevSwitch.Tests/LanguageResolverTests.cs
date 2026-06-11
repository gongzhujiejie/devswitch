// 文件用途：验证 LanguageResolver 的语言标记解析与反向转换逻辑（纯函数，不触磁盘/系统）。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit
// NOTE: 合法授权学习使用，仅限本地环境。

using DevSwitch.Core.Localization;
using Xunit;

namespace DevSwitch.Tests;

public sealed class LanguageResolverTests
{
    // ---- 显式标记解析 ----

    [Theory]
    // 标准 zh-CN → 中文。
    [InlineData("zh-CN")]
    // 大小写容错。
    [InlineData("ZH-cn")]
    // 前后空格容错。
    [InlineData("  zh-CN  ")]
    public void ResolveReturnsChineseForChineseTag(string tag)
    {
        Assert.Equal(AppLanguage.ChineseSimplified, LanguageResolver.Resolve(tag, null));
    }

    [Theory]
    // 标准 en-US → 英文。
    [InlineData("en-US")]
    // 大小写容错。
    [InlineData("EN-us")]
    // 前后空格容错。
    [InlineData("\ten-US\n")]
    public void ResolveReturnsEnglishForEnglishTag(string tag)
    {
        Assert.Equal(AppLanguage.English, LanguageResolver.Resolve(tag, null));
    }

    // ---- auto + 系统区域解析 ----

    [Theory]
    // zh 系列均判中文。
    [InlineData("zh-CN")]
    [InlineData("zh-Hans-CN")]
    [InlineData("zh")]
    [InlineData("ZH-hant-TW")]
    public void ResolveAutoUsesSystemCultureForChinese(string culture)
    {
        Assert.Equal(AppLanguage.ChineseSimplified, LanguageResolver.Resolve("auto", culture));
    }

    [Theory]
    // 非 zh 区域判英文。
    [InlineData("en-US")]
    [InlineData("en-GB")]
    [InlineData("fr-FR")]
    [InlineData("ja-JP")]
    public void ResolveAutoUsesSystemCultureForEnglish(string culture)
    {
        Assert.Equal(AppLanguage.English, LanguageResolver.Resolve("auto", culture));
    }

    [Fact]
    public void ResolveAutoWithEmptyCultureFallsBackToChinese()
    {
        // auto 但系统区域缺失 → 回退中文默认。
        Assert.Equal(AppLanguage.ChineseSimplified, LanguageResolver.Resolve("auto", null));
        Assert.Equal(AppLanguage.ChineseSimplified, LanguageResolver.Resolve("AUTO", "  "));
    }

    // ---- 兜底 ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("klingon")]
    [InlineData("zh_CN")] // 下划线变体不被识别为显式标记，走兜底。
    public void ResolveUnknownOrNullFallsBackToChinese(string? tag)
    {
        Assert.Equal(AppLanguage.ChineseSimplified, LanguageResolver.Resolve(tag, null));
    }

    // ---- ToTag 往返 ----

    [Fact]
    public void ToTagProducesStandardTags()
    {
        Assert.Equal("zh-CN", LanguageResolver.ToTag(AppLanguage.ChineseSimplified));
        Assert.Equal("en-US", LanguageResolver.ToTag(AppLanguage.English));
    }

    [Theory]
    [InlineData(AppLanguage.ChineseSimplified)]
    [InlineData(AppLanguage.English)]
    public void ToTagThenResolveRoundTrips(AppLanguage language)
    {
        // 把语言转标记再解析回来，应得到同一个语言。
        var tag = LanguageResolver.ToTag(language);
        Assert.Equal(language, LanguageResolver.Resolve(tag, null));
    }
}
