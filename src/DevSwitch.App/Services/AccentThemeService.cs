// 文件用途：DevSwitch 全局「强调色」运行时换色服务。
//           根据用户选择的调色板 key，把 Application 资源字典里强调色相关 Brush 的颜色就地更新，
//           使所有引用这些 Brush 的已渲染控件即时变色，无需重启。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：Microsoft.UI.Xaml（WinUI 3）、Windows.UI

using System;
using System.Globalization;
using DevSwitch.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DevSwitch.App.Services;

/// <summary>
/// 强调色运行时应用服务：把所选调色板的三档颜色写入应用级资源字典。
/// </summary>
public static class AccentThemeService
{
    // NOTE: 受影响的资源键，与 App.xaml 中的 SolidColorBrush x:Key 一一对应。
    private const string AccentBrushKey = "DevSwitchAccentBrush";
    private const string AccentTextBrushKey = "DevSwitchAccentTextBrush";
    private const string AccentSubtleBrushKey = "DevSwitchAccentSubtleBrush";

    /// <summary>
    /// 应用指定 key 的强调色。
    /// 采用「改 .Color」方案：直接修改资源字典里既有 SolidColorBrush 实例的 Color 属性，
    /// 而不是替换为新的 Brush 实例。原因——控件在加载时已把 Brush 引用缓存进自身属性，
    /// 替换字典里的实例不会回溯刷新已渲染控件；而修改同一实例的 Color，
    /// 所有引用它的控件会即时收到属性变更通知并重绘，达到全局即时变色。
    /// </summary>
    /// <param name="accentKey">调色板 key；未知/空由 AccentPalette.Resolve 回退默认 azure。</param>
    public static void Apply(string? accentKey)
    {
        var option = AccentPalette.Resolve(accentKey);

        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            // 极端早期调用（Application 尚未就绪）时静默跳过，由后续启动流程再触发。
            return;
        }

        // 三档颜色就地更新；任一键缺失则跳过该项，避免抛异常阻塞启动。
        UpdateBrushColor(resources, AccentBrushKey, option.Accent);
        UpdateBrushColor(resources, AccentTextBrushKey, option.AccentText);
        UpdateBrushColor(resources, AccentSubtleBrushKey, option.AccentSubtle);
    }

    /// <summary>
    /// 就地更新资源字典中指定键的 SolidColorBrush 颜色。
    /// </summary>
    private static void UpdateBrushColor(ResourceDictionary resources, string key, string hex)
    {
        // 仅当键存在且确为 SolidColorBrush 时才改 .Color，命中「同实例变色」路径。
        if (resources.TryGetValue(key, out var value) && value is SolidColorBrush brush)
        {
            brush.Color = ColorFromHex(hex);
        }
    }

    /// <summary>
    /// 把 #RRGGBB 或 #AARRGGBB 形式的 hex 字符串解析为 <see cref="Color"/>。
    /// 解析失败时回退为不透明黑色，保证不抛异常中断换色。
    /// </summary>
    /// <param name="hex">颜色字符串，允许带或不带前导 #。</param>
    /// <returns>对应的 Windows.UI.Color。</returns>
    public static Color ColorFromHex(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
        }

        // 去掉可选的前导 #，统一按十六进制解析。
        var raw = hex.TrimStart('#');

        byte a = 0xFF, r, g, b;
        try
        {
            if (raw.Length == 6)
            {
                // #RRGGBB：默认不透明。
                r = ParseHexByte(raw, 0);
                g = ParseHexByte(raw, 2);
                b = ParseHexByte(raw, 4);
            }
            else if (raw.Length == 8)
            {
                // #AARRGGBB：显式 alpha。
                a = ParseHexByte(raw, 0);
                r = ParseHexByte(raw, 2);
                g = ParseHexByte(raw, 4);
                b = ParseHexByte(raw, 6);
            }
            else
            {
                // 长度非法：回退黑色，避免崩溃。
                return Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
            }
        }
        catch (FormatException)
        {
            return Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
        }

        return Color.FromArgb(a, r, g, b);
    }

    /// <summary>
    /// 从字符串指定位置取 2 个字符解析为字节。
    /// </summary>
    private static byte ParseHexByte(string value, int offset)
        => byte.Parse(value.Substring(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
