// 文件用途：Rustup installer 下载完成流程，串联「校验 → 运行 rustup-init → 定位 toolchain → 登记」。
// 创建/修改日期：2026-06-12
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：DevSwitch.Core
// NOTE: 合法授权学习使用，仅限本地环境。仅运行用户明确下载的 rustup-init.exe，且通过 --no-modify-path 避免修改系统 PATH。

using DevSwitch.Core;

namespace DevSwitch.Downloader;

/// <summary>
/// rustup-init.exe 执行结果。
/// </summary>
public sealed record RustupInstallerResult(
    bool Started,
    int? ExitCode,
    string StdOut,
    string StdErr,
    bool TimedOut);

/// <summary>
/// rustup-init.exe 执行抽象，便于 App 层接入真实进程、测试层注入 fake。
/// </summary>
public interface IRustupInstallerRunner
{
    /// <summary>
    /// 运行 rustup installer。
    /// </summary>
    Task<RustupInstallerResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Rustup 下载完成流程编排器。
/// </summary>
public sealed class RustupInstallCompletionPipeline
{
    private static readonly TimeSpan InstallerTimeout = TimeSpan.FromMinutes(15);

    private readonly IRustupInstallerRunner runner;
    private readonly IManagedSdkRegistrar registrar;

    /// <summary>
    /// 创建 Rustup 完成流程。
    /// </summary>
    public RustupInstallCompletionPipeline(IRustupInstallerRunner runner, IManagedSdkRegistrar registrar)
    {
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.registrar = registrar ?? throw new ArgumentNullException(nameof(registrar));
    }

    /// <summary>
    /// 执行完成流程：校验 rustup-init.exe、隔离安装 stable toolchain、登记真实 toolchain root。
    /// </summary>
    public async Task<DownloadCompletionResult> CompleteAsync(
        DownloadTask task,
        string installerPath,
        string installDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        try
        {
            string? actualSha256 = null;
            if (!string.IsNullOrWhiteSpace(task.ExpectedSha256))
            {
                var verifyingTask = task with { Status = DownloadStatus.Verifying };
                var verification = await Sha256Verifier
                    .VerifyAsync(installerPath, task.ExpectedSha256!, cancellationToken)
                    .ConfigureAwait(false);
                actualSha256 = verification.ActualSha256;
                if (!verification.IsMatch)
                {
                    return new DownloadCompletionResult(
                        verifyingTask with { Status = DownloadStatus.Failed },
                        actualSha256,
                        $"SHA256 mismatch. expected={verification.ExpectedSha256}, actual={verification.ActualSha256}");
                }
            }

            var installingTask = task with { Status = DownloadStatus.Extracting };
            Directory.CreateDirectory(installDirectory);

            var triple = ToWindowsMsvcTriple(task.Arch);
            var toolchain = $"stable-{triple}";
            var cargoHome = Path.Combine(installDirectory, "cargo");
            var rustupHome = Path.Combine(installDirectory, "rustup");
            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["CARGO_HOME"] = cargoHome,
                ["RUSTUP_HOME"] = rustupHome,
            };
            var arguments = new[] { "-y", "--no-modify-path", "--profile", "minimal", "--default-toolchain", toolchain };

            var result = await runner
                .RunAsync(installerPath, arguments, environment, InstallerTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Started)
            {
                return new DownloadCompletionResult(installingTask with { Status = DownloadStatus.Failed }, actualSha256, "rustup installer did not start.");
            }

            if (result.TimedOut)
            {
                return new DownloadCompletionResult(installingTask with { Status = DownloadStatus.Failed }, actualSha256, "rustup installer timed out.");
            }

            if (result.ExitCode is not 0)
            {
                return new DownloadCompletionResult(
                    installingTask with { Status = DownloadStatus.Failed },
                    actualSha256,
                    $"rustup installer exited with code {result.ExitCode?.ToString() ?? "unknown"}.");
            }

            var toolchainRoot = Path.Combine(rustupHome, "toolchains", toolchain);
            if (!Directory.Exists(toolchainRoot))
            {
                return new DownloadCompletionResult(
                    installingTask with { Status = DownloadStatus.Failed },
                    actualSha256,
                    $"rustup toolchain root not found: {toolchainRoot}");
            }

            var registration = new ManagedSdkRegistration(
                TaskId: task.Id,
                SdkType: SdkType.Rust,
                Version: task.Version,
                Distribution: task.Distribution,
                Arch: task.Arch,
                InstallDirectory: toolchainRoot);
            await registrar.RegisterAsync(registration, cancellationToken).ConfigureAwait(false);

            return new DownloadCompletionResult(installingTask with { Status = DownloadStatus.Completed }, actualSha256, FailureReason: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new DownloadCompletionResult(task with { Status = DownloadStatus.Paused }, null, "Cancelled.");
        }
        catch (Exception ex)
        {
            return new DownloadCompletionResult(task with { Status = DownloadStatus.Failed }, null, ex.Message);
        }
    }

    private static string ToWindowsMsvcTriple(SdkArchitecture architecture)
    {
        return architecture switch
        {
            SdkArchitecture.Arm64 => "aarch64-pc-windows-msvc",
            _ => "x86_64-pc-windows-msvc",
        };
    }
}
