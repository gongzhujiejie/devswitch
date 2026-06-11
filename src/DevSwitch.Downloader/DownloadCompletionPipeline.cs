// 文件用途：下载完成流程编排器，串联「校验 → 解压 → 登记」三步。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：DevSwitch.Core
// NOTE: 合法授权学习使用，仅限本地环境。
//       对应设计文档第 10 节下载完成流程：校验失败 -> failed，不解压不登记；校验成功 -> 解压 -> 登记。

using DevSwitch.Core;

namespace DevSwitch.Downloader;

/// <summary>
/// 完成流程结果。
/// </summary>
/// <param name="Task">更新后的下载任务（含最终状态）。</param>
/// <param name="ActualSha256">校验时实际算出的 SHA256；未执行校验时为 null。</param>
/// <param name="FailureReason">失败原因摘要；成功时为 null。</param>
public sealed record DownloadCompletionResult(
    DownloadTask Task,
    string? ActualSha256,
    string? FailureReason);

/// <summary>
/// 下载完成流程编排器。
/// 职责：在文件传输完成后，按设计文档执行 SHA256 校验、解压到目标目录、登记托管 SDK。
/// 不负责字节传输（由 DownloadEngine 完成），只负责完成阶段的状态机推进。
/// </summary>
public sealed class DownloadCompletionPipeline
{
    private readonly IArchiveExtractor _extractor;
    private readonly IManagedSdkRegistrar _registrar;

    /// <summary>
    /// 构造编排器。
    /// </summary>
    /// <param name="extractor">解压器实现。</param>
    /// <param name="registrar">托管 SDK 登记对接点。</param>
    public DownloadCompletionPipeline(IArchiveExtractor extractor, IManagedSdkRegistrar registrar)
    {
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _registrar = registrar ?? throw new ArgumentNullException(nameof(registrar));
    }

    /// <summary>
    /// 执行完成流程：校验 → 解压 → 登记。
    /// </summary>
    /// <param name="task">已完成传输的下载任务。</param>
    /// <param name="archivePath">下载得到的安装包路径。</param>
    /// <param name="installDirectory">解压目标目录（托管 SDK 根目录）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>完成流程结果，含最终任务状态。</returns>
    public async Task<DownloadCompletionResult> CompleteAsync(
        DownloadTask task,
        string archivePath,
        string installDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        try
        {
            // 第一步：SHA256 校验（仅当任务带有期望值时执行）。
            string? actualSha256 = null;
            if (!string.IsNullOrWhiteSpace(task.ExpectedSha256))
            {
                var verifyingTask = task with { Status = DownloadStatus.Verifying };
                var verification = await Sha256Verifier
                    .VerifyAsync(archivePath, verifyingTask.ExpectedSha256!, cancellationToken)
                    .ConfigureAwait(false);
                actualSha256 = verification.ActualSha256;

                // 校验失败：标记 failed，不解压、不登记（设计文档第 10 节第 3 步）。
                if (!verification.IsMatch)
                {
                    return new DownloadCompletionResult(
                        verifyingTask with { Status = DownloadStatus.Failed },
                        actualSha256,
                        $"SHA256 mismatch. expected={verification.ExpectedSha256}, actual={verification.ActualSha256}");
                }
            }

            // 第二步：解压到目标目录。
            var extractingTask = task with { Status = DownloadStatus.Extracting };
            await _extractor.ExtractAsync(archivePath, installDirectory, cancellationToken).ConfigureAwait(false);

            // 第二步补：下探 zip 内的「单根目录」。
            // Temurin / Maven / Node / Go 的官方包通常都多带一层根目录（如 jdk8u472-b08、apache-maven-3.9.9、
            // node-v22.11.0-win-x64、go），如果直接把外层 installDirectory 登记进 catalog，
            // bin/java.exe 等关键文件路径就会少一层、检测和切换都会失败。
            // 规则：仅当 installDirectory 直接孩子是 1 个目录 + 0 个文件时下探一层；其它形态保持原路径，
            // 避免误把多顶层（散平的 zip）当成单根。
            string sdkRootDirectory = ResolveSdkRootDirectory(installDirectory);

            // 第三步：登记托管 SDK（真实版本识别与 sdks.json 写入交给 Core 实现）。
            var registration = new ManagedSdkRegistration(
                TaskId: task.Id,
                SdkType: task.SdkType,
                Version: task.Version,
                Distribution: task.Distribution,
                Arch: task.Arch,
                InstallDirectory: sdkRootDirectory);
            await _registrar.RegisterAsync(registration, cancellationToken).ConfigureAwait(false);

            return new DownloadCompletionResult(
                extractingTask with { Status = DownloadStatus.Completed },
                actualSha256,
                FailureReason: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 取消时回到 paused，保留安装包以便后续重试完成流程。
            return new DownloadCompletionResult(task with { Status = DownloadStatus.Paused }, null, "Cancelled.");
        }
        catch (Exception ex)
        {
            // 解压或登记异常统一标记失败，附带原因摘要。
            return new DownloadCompletionResult(task with { Status = DownloadStatus.Failed }, null, ex.Message);
        }
    }

    /// <summary>
    /// 若 <paramref name="installDirectory"/> 解压后恰好「只有一个子目录、没有任何文件」，
    /// 说明 zip 内多带了一层根目录（典型如 Temurin <c>jdk8u472-b08</c>），返回这个真实 SDK 根；
    /// 否则原样返回 <paramref name="installDirectory"/>。
    /// </summary>
    /// <remarks>
    /// 设计要点：
    /// - 仅识别「1 子目录 + 0 文件」这一种形态，避免误伤散平 zip（带多个顶层条目）。
    /// - 目录不存在或访问异常时保守返回原路径，让登记仍按原行为继续，不阻塞下载流程。
    /// </remarks>
    private static string ResolveSdkRootDirectory(string installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory) || !Directory.Exists(installDirectory))
        {
            return installDirectory;
        }

        try
        {
            var entries = Directory.GetFileSystemEntries(installDirectory);
            if (entries.Length != 1)
            {
                return installDirectory;
            }

            var only = entries[0];
            if (!Directory.Exists(only))
            {
                // 顶层只有一个文件不算单根，保持原路径。
                return installDirectory;
            }

            return only;
        }
        catch (UnauthorizedAccessException)
        {
            return installDirectory;
        }
        catch (IOException)
        {
            return installDirectory;
        }
    }
}
