// 文件用途：纯算法检测 PATH 前序冲突——找出排在 DevSwitch 托管片段之前、可能遮蔽
//           java/node/go/mvn/rustc/cargo 命令的外部 PATH 条目，输出冲突来源、作用域与清理建议。
// 创建/修改日期：2026-06-10
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.Collections.Generic（隐式 using）
// NOTE: 合法授权学习使用，仅限本地环境。本类不修改任何用户配置，只做分析与建议。

namespace DevSwitch.Core;

/// <summary>
/// 单个被遮蔽 SDK 类型对应的 PATH 冲突。
/// </summary>
/// <param name="SdkType">受影响的 SDK 类型（Java/Node/Go/Maven/Rust）。</param>
/// <param name="ShadowingEntry">排在托管片段之前、可能遮蔽命令的外部 PATH 条目。</param>
/// <param name="Commands">该条目中命中的命令文件名集合，例如 java.exe。</param>
/// <param name="Source">推断出的冲突来源标识，例如 oracle-javapath、external-jdk。</param>
/// <param name="Scope">PATH 条目来源作用域：user 或 machine。machine 会先于用户 PATH 生效。</param>
public sealed record PathConflict(
    SdkType SdkType,
    string ShadowingEntry,
    IReadOnlyList<string> Commands,
    string Source,
    string Scope = PathConflictAnalyzer.UserScope);

/// <summary>
/// PATH 前序冲突分析结果。
/// </summary>
/// <param name="Conflicts">检测到的全部冲突；无冲突时为空集合。</param>
public sealed record PathConflictReport(IReadOnlyList<PathConflict> Conflicts)
{
    /// <summary>
    /// 是否存在任何冲突。
    /// </summary>
    public bool HasConflicts => Conflicts.Count > 0;

    /// <summary>
    /// 空结果常量。
    /// </summary>
    public static PathConflictReport Empty { get; } = new(Array.Empty<PathConflict>());
}

/// <summary>
/// PATH 前序冲突 / 遮蔽分析器（纯算法，无 I/O）。
/// </summary>
/// <remarks>
/// 算法概述：
/// 1. 在 PATH 条目列表中定位 DevSwitch 托管片段出现的最早位置（minManagedIndex）。
/// 2. 对每个排在该位置之前的外部条目，检查其目录名/路径特征是否命中某类 SDK 命令。
/// 3. 命中即视为潜在遮蔽，因为同名命令会被更靠前的条目优先解析。
/// Windows 新进程有效 PATH 的真实顺序是 Machine Path 在前、User Path 在后；因此仅把托管片段
/// 写到 HKCU 用户 PATH 的最前面，仍然压不过 HKLM 系统 PATH 里的旧 JDK/Node/Go/Maven。
/// 若 PATH 中根本没有托管片段，则所有命中条目都视为冲突（托管片段缺失会在其它检查项报告）。
/// </remarks>
public static class PathConflictAnalyzer
{
    /// <summary>
    /// 用户级 PATH 作用域标识。
    /// </summary>
    public const string UserScope = "user";

    /// <summary>
    /// 系统级 PATH 作用域标识；Windows 会把它排在用户级 PATH 前面。
    /// </summary>
    public const string MachineScope = "machine";

    // NOTE: 各 SDK 类型的代表性命令文件名（不含扩展名变体），用于匹配外部条目。
    //       匹配时大小写不敏感，并允许带 .exe/.cmd/.bat 扩展。
    private static readonly IReadOnlyDictionary<SdkType, string[]> CommandsByType =
        new Dictionary<SdkType, string[]>
        {
            [SdkType.Java] = new[] { "java", "javac" },
            [SdkType.Maven] = new[] { "mvn" },
            [SdkType.Node] = new[] { "node", "npm" },
            [SdkType.Go] = new[] { "go", "gofmt" },
            [SdkType.Rust] = new[] { "rustc", "cargo", "rustdoc" },
        };

    /// <summary>
    /// 分析单一 PATH 列表内的前序冲突。
    /// </summary>
    /// <param name="pathEntries">按原始顺序排列的 PATH 条目。</param>
    /// <param name="managedSegments">DevSwitch 托管 PATH 片段（已展开 dataRoot）。</param>
    /// <param name="commandFilesProbe">
    /// 可选探针：给定 PATH 条目目录，返回其中存在的命令文件名（小写、不含扩展名）。
    /// 为 null 时退化为“根据路径名特征推断”，便于纯单测不触碰文件系统。
    /// </param>
    /// <returns>冲突报告。</returns>
    public static PathConflictReport Analyze(
        IReadOnlyList<string> pathEntries,
        IReadOnlyList<string> managedSegments,
        Func<string, IReadOnlyList<string>>? commandFilesProbe = null)
    {
        ArgumentNullException.ThrowIfNull(pathEntries);

        // 兼容旧调用：单一 PATH 默认视作用户级 PATH，保持原有公开行为不变。
        var scopedEntries = pathEntries
            .Select(entry => new ScopedPathEntry(entry, UserScope))
            .ToArray();

        return AnalyzeCore(scopedEntries, managedSegments, commandFilesProbe);
    }

    /// <summary>
    /// 按 Windows 新进程有效顺序（Machine PATH 在前、User PATH 在后）分析遮蔽冲突。
    /// </summary>
    /// <param name="machinePathEntries">系统级 PATH 条目，来自 HKLM/Machine。</param>
    /// <param name="userPathEntries">用户级 PATH 条目，来自 HKCU/User。</param>
    /// <param name="managedSegments">DevSwitch 托管 PATH 片段（已展开 dataRoot）。</param>
    /// <param name="commandFilesProbe">可选命令探针；为 null 时使用路径启发式。</param>
    /// <returns>带作用域标识的冲突报告。</returns>
    public static PathConflictReport AnalyzeEffectivePath(
        IReadOnlyList<string> machinePathEntries,
        IReadOnlyList<string> userPathEntries,
        IReadOnlyList<string> managedSegments,
        Func<string, IReadOnlyList<string>>? commandFilesProbe = null)
    {
        ArgumentNullException.ThrowIfNull(machinePathEntries);
        ArgumentNullException.ThrowIfNull(userPathEntries);

        // Windows 构造新进程环境块时，Path 的有效顺序是 Machine 在前、User 在后。
        // 这里显式保留作用域，保证 UI 能说明“系统 PATH 无法被用户 PATH 置顶覆盖”。
        var scopedEntries = machinePathEntries
            .Select(entry => new ScopedPathEntry(entry, MachineScope))
            .Concat(userPathEntries.Select(entry => new ScopedPathEntry(entry, UserScope)))
            .ToArray();

        return AnalyzeCore(scopedEntries, managedSegments, commandFilesProbe);
    }

    /// <summary>
    /// 为冲突生成统一清理建议文案（不自动修改）。
    /// </summary>
    /// <param name="conflict">冲突项。</param>
    /// <returns>面向用户的手动建议。</returns>
    public static string BuildSuggestion(PathConflict conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);

        if (string.Equals(conflict.Scope, MachineScope, StringComparison.OrdinalIgnoreCase))
        {
            // 系统 PATH 优先于用户 PATH，这是这次真实 JDK 切换失败的关键根因。
            return $"系统 PATH 条目 \"{conflict.ShadowingEntry}\" 排在用户 PATH 之前，"
                + $"会优先遮蔽 {string.Join("、", conflict.Commands)}。"
                + "仅调整用户环境变量无法覆盖它；请以管理员权限在系统环境变量中移除/下移该条目，"
                + "或把 DevSwitch 托管片段加入系统 PATH 并置于这些旧 SDK 条目前。DevSwitch 不会静默修改系统 PATH。";
        }

        // NOTE: 用户级冲突仍严格遵循“只建议不自动改”的原则，明确指引用户手动调整顺序或移除外部条目。
        return $"外部 PATH 条目 \"{conflict.ShadowingEntry}\" 排在 DevSwitch 托管片段之前，"
            + $"可能遮蔽 {string.Join("、", conflict.Commands)}。"
            + "请手动在用户环境变量中将 DevSwitch 托管片段上移到该条目之前，或移除该外部条目；DevSwitch 不会自动修改它。";
    }

    /// <summary>
    /// 带作用域的 PATH 条目；仅供内部分析，不暴露到 UI。
    /// </summary>
    private readonly record struct ScopedPathEntry(string Entry, string Scope);

    /// <summary>
    /// 共用分析核心：在已经按真实优先级排序的条目中寻找托管片段之前的遮蔽项。
    /// </summary>
    private static PathConflictReport AnalyzeCore(
        IReadOnlyList<ScopedPathEntry> pathEntries,
        IReadOnlyList<string> managedSegments,
        Func<string, IReadOnlyList<string>>? commandFilesProbe)
    {
        ArgumentNullException.ThrowIfNull(pathEntries);
        ArgumentNullException.ThrowIfNull(managedSegments);

        if (pathEntries.Count == 0)
        {
            return PathConflictReport.Empty;
        }

        // 规整托管片段，便于路径相等比较。
        var normalizedManaged = managedSegments
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .Select(NormalizeEntry)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 定位最早出现的托管片段位置；找不到则用 int.MaxValue，表示“所有外部条目都在它之前”。
        int minManagedIndex = int.MaxValue;
        for (int i = 0; i < pathEntries.Count; i++)
        {
            if (normalizedManaged.Contains(NormalizeEntry(pathEntries[i].Entry)))
            {
                minManagedIndex = i;
                break;
            }
        }

        var conflicts = new List<PathConflict>();

        for (int i = 0; i < pathEntries.Count; i++)
        {
            // 只关心排在最早托管片段之前的条目；托管片段自身及其之后不算遮蔽。
            if (i >= minManagedIndex)
            {
                break;
            }

            var scoped = pathEntries[i];
            var entry = scoped.Entry;
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            var normalizedEntry = NormalizeEntry(entry);

            // 跳过本身就是托管片段的条目（理论上不会进入此分支，保险处理）。
            if (normalizedManaged.Contains(normalizedEntry))
            {
                continue;
            }

            // 解析该条目命中的命令：优先用探针（真实文件系统），否则按路径名特征推断。
            var matchedCommands = commandFilesProbe is not null
                ? MatchByProbe(entry, commandFilesProbe)
                : MatchByHeuristic(entry);

            foreach (var (sdkType, commands) in matchedCommands)
            {
                conflicts.Add(new PathConflict(
                    SdkType: sdkType,
                    ShadowingEntry: entry,
                    Commands: commands,
                    Source: InferSource(entry, sdkType),
                    Scope: scoped.Scope));
            }
        }

        return conflicts.Count == 0 ? PathConflictReport.Empty : new PathConflictReport(conflicts);
    }

    /// <summary>
    /// 用探针匹配条目中实际存在的命令文件。
    /// </summary>
    private static List<(SdkType SdkType, IReadOnlyList<string> Commands)> MatchByProbe(
        string entry,
        Func<string, IReadOnlyList<string>> probe)
    {
        var present = probe(entry)
            .Select(name => name.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var matches = new List<(SdkType, IReadOnlyList<string>)>();
        foreach (var (sdkType, commands) in CommandsByType)
        {
            var hit = commands.Where(present.Contains).ToArray();
            if (hit.Length > 0)
            {
                matches.Add((sdkType, hit));
            }
        }

        return matches;
    }

    /// <summary>
    /// 无探针时按路径名特征推断命中命令。
    /// </summary>
    /// <remarks>
    /// 这是保守的启发式：识别常见外部安装目录命名特征，例如 Oracle javapath、jdk/jre、
    /// nodejs、go、maven。它面向“在测试与无法读盘场景下也能给出合理判断”，
    /// 真实运行时应优先提供 commandFilesProbe。
    /// </remarks>
    private static List<(SdkType SdkType, IReadOnlyList<string> Commands)> MatchByHeuristic(string entry)
    {
        var lower = entry.ToLowerInvariant();
        var matches = new List<(SdkType, IReadOnlyList<string>)>();

        // Oracle 安装的 javapath 是最常见的 java 遮蔽来源。
        if (lower.Contains("javapath")
            || lower.Contains("jdk")
            || lower.Contains("jre")
            || lower.Contains(Path.Combine("java", "bin").ToLowerInvariant())
            || lower.EndsWith(Path.Combine("java").ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
        {
            matches.Add((SdkType.Java, new[] { "java", "javac" }));
        }

        if (lower.Contains("nodejs") || lower.Contains(Path.DirectorySeparatorChar + "node"))
        {
            matches.Add((SdkType.Node, new[] { "node", "npm" }));
        }

        if (lower.Contains("maven") || lower.Contains("apache-maven"))
        {
            matches.Add((SdkType.Maven, new[] { "mvn" }));
        }

        // Go 关键词较短，要求出现在路径分段中（前后是分隔符）以降低误报。
        if (ContainsSegment(lower, "go") || lower.Contains(Path.Combine("go", "bin").ToLowerInvariant()))
        {
            matches.Add((SdkType.Go, new[] { "go", "gofmt" }));
        }

        // Rust 常见遮蔽来源是用户级 .cargo\bin（rustup proxy）或外部 rustup/rust toolchain 目录。
        if (lower.Contains(Path.Combine(".cargo", "bin").ToLowerInvariant())
            || lower.Contains(Path.Combine("cargo", "bin").ToLowerInvariant())
            || lower.Contains("rustup")
            || ContainsSegment(lower, "rust"))
        {
            matches.Add((SdkType.Rust, new[] { "rustc", "cargo", "rustdoc" }));
        }

        return matches;
    }

    /// <summary>
    /// 判断路径中是否包含名为 segment 的独立目录分段。
    /// </summary>
    private static bool ContainsSegment(string lowerPath, string segment)
    {
        var parts = lowerPath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(part => string.Equals(part, segment, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 推断冲突来源标识，供 UI 展示与本地化。
    /// </summary>
    private static string InferSource(string entry, SdkType sdkType)
    {
        var lower = entry.ToLowerInvariant();
        if (lower.Contains("javapath"))
        {
            return "oracle-javapath";
        }

        if (sdkType == SdkType.Rust && lower.Contains(Path.Combine(".cargo", "bin").ToLowerInvariant()))
        {
            return "rustup-cargo-bin";
        }

        return sdkType switch
        {
            SdkType.Java => "external-java",
            SdkType.Node => "external-node",
            SdkType.Go => "external-go",
            SdkType.Maven => "external-maven",
            SdkType.Rust => "external-rust",
            _ => "external",
        };
    }

    /// <summary>
    /// 规整 PATH 条目用于相等比较：去除首尾空白、尾部分隔符，统一大小写由比较器处理。
    /// </summary>
    private static string NormalizeEntry(string entry)
    {
        var trimmed = entry.Trim().Trim('"');
        return trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
