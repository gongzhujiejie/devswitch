// 文件用途：定义 DevSwitch Doctor 诊断功能的公开模型与可注入抽象接口。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.Collections.Generic（隐式 using）
// NOTE: 本文件只声明数据模型与抽象，不含 I/O 行为，便于单元测试用 fake 驱动各检查项。

namespace DevSwitch.Core;

/// <summary>
/// 诊断结果严重等级，对应设计文档第 12 节错误等级。
/// </summary>
/// <remarks>
/// 枚举值按严重程度递增排列，便于用比较运算聚合“整体最高等级”。
/// </remarks>
public enum DiagnosticSeverity
{
    /// <summary>
    /// 检查通过，无需处理。
    /// </summary>
    Pass = 0,

    /// <summary>
    /// 信息或建议，例如可选优化项。
    /// </summary>
    Info = 1,

    /// <summary>
    /// 警告，例如 PATH 前序冲突、未验证状态。
    /// </summary>
    Warning = 2,

    /// <summary>
    /// 错误，例如某项检查失败但工具仍可继续运行。
    /// </summary>
    Error = 3,

    /// <summary>
    /// 致命问题，例如 helper 不可用、配置版本不受支持。
    /// </summary>
    Fatal = 4,
}

/// <summary>
/// 单条诊断检查结果。
/// </summary>
/// <param name="Id">稳定检查项标识，例如 data-root-writable，便于 UI 定位和本地化。</param>
/// <param name="Title">面向用户的简短标题。</param>
/// <param name="Severity">本条结果的严重等级。</param>
/// <param name="Detail">结果细节描述；已脱敏，避免泄露完整路径或环境变量。</param>
/// <param name="Suggestion">手动处理建议；为空表示无需建议（例如通过项）。</param>
public sealed record DiagnosticResult(
    string Id,
    string Title,
    DiagnosticSeverity Severity,
    string Detail,
    string? Suggestion = null)
{
    /// <summary>
    /// 创建一条通过结果的便捷方法。
    /// </summary>
    public static DiagnosticResult Pass(string id, string title, string detail)
        => new(id, title, DiagnosticSeverity.Pass, detail);
}

/// <summary>
/// Doctor 诊断报告：所有检查结果加汇总信息。
/// </summary>
/// <param name="Results">按检查项 id 稳定排序的结果集合。</param>
/// <param name="GeneratedAt">报告生成时间。</param>
public sealed record DoctorReport(IReadOnlyList<DiagnosticResult> Results, DateTimeOffset GeneratedAt)
{
    /// <summary>
    /// 整体严重等级，取所有结果中的最高等级；无结果时视为 Pass。
    /// </summary>
    public DiagnosticSeverity OverallSeverity { get; } =
        Results.Count == 0 ? DiagnosticSeverity.Pass : Results.Max(result => result.Severity);

    /// <summary>
    /// 各等级出现次数汇总，缺省等级计数为 0。
    /// </summary>
    public IReadOnlyDictionary<DiagnosticSeverity, int> Counts { get; } = BuildCounts(Results);

    /// <summary>
    /// 获取指定等级的结果计数。
    /// </summary>
    /// <param name="severity">要查询的严重等级。</param>
    /// <returns>该等级出现次数。</returns>
    public int CountOf(DiagnosticSeverity severity)
        => Counts.TryGetValue(severity, out var count) ? count : 0;

    private static IReadOnlyDictionary<DiagnosticSeverity, int> BuildCounts(IReadOnlyList<DiagnosticResult> results)
    {
        // NOTE: 预填全部等级键，保证 UI 读取任意等级计数时不需要判空。
        var counts = new Dictionary<DiagnosticSeverity, int>
        {
            [DiagnosticSeverity.Pass] = 0,
            [DiagnosticSeverity.Info] = 0,
            [DiagnosticSeverity.Warning] = 0,
            [DiagnosticSeverity.Error] = 0,
            [DiagnosticSeverity.Fatal] = 0,
        };

        foreach (var result in results)
        {
            counts[result.Severity]++;
        }

        return counts;
    }
}

/// <summary>
/// DevSwitch 期望的环境约定（变量名与托管 PATH 片段），对应设计文档第 8 节。
/// </summary>
/// <remarks>
/// 抽离为独立模型，便于 Doctor 检查项与测试共享同一份期望定义，并按 dataRoot 展开。
/// </remarks>
public sealed class DevSwitchEnvironmentExpectations
{
    /// <summary>
    /// 期望存在的 HKCU 环境变量名集合（默认集，不含可选兼容变量）。
    /// </summary>
    public IReadOnlyList<string> ExpectedVariableNames { get; }

    /// <summary>
    /// 期望出现在 PATH 中的 DevSwitch 托管片段（已展开 dataRoot）。
    /// </summary>
    public IReadOnlyList<string> ManagedPathSegments { get; }

    /// <summary>
    /// 使用默认期望集创建。
    /// </summary>
    /// <param name="dataRoot">DevSwitch 数据根目录，用于展开托管 PATH 片段。</param>
    public DevSwitchEnvironmentExpectations(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("Data root is required.", nameof(dataRoot));
        }

        // NOTE: 默认变量取自设计文档第 8 节；DEVSWITCH_HOME 指向 dataRoot，其余指向 current 入口。
        ExpectedVariableNames = new[] { "DEVSWITCH_HOME", "JAVA_HOME", "MAVEN_HOME", "GOROOT" };

        // shim 单目录方案：DevSwitch 只需 PATH 中存在唯一的 shims 目录即可覆盖 java/mvn/node/go 全部命令，
        // 不再要求 current\<type>\bin 多条片段。这样系统 PATH 只占 1 条，根治 2047 上限。
        ManagedPathSegments = new[]
        {
            Path.Combine(dataRoot, "shims"),
        };
    }
}

/// <summary>
/// 环境读取抽象。fake 实现可在测试中返回固定值，避免真实读注册表。
/// </summary>
public interface IEnvironmentReader
{
    /// <summary>
    /// 读取指定用户级环境变量值。
    /// </summary>
    /// <param name="name">环境变量名。</param>
    /// <returns>变量值；不存在时返回 null。</returns>
    string? GetVariable(string name);

    /// <summary>
    /// 读取用户级 PATH 拆分后的条目列表（保持原始顺序）。
    /// </summary>
    /// <returns>PATH 条目集合；无 PATH 时返回空集合。</returns>
    IReadOnlyList<string> GetPathEntries();

    /// <summary>
    /// 读取系统级 PATH 拆分后的条目列表（保持原始顺序）。Windows 新进程会先使用系统 PATH，再追加用户 PATH。
    /// </summary>
    /// <returns>系统 PATH 条目集合；无 PATH 或测试 fake 未覆盖时返回空集合。</returns>
    IReadOnlyList<string> GetMachinePathEntries() => Array.Empty<string>();

    /// <summary>
    /// 读取指定变量（例如 GOTOOLCHAIN）的值。语义同 <see cref="GetVariable"/>，
    /// 单列出来表达“Doctor 关注的可选诊断变量”。
    /// </summary>
    /// <param name="name">变量名。</param>
    /// <returns>变量值；不存在时返回 null。</returns>
    string? GetOptionalVariable(string name) => GetVariable(name);
}

/// <summary>
/// current 链接探测抽象，便于测试用 fake 替代真实文件系统/ helper inspect。
/// </summary>
public interface ICurrentLinkInspector
{
    /// <summary>
    /// 探测某 SDK 类型的 current 链接是否指向有效目标。
    /// </summary>
    /// <param name="sdkType">SDK 类型。</param>
    /// <param name="currentPath">current 入口路径，例如 dataRoot/current/java。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>探测结果。</returns>
    Task<CurrentLinkInspection> InspectAsync(SdkType sdkType, string currentPath, CancellationToken cancellationToken = default);
}

/// <summary>
/// current 链接探测结果。
/// </summary>
/// <param name="Exists">current 入口是否存在。</param>
/// <param name="TargetPath">链接指向的目标路径；不存在或非链接时为空。</param>
/// <param name="TargetExists">目标路径是否实际存在。</param>
public sealed record CurrentLinkInspection(bool Exists, string? TargetPath, bool TargetExists)
{
    /// <summary>
    /// 链接完整即“存在且目标存在”。
    /// </summary>
    public bool IsHealthy => Exists && TargetExists && !string.IsNullOrWhiteSpace(TargetPath);
}

/// <summary>
/// 命令执行抽象，用于解析版本、读取 npm prefix 等。fake 实现避免真实跑外部命令。
/// </summary>
public interface ICommandRunner
{
    /// <summary>
    /// 执行命令并返回结果。实现应对执行加超时，避免卡死 Doctor。
    /// </summary>
    /// <param name="fileName">可执行文件或命令入口。</param>
    /// <param name="arguments">命令参数。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>命令执行结果。</returns>
    Task<CommandResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken = default);
}

/// <summary>
/// 命令执行结果。
/// </summary>
/// <param name="Started">命令是否成功启动（未找到可执行文件时为 false）。</param>
/// <param name="ExitCode">退出码；未启动或超时时为 null。</param>
/// <param name="StandardOutput">标准输出文本。</param>
/// <param name="StandardError">标准错误文本。</param>
/// <param name="TimedOut">是否因超时被终止。</param>
public sealed record CommandResult(
    bool Started,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut)
{
    /// <summary>
    /// 命令是否被认为成功执行（已启动、未超时、退出码 0）。
    /// </summary>
    public bool Succeeded => Started && !TimedOut && ExitCode == 0;

    /// <summary>
    /// 合并标准输出与标准错误，便于从中提取版本号（部分命令把版本写到 stderr）。
    /// </summary>
    public string CombinedOutput => string.Concat(StandardOutput, StandardError);
}

/// <summary>
/// helper 可用性探测抽象。Doctor 只需要一个“能否连通”信号，
/// 故不直接依赖 <see cref="IHelperClient"/> 的具体操作，便于独立 fake。
/// </summary>
public interface IHelperPing
{
    /// <summary>
    /// 探测 helper 是否可用。
    /// </summary>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>true 表示 helper 可连通。</returns>
    Task<bool> PingAsync(CancellationToken cancellationToken = default);
}
