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
