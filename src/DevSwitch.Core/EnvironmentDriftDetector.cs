// 文件用途：检测 DevSwitch「环境位置漂移」——HKCU 中记录的 DEVSWITCH_HOME 与当前实际数据根是否一致。
//          当用户移动了工具目录（便携模式数据根随之改变）时，旧的 DEVSWITCH_HOME 会指向失效路径，
//          导致 JAVA_HOME/MAVEN_HOME/GOROOT 等占位符引用全部失效，需要在启动时检测并提示校正。
// 创建日期：2026-06-10
// 语言版本要求：C# 12 / .NET 8+（nullable enable）
// 依赖库：System.IO（BCL，仅用于路径规范化），System（环境变量展开）
// NOTE: 本类核心为纯函数，不直接读注册表；HKCU 中的 DEVSWITCH_HOME 由上层读出后作为参数传入，便于单测。

namespace DevSwitch.Core;

/// <summary>
/// 环境位置漂移检测结果。面向日志/UI，承载是否漂移、是否已初始化以及规范化后的路径信息。
/// </summary>
/// <param name="HasDrift">是否发生漂移，需要把 DEVSWITCH_HOME 校正为 <paramref name="CurrentDataRoot"/>。</param>
/// <param name="IsInitialized">DEVSWITCH_HOME 是否曾被设置过（用于区分“从未初始化”与“漂移”）。</param>
/// <param name="CurrentDataRoot">当前实际数据根（规范化后）。漂移时上层应据此重写注册值。</param>
/// <param name="RegisteredHome">HKCU 中记录的 DEVSWITCH_HOME（展开+规范化后；未设置为 null）。</param>
/// <param name="Reason">面向日志/UI 的简短中文说明。</param>
public sealed record EnvironmentDriftResult(
    bool HasDrift,
    bool IsInitialized,
    string CurrentDataRoot,
    string? RegisteredHome,
    string Reason);

/// <summary>
/// 提供环境位置漂移的纯逻辑判定。
/// </summary>
public static class EnvironmentDriftDetector
{
    /// <summary>
    /// 判定当前数据根与注册的 DEVSWITCH_HOME 之间是否存在位置漂移。
    /// </summary>
    /// <param name="currentDataRoot">当前实际数据根（绝对路径，通常由 DataRootResolver 解析得到）。</param>
    /// <param name="registeredDevSwitchHome">
    /// HKCU 中 DEVSWITCH_HOME 的当前值；可能为 null/空（从未初始化），
    /// 也可能含未展开的环境变量占位（如 %LOCALAPPDATA%\DevSwitch），内部会先展开再比较。
    /// </param>
    /// <returns>漂移检测结果。</returns>
    /// <exception cref="ArgumentException">当 <paramref name="currentDataRoot"/> 为空或仅空白时抛出。</exception>
    public static EnvironmentDriftResult Detect(string currentDataRoot, string? registeredDevSwitchHome)
    {
        // 当前数据根是判定基准，必须有效；为空属于调用方编程错误，直接抛出。
        if (string.IsNullOrWhiteSpace(currentDataRoot))
        {
            throw new ArgumentException("Current data root is required.", nameof(currentDataRoot));
        }

        // 规范化当前数据根作为对外展示与比较的基准值。
        var normalizedCurrent = Normalize(currentDataRoot);

        // 规则一：DEVSWITCH_HOME 从未设置 → 未初始化，不算漂移（首次初始化由切换/初始化流程负责）。
        if (string.IsNullOrWhiteSpace(registeredDevSwitchHome))
        {
            return new EnvironmentDriftResult(
                HasDrift: false,
                IsInitialized: false,
                CurrentDataRoot: normalizedCurrent,
                RegisteredHome: null,
                Reason: "DEVSWITCH_HOME 尚未设置，跳过漂移校正（等待首次初始化）。");
        }

        // 注册值可能含环境变量占位符，先展开；展开依赖运行环境但对本机已是确定性足够。
        // NOTE: ExpandEnvironmentVariables 不会抛异常，未识别的占位符会原样保留。
        var expandedRegistered = Environment.ExpandEnvironmentVariables(registeredDevSwitchHome);

        // 尝试规范化注册值；注册值可能是各种异常路径（非法字符等），失败时回退到 trim 后的原始值，
        // 保证“健壮：异常路径不抛，尽量判定”。
        string normalizedRegistered;
        try
        {
            normalizedRegistered = Normalize(expandedRegistered);
        }
        catch (Exception)
        {
            // 无法规范化：去除首尾空白后作为比较值，通常会与当前数据根不等从而判定为漂移。
            normalizedRegistered = expandedRegistered.Trim();
        }

        // Windows 路径大小写不敏感，比较使用 OrdinalIgnoreCase。
        var consistent = string.Equals(normalizedCurrent, normalizedRegistered, StringComparison.OrdinalIgnoreCase);

        if (consistent)
        {
            // 规则二：已设置且与当前数据根一致 → 无需校正。
            return new EnvironmentDriftResult(
                HasDrift: false,
                IsInitialized: true,
                CurrentDataRoot: normalizedCurrent,
                RegisteredHome: normalizedRegistered,
                Reason: "DEVSWITCH_HOME 与当前数据根一致，无需校正。");
        }

        // 规则三：已设置但指向不同路径 → 发生漂移，上层应据 CurrentDataRoot 重写注册值。
        return new EnvironmentDriftResult(
            HasDrift: true,
            IsInitialized: true,
            CurrentDataRoot: normalizedCurrent,
            RegisteredHome: normalizedRegistered,
            Reason: $"检测到环境位置漂移：DEVSWITCH_HOME=\"{normalizedRegistered}\" 与当前数据根 \"{normalizedCurrent}\" 不一致，需校正。");
    }

    /// <summary>
    /// 路径规范化纯函数：用于比较与展示。
    /// 处理：解析 <c>.</c>/<c>..</c>、统一为绝对路径（<see cref="Path.GetFullPath(string)"/>）、去除多余尾部分隔符。
    /// 不改变大小写（保留原始书写，便于显示），路径比较时由调用方使用 OrdinalIgnoreCase。
    /// </summary>
    /// <param name="path">待规范化的路径，可含 <c>.</c>/<c>..</c>、尾分隔符、混用 <c>/</c> 与 <c>\</c>。</param>
    /// <returns>规范化后的绝对路径。</returns>
    /// <exception cref="ArgumentException">当 <paramref name="path"/> 为空或仅空白时抛出。</exception>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path is required.", nameof(path));
        }

        // GetFullPath 会规范化 . 与 ..、统一分隔符、补全为绝对路径（相对路径基于当前工作目录）。
        var full = Path.GetFullPath(path.Trim());

        // 去除尾部的目录分隔符（\ 与 /），使 "C:\a\b\" 与 "C:\a\b" 视为相同。
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // NOTE: 若 trim 后退化为盘符（如 "C:"），补回根分隔符避免误判（"C:\" 是盘根的合法形式）。
        if (trimmed.Length == 2 && trimmed[1] == Path.VolumeSeparatorChar)
        {
            trimmed += Path.DirectorySeparatorChar;
        }

        return trimmed;
    }
}
