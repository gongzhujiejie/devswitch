// 文件用途：提供语义化（SemVer-ish）版本比较，用于版本列表降序排序。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System、System.Collections.Generic
// NOTE: 合法授权学习使用，仅限本地环境。

using System.Globalization;

namespace DevSwitch.Sources;

/// <summary>
/// 宽松的语义化版本比较器。
/// </summary>
/// <remarks>
/// 兼容多种来源前缀与分隔：node 的 "v22.11.0"、go 的 "go1.22.5"、
/// Adoptium 的 "21.0.4+7"、Maven 的 "3.9.9"。
/// 规则：
/// 1. 去除前缀（v / go）后按数字段（以 . 或 + 或 - 分隔）逐段比较。
/// 2. 纯数字段按数值比较；遇到非数字段回退为字典序，保证不抛异常。
/// 3. 段数不同时缺失段视为 0。
/// 比较语义为升序；降序排序时取反。
/// </remarks>
public sealed class SemanticVersionComparer : IComparer<string>
{
    /// <summary>
    /// 共享的无状态比较器实例。
    /// </summary>
    public static SemanticVersionComparer Instance { get; } = new();

    /// <summary>
    /// 升序比较两个版本字符串。
    /// </summary>
    /// <param name="x">左版本。</param>
    /// <param name="y">右版本。</param>
    /// <returns>负数表示 x 小于 y，正数表示 x 大于 y，0 表示相等。</returns>
    public int Compare(string? x, string? y)
    {
        // 空值排在最前，保证排序稳定且不抛异常。
        if (string.IsNullOrEmpty(x) && string.IsNullOrEmpty(y))
        {
            return 0;
        }

        if (string.IsNullOrEmpty(x))
        {
            return -1;
        }

        if (string.IsNullOrEmpty(y))
        {
            return 1;
        }

        // 拆分为可比较的段（数字段携带数值，非数字段携带原文）。
        var left = Tokenize(x);
        var right = Tokenize(y);

        var max = Math.Max(left.Count, right.Count);
        for (var i = 0; i < max; i++)
        {
            // 缺失段补 0，使 "1.2" 与 "1.2.0" 相等。
            var a = i < left.Count ? left[i] : NumericToken(0);
            var b = i < right.Count ? right[i] : NumericToken(0);

            int cmp;
            if (a.IsNumeric && b.IsNumeric)
            {
                // 两侧都是数字：按数值比较，避免 "10" < "9" 的字典序错误。
                cmp = a.Number.CompareTo(b.Number);
            }
            else
            {
                // 任一侧为非数字：数字优先级高于预发布标识（如 rc、beta）。
                // SemVer 约定：正式版 > 预发布版，因此数字段视为更大。
                if (a.IsNumeric != b.IsNumeric)
                {
                    cmp = a.IsNumeric ? 1 : -1;
                }
                else
                {
                    cmp = string.CompareOrdinal(a.Text, b.Text);
                }
            }

            if (cmp != 0)
            {
                return cmp;
            }
        }

        return 0;
    }

    /// <summary>
    /// 把版本字符串拆分为有序的比较段。
    /// </summary>
    private static List<Token> Tokenize(string version)
    {
        var tokens = new List<Token>(8);
        var span = version.AsSpan();

        // 跳过常见前缀：node 的 'v'、go 的 'go'。
        var start = 0;
        if (span.Length > 0 && (span[0] == 'v' || span[0] == 'V'))
        {
            start = 1;
        }
        else if (span.Length >= 2 && (span[0] == 'g' || span[0] == 'G') && (span[1] == 'o' || span[1] == 'O')
                 && span.Length > 2 && char.IsDigit(span[2]))
        {
            start = 2;
        }

        var i = start;
        while (i < span.Length)
        {
            var c = span[i];

            // 分隔符：. + - 直接跳过，作为段边界。
            if (c is '.' or '+' or '-' or '_')
            {
                i++;
                continue;
            }

            if (char.IsDigit(c))
            {
                // 连续数字组成一个数值段。
                var j = i;
                while (j < span.Length && char.IsDigit(span[j]))
                {
                    j++;
                }

                var slice = span[i..j];
                // 用 long 容纳较大构建号；溢出时回退为文本段保证不抛异常。
                if (long.TryParse(slice, NumberStyles.None, CultureInfo.InvariantCulture, out var num))
                {
                    tokens.Add(NumericToken(num));
                }
                else
                {
                    tokens.Add(TextToken(slice.ToString()));
                }

                i = j;
            }
            else
            {
                // 连续非数字（字母）组成一个文本段，例如 rc、beta。
                var j = i;
                while (j < span.Length && !char.IsDigit(span[j]) && span[j] is not ('.' or '+' or '-' or '_'))
                {
                    j++;
                }

                tokens.Add(TextToken(span[i..j].ToString()));
                i = j;
            }
        }

        return tokens;
    }

    private static Token NumericToken(long value) => new(true, value, string.Empty);

    private static Token TextToken(string text) => new(false, 0, text);

    /// <summary>
    /// 版本比较段：数字或文本二选一。
    /// </summary>
    private readonly record struct Token(bool IsNumeric, long Number, string Text);
}
