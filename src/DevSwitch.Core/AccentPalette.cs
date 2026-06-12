// 文件用途：定义 DevSwitch 全局「强调色」调色板（6 套配色）及其解析逻辑。
//           纯数据 + 纯逻辑，不依赖 WinUI / Windows.UI，便于在 Core 单元测试中验证。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：无（仅 BCL）

using System.Collections.Generic;
using System.Linq;

namespace DevSwitch.Core;

/// <summary>
/// 单个强调色选项：包含标识 key、中英文显示名，以及三个配套颜色的 hex 值。
/// </summary>
/// <param name="Key">调色板标识，例如 azure/violet/emerald/amber/rose/sky；持久化到 settings.json。</param>
/// <param name="DisplayNameZh">中文显示名，设置页色块的 zh 标签。</param>
/// <param name="DisplayNameEn">英文显示名，设置页色块的 en 标签。</param>
/// <param name="Accent">主强调色 hex（#RRGGBB）：按钮背景、链接、主要高亮。</param>
/// <param name="AccentText">强调文本色 hex（略深）：文字、hover 态。</param>
/// <param name="AccentSubtle">强调浅底色 hex：选中态浅背景、信息条底色。</param>
public sealed record AccentColorOption(
    string Key,
    string DisplayNameZh,
    string DisplayNameEn,
    string Accent,
    string AccentText,
    string AccentSubtle);

/// <summary>
/// DevSwitch 强调色调色板：集中定义所有可选配色并提供容错解析。
/// 设计遵循 Fluent 2 风格，主色/文本色/浅底色三档保证文字与背景的可读对比度。
/// </summary>
public static class AccentPalette
{
    /// <summary>
    /// 默认强调色 key。取 azure 且其主色为 #2563EB，与历史版本 App.xaml 的硬编码一致，
    /// 确保升级后老用户视觉无变化。声明为 const 以便用作 record 构造参数的默认值。
    /// </summary>
    public const string DefaultKey = "azure";

    // NOTE: 颜色值参考 Fluent 2 / Tailwind 调色板，三档分别用于背景、文字、浅底，保证对比度可用。
    //       顺序即设置页色块的展示顺序。
    private static readonly IReadOnlyList<AccentColorOption> Options = new[]
    {
        // 海蓝：保持与历史默认 #2563EB 完全一致，作为默认色。
        new AccentColorOption("azure",   "海蓝",   "Azure",   "#2563EB", "#1D4ED8", "#EFF6FF"),
        new AccentColorOption("violet",  "紫罗兰", "Violet",  "#7C3AED", "#6D28D9", "#F5F3FF"),
        new AccentColorOption("emerald", "翡翠",   "Emerald", "#059669", "#047857", "#ECFDF5"),
        new AccentColorOption("amber",   "琥珀",   "Amber",   "#D97706", "#B45309", "#FFFBEB"),
        new AccentColorOption("rose",    "玫瑰",   "Rose",    "#E11D48", "#BE123C", "#FFF1F2"),
        new AccentColorOption("sky",     "青空",   "Sky",     "#0EA5E9", "#0284C7", "#F0F9FF"),
    };

    /// <summary>
    /// 所有可选强调色，按展示顺序排列。设置页 UI 直接遍历此列表渲染色块。
    /// </summary>
    public static IReadOnlyList<AccentColorOption> All => Options;

    /// <summary>
    /// 根据 key 解析强调色选项；容错处理。
    /// 未知、空白或 null 一律回退默认 <see cref="DefaultKey"/>（azure），
    /// 因此可安全用于反序列化旧 settings.json（缺失字段时 key 为 null）。
    /// </summary>
    /// <param name="key">调色板 key，大小写不敏感。</param>
    /// <returns>匹配的强调色选项，找不到时返回默认 azure。</returns>
    public static AccentColorOption Resolve(string? key)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            // NOTE: 大小写不敏感匹配，避免持久化值大小写漂移导致回退默认。
            var match = Options.FirstOrDefault(
                o => string.Equals(o.Key, key, System.StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        // 兜底：null/空/未知 key 均回退默认色，确保调用方永不拿到 null。
        return Options.First(o => o.Key == DefaultKey);
    }
}
