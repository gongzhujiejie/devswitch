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

            // 第三步：登记托管 SDK（真实版本识别与 sdks.json 写入交给 Core 实现）。
            var registration = new ManagedSdkRegistration(
                TaskId: task.Id,
                SdkType: task.SdkType,
                Version: task.Version,
                Distribution: task.Distribution,
                Arch: task.Arch,
                InstallDirectory: installDirectory);
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
}
