// 文件用途：导入/下载登记阶段的 SDK 自动验证服务。
//           在用户显式导入或下载完成后运行一次版本命令，提取真实版本并推导可用状态。
// 创建/修改日期：2026-06-12
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：DevSwitch.Core 内部验证模型与 BCL
// NOTE: 合法授权学习使用，仅限本地环境。
//       - 只在用户显式导入/下载完成时运行，不做后台批量扫描，避免 UI 卡顿。
//       - 运行命令前先做关键文件结构检查；结构不完整时不启动外部进程。

namespace DevSwitch.Core;

/// <summary>
/// SDK 导入自动验证结果。
/// </summary>
/// <param name="Record">应用验证结果后的 SDK 记录。</param>
/// <param name="Command">命令验证结果；结构不完整时为 null。</param>
/// <param name="StructureOk">路径存在且关键文件齐全。</param>
/// <param name="MissingKeyFiles">缺失的关键文件相对路径。</param>
public sealed record SdkImportVerificationResult(
    SdkRecord Record,
    CommandVerificationResult? Command,
    bool StructureOk,
    IReadOnlyList<string> MissingKeyFiles);

/// <summary>
/// 导入/下载登记时使用的自动验证服务。
/// </summary>
public sealed class SdkImportVerificationService
{
    private readonly ISdkCommandRunner commandRunner;
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 创建导入验证服务。
    /// </summary>
    /// <param name="commandRunner">可注入的命令执行器，测试中用 fake，App 中用真实进程 runner。</param>
    public SdkImportVerificationService(ISdkCommandRunner commandRunner)
    {
        this.commandRunner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));
    }

    /// <summary>
    /// 对 SDK 记录执行一次结构检查和命令验证，返回应用验证结果后的记录。
    /// </summary>
    public async Task<SdkImportVerificationResult> VerifyAsync(SdkRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        bool pathExists = !string.IsNullOrWhiteSpace(record.Path) && Directory.Exists(record.Path);
        var missingKeyFiles = pathExists
            ? SdkVerificationService.GetMissingKeyFiles(record.Type, record.Path)
            : SdkVerificationService.GetRequiredKeyFiles(record.Type);
        bool structureOk = pathExists && missingKeyFiles.Count == 0 && record.Type != SdkType.Unknown;
        var verifiedAt = DateTimeOffset.UtcNow;

        if (!structureOk)
        {
            return new SdkImportVerificationResult(
                record with
                {
                    Status = SdkRecordStatus.Unavailable,
                    LastVerifiedAt = verifiedAt,
                },
                Command: null,
                StructureOk: false,
                MissingKeyFiles: missingKeyFiles);
        }

        var (fileName, arguments) = SdkVerificationService.ResolveVersionCommand(record.Type, record.Path);
        var commandResult = await commandRunner.RunAsync(fileName, arguments, DefaultCommandTimeout, cancellationToken)
            .ConfigureAwait(false);
        var command = BuildCommandVerificationResult(record.Type, fileName, commandResult);

        var updated = command.Outcome == CommandVerificationOutcome.Verified && !string.IsNullOrWhiteSpace(command.ParsedVersion)
            ? record with
            {
                Version = command.ParsedVersion!,
                Status = SdkRecordStatus.Usable,
                LastVerifiedAt = verifiedAt,
            }
            : record with
            {
                Status = SdkRecordStatus.Unavailable,
                LastVerifiedAt = verifiedAt,
            };

        return new SdkImportVerificationResult(updated, command, true, missingKeyFiles);
    }

    private static CommandVerificationResult BuildCommandVerificationResult(SdkType type, string fileName, SdkCommandResult result)
    {
        if (!result.Started)
        {
            return new CommandVerificationResult(
                CommandVerificationOutcome.NotStarted,
                ParsedVersion: null,
                FileName: fileName,
                ExitCode: result.ExitCode,
                StdOut: result.StdOut,
                StdErr: result.StdErr,
                Message: $"Command '{fileName}' did not start.");
        }

        if (result.TimedOut)
        {
            return new CommandVerificationResult(
                CommandVerificationOutcome.TimedOut,
                ParsedVersion: null,
                FileName: fileName,
                ExitCode: result.ExitCode,
                StdOut: result.StdOut,
                StdErr: result.StdErr,
                Message: $"Command '{fileName}' timed out.");
        }

        if (result.ExitCode is not 0)
        {
            return new CommandVerificationResult(
                CommandVerificationOutcome.NonZeroExit,
                ParsedVersion: null,
                FileName: fileName,
                ExitCode: result.ExitCode,
                StdOut: result.StdOut,
                StdErr: result.StdErr,
                Message: $"Command '{fileName}' exited with code {result.ExitCode?.ToString() ?? "unknown"}.");
        }

        var parsed = SdkVerificationService.ParseVersion(type, result.StdOut, result.StdErr);
        if (string.IsNullOrWhiteSpace(parsed))
        {
            return new CommandVerificationResult(
                CommandVerificationOutcome.ParseFailed,
                ParsedVersion: null,
                FileName: fileName,
                ExitCode: result.ExitCode,
                StdOut: result.StdOut,
                StdErr: result.StdErr,
                Message: $"Could not parse version from '{fileName}' output.");
        }

        return new CommandVerificationResult(
            CommandVerificationOutcome.Verified,
            ParsedVersion: parsed,
            FileName: fileName,
            ExitCode: result.ExitCode,
            StdOut: result.StdOut,
            StdErr: result.StdErr,
            Message: $"Resolved version {parsed}.");
    }
}
