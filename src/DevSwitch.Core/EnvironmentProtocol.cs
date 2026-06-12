// 文件用途：定义 DevSwitch 用户环境变量写入相关的 helper JSON 协议模型与纯算法。
//           包含默认变量集 / 托管 PATH 片段生成、PATH 合并去重、PATH 移除等纯函数。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.Text.Json.Serialization
// NOTE: 合法授权学习使用，仅限本地环境。本文件不做任何 I/O，只产出待写入的模型与结果，便于单测。

using System.IO;
using System.Text.Json.Serialization;

namespace DevSwitch.Core;

/// <summary>
/// 单个待写入的用户环境变量。
/// </summary>
/// <param name="Name">变量名，例如 JAVA_HOME。</param>
/// <param name="Value">变量值，保留 %VAR% 占位符，由 helper 以 REG_EXPAND_SZ 写入。</param>
public sealed record EnvironmentVariable(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] string Value);

/// <summary>
/// writeUserEnvironment payload。
/// </summary>
public sealed record WriteUserEnvironmentPayload(
    [property: JsonPropertyName("variables")] IReadOnlyList<EnvironmentVariable> Variables);

/// <summary>
/// appendManagedPathEntries / removeManagedPathEntries payload。
/// </summary>
public sealed record ManagedPathEntriesPayload(
    [property: JsonPropertyName("entries")] IReadOnlyList<string> Entries);

/// <summary>
/// readUserEnvironment payload。
/// </summary>
public sealed record ReadUserEnvironmentPayload(
    [property: JsonPropertyName("names")] IReadOnlyList<string> Names);

/// <summary>
/// rebuildShims payload：根据 current\&lt;type&gt;\bin 真实可执行重建 dataRoot\shims 下的转发器。
/// </summary>
/// <param name="DataRoot">数据根目录绝对路径。</param>
/// <param name="ShimSourcePath">通用转发器 DevSwitch.Shim.exe 的绝对路径（被复制成各命令 shim）。</param>
public sealed record RebuildShimsPayload(
    [property: JsonPropertyName("dataRoot")] string DataRoot,
    [property: JsonPropertyName("shimSourcePath")] string ShimSourcePath);

/// <summary>
/// 用户环境兼容选项（来自 settings.json 的 compatibility 段）。
/// </summary>
/// <param name="SetJdkHome">是否额外写入兼容变量 JDK_HOME。</param>
/// <param name="SetM2Home">是否额外写入兼容变量 M2_HOME。</param>
public sealed record EnvironmentCompatibilityOptions(bool SetJdkHome = false, bool SetM2Home = false)
{
    /// <summary>
    /// 默认（不写任何兼容变量）。
    /// </summary>
    public static EnvironmentCompatibilityOptions Default { get; } = new();
}

/// <summary>
/// PATH 合并结果。
/// </summary>
/// <param name="Entries">合并后的完整 PATH 条目（保持原有顺序，新增项追加在末尾）。</param>
/// <param name="Added">本次实际新增的托管片段（原始书写形式）。</param>
public sealed record PathMergeResult(IReadOnlyList<string> Entries, IReadOnlyList<string> Added)
{
    /// <summary>
    /// 是否发生变化。
    /// </summary>
    public bool Changed => Added.Count > 0;
}

/// <summary>
/// PATH 移除结果。
/// </summary>
/// <param name="Entries">移除托管片段后的剩余 PATH 条目（保持原有顺序）。</param>
/// <param name="Removed">本次实际移除的条目（原始书写形式）。</param>
public sealed record PathRemoveResult(IReadOnlyList<string> Entries, IReadOnlyList<string> Removed)
{
    /// <summary>
    /// 是否发生变化。
    /// </summary>
    public bool Changed => Removed.Count > 0;
}

/// <summary>
/// DevSwitch 用户环境变量布局纯算法：默认变量集、托管 PATH 片段生成、PATH 合并/移除。
/// </summary>
/// <remarks>
/// 设计依据 design.md 第 8 节。所有方法均为纯函数、无 I/O，便于单测。
/// helper 端实现一致的 PATH 去重/移除逻辑（NormalizePathEntryForCompare），
/// 两侧保持语义对齐：去首尾空白、去尾部分隔符、大小写不敏感比较，绝不重排用户已有条目。
/// </remarks>
public static class EnvironmentLayout
{
    /// <summary>
    /// DEVSWITCH_HOME 之外的变量统一引用该占位符，跟随 current 入口生效。
    /// </summary>
    public const string HomePlaceholder = "%DEVSWITCH_HOME%";

    /// <summary>
    /// DEVSWITCH_HOME 变量名常量。
    /// </summary>
    public const string DevSwitchHomeName = "DEVSWITCH_HOME";

    /// <summary>
    /// PATH 变量名常量。
    /// </summary>
    public const string PathName = "Path";

    /// <summary>
    /// shims 目录名。shim 单目录方案：系统 PATH 只放 dataRoot\shims 一条即可覆盖所有命令，
    /// 根治系统 PATH 2047 字符上限，且切换只换 current junction、不改 PATH。
    /// </summary>
    public const string ShimsDirectoryName = "shims";

    /// <summary>
    /// 生成 shims 目录的绝对路径片段（dataRoot\shims）。
    /// </summary>
    /// <param name="devSwitchHomeValue">数据根目录绝对路径。</param>
    public static string BuildShimsPathEntry(string devSwitchHomeValue)
    {
        if (string.IsNullOrWhiteSpace(devSwitchHomeValue))
        {
            throw new ArgumentException("DEVSWITCH_HOME value is required.", nameof(devSwitchHomeValue));
        }

        return Path.Combine(devSwitchHomeValue, ShimsDirectoryName);
    }

    /// <summary>
    /// 生成 DevSwitch 默认用户环境变量集（design.md 第 8 节）。
    /// </summary>
    /// <param name="devSwitchHomeValue">DEVSWITCH_HOME 的值，必须是解析后的绝对路径（数据根目录）。</param>
    /// <param name="options">兼容变量选项。</param>
    /// <returns>有序的待写入变量集合。</returns>
    /// <remarks>
    /// 修复（2026-06-09）：JAVA_HOME/MAVEN_HOME/GOROOT 等不再使用嵌套占位符 %DEVSWITCH_HOME%\...，
    /// 因为 Windows 环境变量展开只展开一层，%DEVSWITCH_HOME%\current\java 永远展不开成真实路径，
    /// 导致 JAVA_HOME 无效、mvn 找不到 java。这里改为基于 devSwitchHomeValue 的绝对路径，
    /// 切换 SDK 只改 current 链接指向，这些绝对路径不变仍有效；移动数据目录由漂移校正重写。
    /// </remarks>
    public static IReadOnlyList<EnvironmentVariable> BuildDefaultVariables(
        string devSwitchHomeValue,
        EnvironmentCompatibilityOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(devSwitchHomeValue))
        {
            throw new ArgumentException("DEVSWITCH_HOME value is required.", nameof(devSwitchHomeValue));
        }

        options ??= EnvironmentCompatibilityOptions.Default;

        // DEVSWITCH_HOME 仍写绝对路径（供漂移检测与用户参考）；
        // 其余变量改为展开后的绝对路径，避免嵌套占位符无法递归展开。
        var javaHome = Path.Combine(devSwitchHomeValue, "current", "java");
        var mavenHome = Path.Combine(devSwitchHomeValue, "current", "maven");
        var goRoot = Path.Combine(devSwitchHomeValue, "current", "go");

        var variables = new List<EnvironmentVariable>
        {
            new(DevSwitchHomeName, devSwitchHomeValue),
            new("JAVA_HOME", javaHome),
            new("MAVEN_HOME", mavenHome),
            new("GOROOT", goRoot),
        };

        // 可选兼容变量：部分老旧构建脚本依赖 JDK_HOME / M2_HOME。
        if (options.SetJdkHome)
        {
            variables.Add(new("JDK_HOME", javaHome));
        }

        if (options.SetM2Home)
        {
            variables.Add(new("M2_HOME", mavenHome));
        }

        return variables;
    }

    /// <summary>
    /// 生成 DevSwitch 托管 PATH 片段（遗留占位符版本），顺序固定：java、maven、node、go、rust。
    /// </summary>
    /// <remarks>
    /// 遗留实现：返回嵌套 %DEVSWITCH_HOME% 占位符片段。该形式因 Windows 仅展开一层而无法生效，
    /// 仅为向后兼容仍调用本无参重载的代码路径（如 reset 移除链路）而保留。
    /// 初始化等新链路应改用 <see cref="BuildManagedPathEntries(string)"/> 生成绝对路径片段。
    /// NOTE: 当 init 写绝对路径、reset 用本占位符版本移除时，两者不匹配会导致 reset 移不掉新片段，
    /// 这一不一致需由上层统一为绝对路径后才能彻底消除。
    /// </remarks>
    /// <returns>有序的托管 PATH 片段集合（占位符形式）。</returns>
    public static IReadOnlyList<string> BuildManagedPathEntries()
    {
        return new[]
        {
            $@"{HomePlaceholder}\current\java\bin",
            $@"{HomePlaceholder}\current\maven\bin",
            $@"{HomePlaceholder}\current\node",
            $@"{HomePlaceholder}\current\go\bin",
            $@"{HomePlaceholder}\current\rust\bin",
        };
    }

    /// <summary>
    /// 生成 DevSwitch 托管 PATH 片段（绝对路径版本），顺序固定：java、maven、node、go、rust。
    /// </summary>
    /// <param name="devSwitchHomeValue">DEVSWITCH_HOME 的值，必须是解析后的绝对路径（数据根目录）。</param>
    /// <returns>有序的托管 PATH 片段集合（绝对路径形式）。</returns>
    /// <remarks>
    /// 修复（2026-06-09）：与 <see cref="BuildDefaultVariables"/> 同因——嵌套占位符无法被 Windows 展开。
    /// 这里返回基于 devSwitchHomeValue 的绝对路径片段，确保托管 PATH 真正可用。
    /// </remarks>
    public static IReadOnlyList<string> BuildManagedPathEntries(string devSwitchHomeValue)
    {
        if (string.IsNullOrWhiteSpace(devSwitchHomeValue))
        {
            throw new ArgumentException("DEVSWITCH_HOME value is required.", nameof(devSwitchHomeValue));
        }

        return new[]
        {
            Path.Combine(devSwitchHomeValue, "current", "java", "bin"),
            Path.Combine(devSwitchHomeValue, "current", "maven", "bin"),
            Path.Combine(devSwitchHomeValue, "current", "node"),
            Path.Combine(devSwitchHomeValue, "current", "go", "bin"),
            Path.Combine(devSwitchHomeValue, "current", "rust", "bin"),
        };
    }

    /// <summary>
    /// 把托管片段合并进现有 PATH：去重、只加不存在的、保持已有顺序，绝不删除/重排用户原有条目。
    /// </summary>
    /// <param name="existingEntries">现有 PATH 条目（按原始顺序，可能含空条目）。</param>
    /// <param name="managedEntries">待追加的托管片段。</param>
    /// <returns>合并结果。</returns>
    public static PathMergeResult MergeManagedPathEntries(
        IReadOnlyList<string> existingEntries,
        IReadOnlyList<string> managedEntries)
    {
        ArgumentNullException.ThrowIfNull(existingEntries);
        ArgumentNullException.ThrowIfNull(managedEntries);

        // 现有条目的规范化集合，用于去重判断；保留原始 existingEntries 不变。
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in existingEntries)
        {
            var normalized = NormalizeEntryForCompare(entry);
            if (normalized.Length > 0)
            {
                present.Add(normalized);
            }
        }

        // 起点是用户原有条目的完整拷贝，保持原始顺序与书写形式。
        var merged = new List<string>(existingEntries);
        var added = new List<string>();

        foreach (var managed in managedEntries)
        {
            var normalized = NormalizeEntryForCompare(managed);
            // 空片段或已存在（含本次已追加）的片段直接跳过。
            if (normalized.Length == 0 || present.Contains(normalized))
            {
                continue;
            }

            present.Add(normalized);
            merged.Add(managed);
            added.Add(managed);
        }

        return new PathMergeResult(merged, added);
    }

    /// <summary>
    /// 把托管片段「置顶」合并进现有 PATH：managed 去重、按给定顺序排到最前；
    /// existing 中与 managed 规范化相等的旧条目被移除（避免重复遮蔽），其余用户条目保序在后。
    /// </summary>
    /// <param name="existingEntries">现有 PATH 条目（按原始顺序，可能含空条目）。</param>
    /// <param name="managedEntries">待置顶的托管片段。</param>
    /// <returns>合并结果：Entries 为置顶后的完整 PATH，Added 为实际置顶的托管片段（去重保序）。</returns>
    /// <remarks>
    /// 这是 helper 端 prependManagedPathEntries 置顶算法的 C# 镜像，作为契约文档与回归保护。
    /// 修复场景：用户已有其它 SDK 工具残留项排在前面，会遮蔽追加到末尾的托管 java\bin，
    /// 导致 java 仍解析旧版本。置顶后托管片段优先级最高，确保解析到 DevSwitch 管理的版本。
    /// 比较语义与 <see cref="NormalizeEntryForCompare"/> 一致：去首尾空白、去尾部分隔符、大小写不敏感。
    /// 空条目（规范化后长度为 0）不参与去重判断，原样保留在尾部，忠实反映用户原始结构。
    /// </remarks>
    public static PathMergeResult MergeManagedPathEntriesPrepend(
        IReadOnlyList<string> existingEntries,
        IReadOnlyList<string> managedEntries)
    {
        ArgumentNullException.ThrowIfNull(existingEntries);
        ArgumentNullException.ThrowIfNull(managedEntries);

        // 1) 先按给定顺序对 managed 去重，得到要置顶的片段集合（原始书写形式）。
        var prepended = new List<string>();
        var managedNormalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var managed in managedEntries)
        {
            var normalized = NormalizeEntryForCompare(managed);
            // 空片段或同一请求内的等价重复片段跳过，只保留首次出现。
            if (normalized.Length == 0 || managedNormalized.Contains(normalized))
            {
                continue;
            }

            managedNormalized.Add(normalized);
            prepended.Add(managed);
        }

        // 2) existing 中与 managed 规范化相等的旧条目移除（防止重复并遮蔽），其余保序保留。
        var tail = new List<string>(existingEntries.Count);
        foreach (var entry in existingEntries)
        {
            var normalized = NormalizeEntryForCompare(entry);
            // 仅移除与置顶托管片段完全匹配的非空条目；空条目与其它用户条目原样保留。
            if (normalized.Length > 0 && managedNormalized.Contains(normalized))
            {
                continue;
            }

            tail.Add(entry);
        }

        // 3) 置顶片段在前，用户其余条目保序在后。
        var merged = new List<string>(prepended.Count + tail.Count);
        merged.AddRange(prepended);
        merged.AddRange(tail);

        return new PathMergeResult(merged, prepended);
    }

    /// <summary>
    /// 从现有 PATH 移除与托管片段完全匹配（规范化相等）的条目，保留其它条目与顺序。
    /// </summary>
    /// <param name="existingEntries">现有 PATH 条目。</param>
    /// <param name="managedEntries">待移除的托管片段。</param>
    /// <returns>移除结果。</returns>
    public static PathRemoveResult RemoveManagedPathEntries(
        IReadOnlyList<string> existingEntries,
        IReadOnlyList<string> managedEntries)
    {
        ArgumentNullException.ThrowIfNull(existingEntries);
        ArgumentNullException.ThrowIfNull(managedEntries);

        // 待移除片段的规范化集合。
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in managedEntries)
        {
            var normalized = NormalizeEntryForCompare(entry);
            if (normalized.Length > 0)
            {
                targets.Add(normalized);
            }
        }

        var kept = new List<string>(existingEntries.Count);
        var removed = new List<string>();

        foreach (var entry in existingEntries)
        {
            var normalized = NormalizeEntryForCompare(entry);
            // 只移除完全匹配的托管条目；空条目与非托管条目原样保留。
            if (normalized.Length > 0 && targets.Contains(normalized))
            {
                removed.Add(entry);
                continue;
            }

            kept.Add(entry);
        }

        return new PathRemoveResult(kept, removed);
    }

    /// <summary>
    /// 按 ';' 拆分 PATH 原始值（保留空条目，忠实反映用户原始结构）。
    /// </summary>
    /// <param name="rawPath">PATH 原始未展开值，可能为 null/空。</param>
    /// <returns>条目列表。</returns>
    public static IReadOnlyList<string> SplitPath(string? rawPath)
    {
        if (string.IsNullOrEmpty(rawPath))
        {
            return Array.Empty<string>();
        }

        return rawPath.Split(';');
    }

    /// <summary>
    /// 用 ';' 重新拼接 PATH 条目。
    /// </summary>
    /// <param name="entries">PATH 条目。</param>
    /// <returns>拼接后的 PATH 值。</returns>
    public static string JoinPath(IReadOnlyList<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return string.Join(';', entries);
    }

    /// <summary>
    /// 规范化单个 PATH 条目用于去重比较：去首尾空白、去尾部分隔符。比较大小写由调用方比较器处理。
    /// </summary>
    /// <remarks>
    /// 与 helper 端 NormalizePathEntryForCompare 语义一致，确保两侧去重判断结果相同。
    /// 仅用于比较，绝不替换写入的原始条目文本。
    /// </remarks>
    public static string NormalizeEntryForCompare(string? entry)
    {
        if (string.IsNullOrEmpty(entry))
        {
            return string.Empty;
        }

        var trimmed = entry.Trim();
        // 去尾部反斜杠/正斜杠，但至少保留一个字符（避免把单独的 "\" 清空导致与空条目混淆）。
        int end = trimmed.Length;
        while (end > 1 && (trimmed[end - 1] == '\\' || trimmed[end - 1] == '/'))
        {
            end--;
        }

        return trimmed[..end];
    }
}
