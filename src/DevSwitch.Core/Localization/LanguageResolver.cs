// 文件用途：把设置中的语言标记（auto / zh-CN / en-US）解析为具体的 AppLanguage 枚举，
//           并提供 AppLanguage 反向转回标准标记字符串的能力。属于纯逻辑、无副作用、可单测。
// 创建日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：仅 System（字符串处理）

using System;

namespace DevSwitch.Core.Localization;

/// <summary>
/// DevSwitch 支持的界面语言枚举。
/// 仅区分“具体语言”，不含 auto——auto 是设置层的取值，会在解析阶段被映射为下列具体值之一。
/// </summary>
public enum AppLanguage
{
    /// <summary>简体中文（本项目主语言，也是一切无法识别情况的兜底）。</summary>
    ChineseSimplified,

    /// <summary>英文（en-US）。</summary>
    English
}

/// <summary>
/// 语言标记解析器（纯函数集合）。
/// 负责把不可信的、可能含大小写/空格噪声的语言标记，规约为确定的 <see cref="AppLanguage"/>。
/// </summary>
public static class LanguageResolver
{
    // 标准标记常量：集中定义，避免散落的魔法字符串。
    private const string AutoTag = "auto";
    private const string ChineseTag = "zh-CN";
    private const string EnglishTag = "en-US";

    /// <summary>
    /// 把设置中的语言标记解析为具体 <see cref="AppLanguage"/>。
    /// </summary>
    /// <param name="languageTag">
    /// 设置里的语言标记，取值概念为 auto / zh-CN / en-US。
    /// 允许大小写不敏感与前后空格容错；null、空白或无法识别时回退到默认中文。
    /// </param>
    /// <param name="systemCulture">
    /// 当 <paramref name="languageTag"/> 为 auto 时使用的系统区域名（如 "zh-CN"、"en-US"、"zh-Hans-CN"）。
    /// 以 "zh" 开头（不区分大小写）判定为中文，否则判定为英文；为空时回退到默认中文。
    /// </param>
    /// <returns>解析后的具体界面语言。</returns>
    public static AppLanguage Resolve(string? languageTag, string? systemCulture)
    {
        // 归一化：去除前后空白。null 时按空字符串处理，统一走兜底逻辑。
        var normalizedTag = (languageTag ?? string.Empty).Trim();

        // 显式 zh-CN：大小写不敏感比较 → 中文。
        if (string.Equals(normalizedTag, ChineseTag, StringComparison.OrdinalIgnoreCase))
        {
            return AppLanguage.ChineseSimplified;
        }

        // 显式 en-US：大小写不敏感比较 → 英文。
        if (string.Equals(normalizedTag, EnglishTag, StringComparison.OrdinalIgnoreCase))
        {
            return AppLanguage.English;
        }

        // auto：依据系统区域名判定。
        if (string.Equals(normalizedTag, AutoTag, StringComparison.OrdinalIgnoreCase))
        {
            return ResolveFromCulture(systemCulture);
        }

        // 其余情况（null / 空白 / 未知标记）：回退到本项目主语言——中文。
        return AppLanguage.ChineseSimplified;
    }

    /// <summary>
    /// 把具体 <see cref="AppLanguage"/> 转回标准标记字符串。
    /// </summary>
    /// <param name="language">界面语言。</param>
    /// <returns>"zh-CN" 或 "en-US"。</returns>
    public static string ToTag(AppLanguage language) => language switch
    {
        AppLanguage.English => EnglishTag,
        // 中文及任何未来未覆盖的枚举值均回退到中文标记，保证不抛异常、行为可预期。
        _ => ChineseTag
    };

    /// <summary>
    /// 根据系统区域名判定语言：以 "zh" 开头视为中文，否则英文；空白回退中文。
    /// 抽出为私有方法，便于 auto 分支单一职责且可读。
    /// </summary>
    private static AppLanguage ResolveFromCulture(string? systemCulture)
    {
        var normalizedCulture = (systemCulture ?? string.Empty).Trim();

        // 系统区域为空：无法判定，回退中文默认。
        if (normalizedCulture.Length == 0)
        {
            return AppLanguage.ChineseSimplified;
        }

        // "zh"、"zh-CN"、"zh-Hans-CN" 等中文系列均以 "zh" 开头。
        if (normalizedCulture.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return AppLanguage.ChineseSimplified;
        }

        // 非中文区域统一归为英文。
        return AppLanguage.English;
    }
}
