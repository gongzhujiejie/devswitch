// 文件用途：用假 HttpMessageHandler 驱动 DownloadEngine，验证分块下载、断点续传与单线程回退。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit、System.Net.Http
// NOTE: 合法授权学习使用，仅限本地环境。全程使用内存假服务器，不发起真实网络请求。

using DevSwitch.Core;
using DevSwitch.Downloader;
using Xunit;

namespace DevSwitch.Tests;

public sealed class DownloaderEngineTests
{
    [Fact]
    public async Task DownloadChunkedWritesCompleteFileWhenServerSupportsRange()
    {
        // 服务器支持 Range：引擎应分块下载并拼出完整文件。
        var content = CreateSequentialBytes(1000);
        var handler = new FakeByteRangeHandler(content, supportsRange: true);
        var engine = new DownloadEngine(handler, new DownloadEngineOptions { Parallelism = 4, ProgressThrottleMilliseconds = 0 });

        var task = CreateTask();
        var destination = CreateTempFilePath();

        var result = await engine.DownloadAsync(task, destination);

        Assert.Equal(content.Length, result.BytesTotal);
        Assert.Equal(content.Length, result.BytesCompleted);
        Assert.Equal(4, result.Chunks.Count);
        Assert.Equal(content, await File.ReadAllBytesAsync(destination));
        // 多分块应产生多个 Range 请求（探针 + 4 个分块）。
        Assert.True(handler.RangeRequests.Count >= 4);
    }

    [Fact]
    public async Task DownloadFallsBackToSingleStreamWhenRangeUnsupported()
    {
        // 服务器不支持 Range：应回退单线程整下，文件仍然完整，无分块。
        var content = CreateSequentialBytes(777);
        var handler = new FakeByteRangeHandler(content, supportsRange: false);
        var engine = new DownloadEngine(handler, new DownloadEngineOptions { ProgressThrottleMilliseconds = 0 });

        var task = CreateTask();
        var destination = CreateTempFilePath();

        var result = await engine.DownloadAsync(task, destination);

        Assert.Empty(result.Chunks);
        Assert.Equal(content.Length, result.BytesCompleted);
        Assert.Equal(content, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task DownloadResumesFromCompletedChunks()
    {
        // 断点续传：预置部分已完成的 chunk，引擎应跳过已完成块，只补齐剩余块。
        var content = CreateSequentialBytes(1000);
        var handler = new FakeByteRangeHandler(content, supportsRange: true);
        var engine = new DownloadEngine(handler, new DownloadEngineOptions { Parallelism = 4, ProgressThrottleMilliseconds = 0 });

        var destination = CreateTempFilePath();

        // 预先把前半部分字节写入目标文件（模拟上次下载到一半）。
        var planned = ChunkPlanner.Plan(content.Length, 4);
        using (var handle = File.OpenHandle(destination, FileMode.Create, FileAccess.Write))
        {
            RandomAccess.SetLength(handle, content.Length);
            // 写入第 0、1 块对应的真实字节。
            for (var i = 0; i < 2; i++)
            {
                var slice = content[(int)planned[i].Start..((int)planned[i].End + 1)];
                RandomAccess.Write(handle, slice, planned[i].Start);
            }
        }

        // 构造续传任务：前两块标记已完成。
        var resumeChunks = planned.Select((c, i) =>
            i < 2 ? c with { BytesCompleted = c.Length } : c).ToArray();
        var task = CreateTask() with
        {
            BytesTotal = content.Length,
            BytesCompleted = resumeChunks.Take(2).Sum(c => c.Length),
            Chunks = resumeChunks,
        };

        var result = await engine.DownloadAsync(task, destination);

        Assert.Equal(content.Length, result.BytesCompleted);
        Assert.Equal(content, await File.ReadAllBytesAsync(destination));
        // 续传只请求未完成的 2 个分块（外加 1 个探针），不应重复下载已完成块。
        Assert.Equal(3, handler.RangeRequests.Count);
    }

    [Fact]
    public async Task DownloadReportsProgress()
    {
        // 进度回调应被触发，且最终一帧达到总字节数。
        var content = CreateSequentialBytes(500);
        var handler = new FakeByteRangeHandler(content, supportsRange: true);
        var engine = new DownloadEngine(handler, new DownloadEngineOptions { Parallelism = 2, ProgressThrottleMilliseconds = 0 });

        var snapshots = new List<DownloadProgress>();
        var progress = new SynchronousProgress(snapshots);

        var result = await engine.DownloadAsync(CreateTask(), CreateTempFilePath(), progress);

        Assert.NotEmpty(snapshots);
        Assert.Equal(content.Length, result.BytesCompleted);
        Assert.Contains(snapshots, s => s.BytesCompleted == content.Length);
    }

    private static DownloadTask CreateTask()
    {
        return DownloadTask.CreateQueued(
            id: "download-test",
            sdkType: SdkType.Node,
            version: "22.0.0",
            distribution: "nodejs",
            arch: SdkArchitecture.X64,
            url: "https://example.invalid/node.zip",
            expectedSha256: null);
    }

    private static byte[] CreateSequentialBytes(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i % 256);
        }

        return bytes;
    }

    private static string CreateTempFilePath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "download.bin");
    }

    /// <summary>
    /// 同步收集进度快照的 IProgress 实现，避免 SynchronizationContext 导致回调丢失。
    /// </summary>
    private sealed class SynchronousProgress : IProgress<DownloadProgress>
    {
        private readonly List<DownloadProgress> _snapshots;
        private readonly object _gate = new();

        public SynchronousProgress(List<DownloadProgress> snapshots) => _snapshots = snapshots;

        public void Report(DownloadProgress value)
        {
            lock (_gate)
            {
                _snapshots.Add(value);
            }
        }
    }
}
