// 文件用途：定义 DevSwitch「SDK 验证」能力的公开模型与抽象接口（轻量验证 + 手动命令验证）。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：仅 BCL（System、System.Collections.Generic、System.Threading）
// NOTE: 合法授权学习使用，仅限本地环境。本文件不运行任何外部命令，仅定义类型与抽象。

namespace DevSwitch.Core;

/// <summary>
/// current 入口链接探测结果。
/// </summary>
/// <param name="Exists">current 入口是否存在（链接已建立）。</param>
/// <param name="TargetPath">current 入口当前指向的目标 SDK 根目录；不存在时为 null。</param>
public sealed record CurrentLinkInfo(bool Exists, string? TargetPath)
{
    /// <summary>
    /// current 入口缺失常量。
    /// </summary>
    public static CurrentLinkInfo Missing { get; } = new(Exists: false, TargetPath: null);
}

/// <summary>
/// current 入口链接探测抽象。
/// 自定义于验证能力，便于测试用 fake 替代，且不改动 SdkSwitchService 的 InspectLink 行为。
/// NOTE: 命名为 ISdkLinkInspector 以避免与 DoctorModels.cs 中签名不同的 ICurrentLinkInspector 冲突。
/// </summary>
public interface ISdkLinkInspector
{
    /// <summary>
    /// 探测指定 SDK 类型的 current 入口当前指向。
    /// </summary>
    /// <param name="sdkType">SDK 类型。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>current 入口是否存在及其目标路径。</returns>
    Task<CurrentLinkInfo> InspectAsync(SdkType sdkType, CancellationToken cancellationToken = default);
}

/// <summary>
/// 外部命令执行结果。
/// </summary>
/// <param name="Started">进程是否成功启动；启动失败（如可执行文件不存在）为 false。</param>
/// <param name="ExitCode">进程退出码；未启动或超时被终止时可能为 null。</param>
/// <param name="StdOut">标准输出全文。</param>
/// <param name="StdErr">标准错误全文。</param>
/// <param name="TimedOut">是否因超时被终止。</param>
public sealed record SdkCommandResult(
    bool Started,
    int? ExitCode,
    string StdOut,
    string StdErr,
    bool TimedOut);

/// <summary>
/// 外部命令执行抽象。
/// 由具体实现负责进程启动、输出收集与超时控制；验证服务只透传取消令牌与超时值。
/// NOTE: 命名为 ISdkCommandRunner 以避免与 DoctorModels.cs 中签名不同的 ICommandRunner 冲突。
/// </summary>
public interface ISdkCommandRunner
{
    /// <summary>
    /// 运行一个外部命令并收集输出。
    /// </summary>
    /// <param name="fileName">可执行文件名或命令名（依赖当前终端 PATH 解析）。</param>
    /// <param name="arguments">命令参数列表。</param>
    /// <param name="timeout">命令超时时间，超时由实现负责终止进程。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命令执行结果，包含是否启动、退出码、输出与是否超时。</returns>
    Task<SdkCommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>
/// 轻量验证结果：只确认 current 入口指向与关键文件存在，不运行外部命令。
/// </summary>
/// <param name="Status">推导出的版本状态：使用中(Active)/可用(Usable)/不可用(Unavailable)。</param>
/// <param name="IsCurrent">该记录是否为当前 current 指向的 SDK。</param>
/// <param name="PathExists">SDK 根目录是否存在。</param>
/// <param name="MissingKeyFiles">缺失的关键命令文件相对路径列表；齐全时为空。</param>
public sealed record LightweightVerificationResult(
    SdkRecordStatus Status,
    bool IsCurrent,
    bool PathExists,
    IReadOnlyList<string> MissingKeyFiles);

/// <summary>
/// 手动命令验证的结果分类。
/// </summary>
public enum CommandVerificationOutcome
{
    /// <summary>
    /// 命令成功启动、退出码为 0 且解析出版本号。
    /// </summary>
    Verified,

    /// <summary>
    /// 命令成功执行但无法从输出中解析出版本号。
    /// </summary>
    ParseFailed,

    /// <summary>
    /// 命令未能启动（如可执行文件不在 PATH 中）。
    /// </summary>
    NotStarted,

    /// <summary>
    /// 命令执行超时被终止。
    /// </summary>
    TimedOut,

    /// <summary>
    /// 命令以非零退出码结束。
    /// </summary>
    NonZeroExit,
}

/// <summary>
/// 手动命令验证结果。
/// </summary>
/// <param name="Outcome">结果分类。</param>
/// <param name="ParsedVersion">解析出的版本号；未解析出时为 null。</param>
/// <param name="FileName">实际运行的命令名。</param>
/// <param name="ExitCode">命令退出码；未启动或超时时可能为 null。</param>
/// <param name="StdOut">标准输出全文。</param>
/// <param name="StdErr">标准错误全文（Java 版本信息位于此处）。</param>
/// <param name="Message">面向 UI/日志的简短说明。</param>
public sealed record CommandVerificationResult(
    CommandVerificationOutcome Outcome,
    string? ParsedVersion,
    string FileName,
    int? ExitCode,
    string StdOut,
    string StdErr,
    string Message);
