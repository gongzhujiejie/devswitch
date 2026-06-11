// 文件用途：验证 DownloadCompletionPipeline 的「校验 -> 解压 -> 登记」编排与失败短路。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit、System.Security.Cryptography
// NOTE: 合法授权学习使用，仅限本地环境。用假解压器与假登记器隔离 IO，只验证状态机推进。

using System.Security.Cryptography;
using DevSwitch.Core;
using DevSwitch.Downloader;
using Xunit;

namespace DevSwitch.Tests;

public sealed class DownloaderCompletionPipelineTests
{
    [Fact]
    public async Task CompleteRunsVerifyExtractRegisterWhenHashMatches()
    {
        // 校验通过 -> 解压 -> 登记 -> Completed。
        var bytes = System.Text.Encoding.UTF8.GetBytes("archive-bytes");
        var archivePath = await WriteTempFileAsync(bytes);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var extractor = new RecordingExtractor();
        var registrar = new RecordingRegistrar();
        var pipeline = new DownloadCompletionPipeline(extractor, registrar);

        var task = CreateTask(sha);
        var result = await pipeline.CompleteAsync(task, archivePath, Path.Combine(Path.GetDirectoryName(archivePath)!, "install"));

        Assert.Equal(DownloadStatus.Completed, result.Task.Status);
        Assert.Equal(sha, result.ActualSha256);
        Assert.Null(result.FailureReason);
        Assert.True(extractor.Called);
        Assert.NotNull(registrar.LastRegistration);
        Assert.Equal(task.Id, registrar.LastRegistration!.TaskId);
    }

    [Fact]
    public async Task CompleteFailsWithoutExtractWhenHashMismatch()
    {
        // 校验失败 -> Failed，且不解压、不登记（设计文档第 10 节第 3 步）。
        var archivePath = await WriteTempFileAsync(new byte[] { 1, 2, 3 });

        var extractor = new RecordingExtractor();
        var registrar = new RecordingRegistrar();
        var pipeline = new DownloadCompletionPipeline(extractor, registrar);

        var task = CreateTask(expectedSha256: new string('a', 64));
        var result = await pipeline.CompleteAsync(task, archivePath, "unused-dir");

        Assert.Equal(DownloadStatus.Failed, result.Task.Status);
        Assert.NotNull(result.FailureReason);
        Assert.False(extractor.Called);
        Assert.Null(registrar.LastRegistration);
    }

    [Fact]
    public async Task CompleteSkipsVerificationWhenNoExpectedHash()
    {
        // 任务无期望哈希时跳过校验，直接解压登记完成。
        var archivePath = await WriteTempFileAsync(new byte[] { 9 });
        var extractor = new RecordingExtractor();
        var registrar = new RecordingRegistrar();
        var pipeline = new DownloadCompletionPipeline(extractor, registrar);

        var result = await pipeline.CompleteAsync(CreateTask(expectedSha256: null), archivePath, "install-dir");

        Assert.Equal(DownloadStatus.Completed, result.Task.Status);
        Assert.Null(result.ActualSha256);
        Assert.True(extractor.Called);
    }

    [Fact]
    public async Task CompleteRegistersNestedSingleRootAsRealSdkPath()
    {
        // Temurin / Maven / Node / Go 的官方 zip 通常多带一层根目录（如 jdk8u472-b08）。
        // 解压后若 installDirectory 下只有一个子目录、无任何文件，应把这个子目录作为真实 SDK 根登记到 catalog，
        // 否则 bin/java.exe 等关键文件无法被找到，切换/检测都会失败。
        var bytes = System.Text.Encoding.UTF8.GetBytes("nested-zip");
        var archivePath = await WriteTempFileAsync(bytes);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var installRoot = Path.Combine(Path.GetDirectoryName(archivePath)!, "install");
        var extractor = new SimulatedExtractor(targetDir =>
        {
            // 模拟 zip 内单根目录（jdk8u472-b08）解压后的目录结构。
            var nested = Path.Combine(targetDir, "jdk8u472-b08");
            Directory.CreateDirectory(Path.Combine(nested, "bin"));
            File.WriteAllText(Path.Combine(nested, "release"), "JAVA_VERSION=\"1.8.0_472\"");
            File.WriteAllText(Path.Combine(nested, "bin", "java.exe"), "stub");
        });
        var registrar = new RecordingRegistrar();
        var pipeline = new DownloadCompletionPipeline(extractor, registrar);

        var result = await pipeline.CompleteAsync(CreateTask(sha), archivePath, installRoot);

        Assert.Equal(DownloadStatus.Completed, result.Task.Status);
        Assert.NotNull(registrar.LastRegistration);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(installRoot, "jdk8u472-b08")),
            Path.GetFullPath(registrar.LastRegistration!.InstallDirectory));
    }

    [Fact]
    public async Task CompleteKeepsInstallDirectoryWhenZipHasNoSingleRoot()
    {
        // zip 内无统一外层目录（直接 bin/、release 散在根下）时不应下探，否则会少注册一层。
        var bytes = System.Text.Encoding.UTF8.GetBytes("flat-zip");
        var archivePath = await WriteTempFileAsync(bytes);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var installRoot = Path.Combine(Path.GetDirectoryName(archivePath)!, "install-flat");
        var extractor = new SimulatedExtractor(targetDir =>
        {
            // 解压后顶层既有目录（bin）也有文件（release），不构成「单根」，保持原 installDirectory。
            Directory.CreateDirectory(Path.Combine(targetDir, "bin"));
            File.WriteAllText(Path.Combine(targetDir, "release"), "JAVA_VERSION=\"1.8.0_472\"");
        });
        var registrar = new RecordingRegistrar();
        var pipeline = new DownloadCompletionPipeline(extractor, registrar);

        var result = await pipeline.CompleteAsync(CreateTask(sha), archivePath, installRoot);

        Assert.Equal(DownloadStatus.Completed, result.Task.Status);
        Assert.NotNull(registrar.LastRegistration);
        Assert.Equal(
            Path.GetFullPath(installRoot),
            Path.GetFullPath(registrar.LastRegistration!.InstallDirectory));
    }

    private static DownloadTask CreateTask(string? expectedSha256)
    {
        return DownloadTask.CreateQueued(
            id: "task-1",
            sdkType: SdkType.Go,
            version: "1.22.0",
            distribution: "go",
            arch: SdkArchitecture.X64,
            url: "https://example.invalid/go.zip",
            expectedSha256: expectedSha256);
    }

    private static async Task<string> WriteTempFileAsync(byte[] content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "archive.zip");
        await File.WriteAllBytesAsync(path, content);
        return path;
    }

    private sealed class RecordingExtractor : IArchiveExtractor
    {
        public bool Called { get; private set; }

        public Task ExtractAsync(string archivePath, string destinationDirectory, CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 测试桩解压器：用回调在 destinationDirectory 中模拟真实解压后的目录结构，
    /// 用于验证 pipeline 后续「下探单根」逻辑能否识别 zip 内的嵌套布局。
    /// </summary>
    private sealed class SimulatedExtractor : IArchiveExtractor
    {
        private readonly Action<string> populate;

        public SimulatedExtractor(Action<string> populate)
        {
            this.populate = populate;
        }

        public Task ExtractAsync(string archivePath, string destinationDirectory, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(destinationDirectory);
            populate(destinationDirectory);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRegistrar : IManagedSdkRegistrar
    {
        public ManagedSdkRegistration? LastRegistration { get; private set; }

        public Task RegisterAsync(ManagedSdkRegistration registration, CancellationToken cancellationToken = default)
        {
            LastRegistration = registration;
            return Task.CompletedTask;
        }
    }
}
