// 文件用途：Doctor 诊断结果的 UI 绑定行模型，把 Core 的 DiagnosticResult 投影为可绑定显示项
//           （含严重度文案、色块画刷、建议可见性）。
// 创建/修改日期：2026-06-10
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：DevSwitch.Core、Microsoft.UI.Xaml
// NOTE: 合法授权学习使用，仅限本地环境。

using DevSwitch.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DevSwitch.App.Models;

/// <summary>
/// Doctor 诊断结果 UI 行。
/// </summary>
public sealed class DoctorResultRow
{
    /// <summary>
    /// 检查项标题。
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// 结果细节描述。
    /// </summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>
    /// 手动处理建议；为空表示无建议。
    /// </summary>
    public string Suggestion { get; init; } = string.Empty;

    /// <summary>
    /// 严重度中文文案，例如 通过、信息、警告、错误、致命。
    /// </summary>
    public string SeverityText { get; init; } = string.Empty;

    /// <summary>
    /// 严重度对应的色块画刷。
    /// </summary>
    public Brush SeverityBrush { get; init; } = new SolidColorBrush(Colors.Gray);

    /// <summary>
    /// 建议文本的可见性（无建议时折叠）。
    /// </summary>
    public Visibility SuggestionVisibility =>
        string.IsNullOrWhiteSpace(Suggestion) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// 从 Core 诊断结果创建 UI 行。
    /// </summary>
    /// <param name="result">Core 诊断结果。</param>
    /// <returns>可绑定 UI 行。</returns>
    public static DoctorResultRow FromResult(DiagnosticResult result)
    {
        return new DoctorResultRow
        {
            Title = result.Title,
            Detail = result.Detail,
            Suggestion = result.Suggestion ?? string.Empty,
            SeverityText = MapSeverityText(result.Severity),
            SeverityBrush = new SolidColorBrush(MapSeverityColor(result.Severity)),
        };
    }

    /// <summary>
    /// 严重度 → 中文文案。
    /// </summary>
    private static string MapSeverityText(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Pass => "通过",
        DiagnosticSeverity.Info => "信息",
        DiagnosticSeverity.Warning => "警告",
        DiagnosticSeverity.Error => "错误",
        DiagnosticSeverity.Fatal => "致命",
        _ => "未知",
    };

    /// <summary>
    /// 严重度 → 色块颜色（绿/蓝/橙/红/深红）。
    /// </summary>
    private static Color MapSeverityColor(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Pass => Color.FromArgb(0xFF, 0x22, 0xC5, 0x5E),
        DiagnosticSeverity.Info => Color.FromArgb(0xFF, 0x0F, 0x6C, 0xBD),
        DiagnosticSeverity.Warning => Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B),
        DiagnosticSeverity.Error => Color.FromArgb(0xFF, 0xEF, 0x44, 0x44),
        DiagnosticSeverity.Fatal => Color.FromArgb(0xFF, 0xB9, 0x1C, 0x1C),
        _ => Colors.Gray,
    };
}
