// 文件用途：验证 RustupInstallCompletionPipeline 的完成阶段行为。
// 创建/修改日期：2026-06-12
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit、DevSwitch.Downloader、DevSwitch.Core
// NOTE: 合法授权学习使用，仅限本地环境。本测试不运行真实 rustup，只用 fake runner 记录参数并创建临时 toolchain 目录。

using System.Security.Cryptography;
using DevSwitch.Core;
using DevSwitch.Downloader;
using Xunit;

namespace DevSwitch.Tests;

public sealed class DownloaderRustupInstallCompletionPipelineTests
{
    [Fact]
    public async Task CompleteAsyncRunsRustupWithIsolatedEnvironmentAndRegistersToolchainRoot()
    {
        var tempRoot = CreateTemporaryDirectory();
        var installerPath = Path.Combine(tempRoot, "rustup-init.exe");
        await File.WriteAllTextAsync(installerPath, "fake rustup installer");
        var expectedSha = await ComputeSha256Async(installerPath);
        var installDirectory = Path.Combine(tempRoot, "rustup-stable-x64");
        var task = DownloadTask.CreateQueued(
            id: "rust-task",
            sdkType: SdkType.Rust,
            version: "stable",
            distribution: "rustup",
            arch: SdkArchitecture.X64,
            url: "https://static.rust-lang.org/rustup/dist/x86_64-pc-windows-msvc/rustup-init.exe",
            expectedSha256: expectedSha);
        var runner = new FakeRustupInstallerRunner(exitCode: 0);
        var registrar = new FakeManagedSdkRegistrar();
        var pipeline = new RustupInstallCompletionPipeline(runner, registrar);

        var result = await pipeline.CompleteAsync(task, installerPath, installDirectory);

        Assert.Equal(DownloadStatus.Completed, result.Task.Status);
        Assert.Null(result.FailureReason);
        Assert.Equal(installerPath, runner.FileName);
        Assert.Equal(new[] { "-y", "--no-modify-path", "--profile", "minimal", "--default-toolchain", "stable-x86_64-pc-windows-msvc" }, runner.Arguments);
        Assert.Equal(Path.Combine(installDirectory, "cargo"), runner.Environment["CARGO_HOME"]);
        Assert.Equal(Path.Combine(installDirectory, "rustup"), runner.Environment["RUSTUP_HOME"]);
        var registration = Assert.Single(registrar.Registrations);
        Assert.Equal(SdkType.Rust, registration.SdkType);
        Assert.Equal(Path.Combine(installDirectory, "rustup", "toolchains", "stable-x86_64-pc-windows-msvc"), registration.InstallDirectory);
    }

    [Fact]
    public async Task CompleteAsyncFailsWhenInstallerReturnsNonZeroExit()
    {
        var tempRoot = CreateTemporaryDirectory();
        var installerPath = Path.Combine(tempRoot, "rustup-init.exe");
        await File.WriteAllTextAsync(installerPath, "fake rustup installer");
        var installDirectory = Path.Combine(tempRoot, "rustup-stable-x64");
        var task = DownloadTask.CreateQueued("rust-task", SdkType.Rust, "stable", "rustup", SdkArchitecture.X64, "https://example.test/rustup-init.exe", expectedSha256: null);
        var runner = new FakeRustupInstallerRunner(exitCode: 1);
        var registrar = new FakeManagedSdkRegistrar();
        var pipeline = new RustupInstallCompletionPipeline(runner, registrar);

        var result = await pipeline.CompleteAsync(task, installerPath, installDirectory);

        Assert.Equal(DownloadStatus.Failed, result.Task.Status);
        Assert.Contains("rustup installer exited with code 1", result.FailureReason);
        Assert.Empty(registrar.Registrations);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<string> ComputeSha256Async(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        var bytes = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed class FakeRustupInstallerRunner(int exitCode) : IRustupInstallerRunner
    {
        public string? FileName { get; private set; }

        public IReadOnlyList<string> Arguments { get; private set; } = Array.Empty<string>();

        public IReadOnlyDictionary<string, string> Environment { get; private set; } = new Dictionary<string, string>();

        public Task<RustupInstallerResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string> environment,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            FileName = fileName;
            Arguments = arguments.ToArray();
            Environment = new Dictionary<string, string>(environment);

            if (exitCode == 0)
            {
                var rustupHome = environment["RUSTUP_HOME"];
                var toolchainRoot = Path.Combine(rustupHome, "toolchains", "stable-x86_64-pc-windows-msvc");
                Directory.CreateDirectory(Path.Combine(toolchainRoot, "bin"));
                File.WriteAllText(Path.Combine(toolchainRoot, "bin", "rustc.exe"), string.Empty);
                File.WriteAllText(Path.Combine(toolchainRoot, "bin", "cargo.exe"), string.Empty);
                File.WriteAllText(Path.Combine(toolchainRoot, "bin", "rustdoc.exe"), string.Empty);
            }

            return Task.FromResult(new RustupInstallerResult(Started: true, ExitCode: exitCode, StdOut: string.Empty, StdErr: string.Empty, TimedOut: false));
        }
    }

    private sealed class FakeManagedSdkRegistrar : IManagedSdkRegistrar
    {
        public List<ManagedSdkRegistration> Registrations { get; } = new();

        public Task RegisterAsync(ManagedSdkRegistration registration, CancellationToken cancellationToken = default)
        {
            Registrations.Add(registration);
            return Task.CompletedTask;
        }
    }
}
