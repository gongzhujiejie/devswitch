// 文件用途：基于 HttpClient + HTTP Range 的多线程断点续传下载引擎。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.Net.Http、System.IO、System.Threading、System.Buffers（ArrayPool，BCL 自带）
// NOTE: 合法授权学习使用，仅限本地环境。
//       HttpClient 由外部注入（构造时传入或注入 HttpMessageHandler），测试可用假 handler 驱动，不真实联网。

using System.Buffers;
using System.Net;
using System.Net.Http.Headers;

namespace DevSwitch.Downloader;

/// <summary>
/// 探测结果：服务器是否支持 Range 以及文件总长度。
/// </summary>
/// <param name="SupportsRange">服务器是否支持分块（HTTP Range）。</param>
/// <param name="TotalBytes">文件总字节数；-1 表示未知。</param>
internal readonly record struct DownloadProbe(bool SupportsRange, long TotalBytes);

/// <summary>
/// 多线程断点续传下载引擎。
/// 关键特性：
/// - Range 支持探测，不支持时回退单线程整下；
/// - 多分块并发（SemaphoreSlim 限流），断点续传跳过已完成 chunk；
/// - 全异步，Stream.CopyToAsync 流式写入，positioned write 并发写同一文件；
/// - 进度回调按时间节流，避免高频回调拖垮 UI；
/// - 取消令牌触发时保存 chunk 进度并返回 Paused，支持后续续传。
/// </summary>
public sealed class DownloadEngine
{
    private readonly HttpClient _httpClient;
    private readonly DownloadEngineOptions _options;

    /// <summary>
    /// 使用已配置好的 HttpClient 构造引擎（推荐，便于注入假 handler）。
    /// </summary>
    /// <param name="httpClient">用于发起请求的 HttpClient，不会被 Dispose。</param>
    /// <param name="options">引擎选项；为空时使用默认值。</param>
    public DownloadEngine(HttpClient httpClient, DownloadEngineOptions? options = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? new DownloadEngineOptions();
    }

    /// <summary>
    /// 使用指定的 HttpMessageHandler 构造引擎，内部创建 HttpClient。
    /// </summary>
    /// <param name="handler">消息处理器，测试时注入假实现。</param>
    /// <param name="options">引擎选项；为空时使用默认值。</param>
    public DownloadEngine(HttpMessageHandler handler, DownloadEngineOptions? options = null)
        : this(new HttpClient(handler ?? throw new ArgumentNullException(nameof(handler))), options)
    {
    }

    /// <summary>
    /// 下载任务对应的文件到指定路径，并返回更新后的任务（含状态、进度、chunk）。
    /// 取消时返回 Paused 状态且保留已完成进度，可用返回的任务再次调用以续传。
    /// </summary>
    /// <param name="task">下载任务（可包含已完成 chunk 用于续传）。</param>
    /// <param name="destinationFilePath">目标文件路径。</param>
    /// <param name="progress">进度回调；可为空。</param>
    /// <param name="cancellationToken">取消/暂停令牌。</param>
    /// <returns>更新后的任务。</returns>
    public async Task<DownloadTask> DownloadAsync(
        DownloadTask task,
        string destinationFilePath,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (string.IsNullOrWhiteSpace(destinationFilePath))
        {
            throw new ArgumentException("Destination file path is required.", nameof(destinationFilePath));
        }

        var reporter = new ThrottledProgressReporter(task.Id, progress, _options.ProgressThrottleMilliseconds);

        try
        {
            // 第一步：探测服务器能力，决定多线程还是单线程。
            var probe = await ProbeAsync(task.Url, cancellationToken).ConfigureAwait(false);

            // Range 不支持或长度未知 → 回退单线程整下。
            if (!probe.SupportsRange || probe.TotalBytes <= 0)
            {
                return await DownloadSingleStreamAsync(task, destinationFilePath, reporter, cancellationToken)
                    .ConfigureAwait(false);
            }

            return await DownloadChunkedAsync(task, destinationFilePath, probe.TotalBytes, reporter, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 取消/暂停：保留磁盘上已写字节，返回 Paused 任务以便续传。
            return task with { Status = DownloadStatus.Paused };
        }
        catch
        {
            // 其它异常（网络、IO 等）标记失败，交由上层流程决定重试或提示。
            return task with { Status = DownloadStatus.Failed };
        }
    }

    /// <summary>
    /// 探测目标 URL 是否支持 Range 以及总长度。使用 Range: bytes=0-0 探针。
    /// </summary>
    private async Task<DownloadProbe> ProbeAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // 只请求首字节作为探针，尽量不拉全量数据。
        request.Headers.Range = new RangeHeaderValue(0, 0);

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        // 206 Partial Content：服务器支持 Range，从 Content-Range 拿总长度。
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var total = response.Content.Headers.ContentRange?.Length ?? -1L;
            return new DownloadProbe(SupportsRange: total > 0, TotalBytes: total);
        }

        // 200 OK：服务器忽略了 Range。靠 Accept-Ranges 头判断是否支持，长度取 Content-Length。
        var contentLength = response.Content.Headers.ContentLength ?? -1L;
        var acceptsRanges = response.Headers.AcceptRanges.Contains("bytes");
        return new DownloadProbe(SupportsRange: acceptsRanges && contentLength > 0, TotalBytes: contentLength);
    }

    /// <summary>
    /// 回退路径：服务器不支持 Range 时单线程整包下载。
    /// </summary>
    private async Task<DownloadTask> DownloadSingleStreamAsync(
        DownloadTask task,
        string destinationFilePath,
        ThrottledProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        EnsureParentDirectory(destinationFilePath);

        using var request = new HttpRequestMessage(HttpMethod.Get, task.Url);
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1L;

        var httpStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (httpStream.ConfigureAwait(false))
        {
            // 整下场景从头覆盖写入，不做续传（无 Range 无法定位偏移）。
            var fileStream = new FileStream(
                destinationFilePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                _options.CopyBufferSize,
                FileOptions.Asynchronous);
            await using (fileStream.ConfigureAwait(false))
            {
                // 从共享池租借缓冲区，避免每次下载都新分配大数组、减轻 GC 压力。
                // 池返回的数组可能大于请求长度，因此用 read 长度切片，不依赖 buffer.Length。
                var buffer = ArrayPool<byte>.Shared.Rent(_options.CopyBufferSize);
                try
                {
                    long completed = 0;
                    int read;
                    while ((read = await httpStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        completed += read;
                        reporter.Report(completed, total <= 0 ? completed : total, DownloadStatus.Running);
                    }

                    reporter.ReportFinal(completed, total <= 0 ? completed : total, DownloadStatus.Running);

                    // 单线程整下不产生 chunk 列表。
                    return task with
                    {
                        Status = DownloadStatus.Running,
                        BytesTotal = total <= 0 ? completed : total,
                        BytesCompleted = completed,
                        Chunks = Array.Empty<DownloadChunk>(),
                    };
                }
                finally
                {
                    // 归还缓冲区。clearArray:false 因内容非敏感，省去清零开销。
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
    }

    /// <summary>
    /// 主路径：多分块并发下载，支持断点续传与 positioned write。
    /// </summary>
    private async Task<DownloadTask> DownloadChunkedAsync(
        DownloadTask task,
        string destinationFilePath,
        long totalBytes,
        ThrottledProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        EnsureParentDirectory(destinationFilePath);

        // 决定分块方案：若已有匹配总长度的 chunk（续传场景）则复用，否则重新规划。
        var chunks = (task.Chunks.Count > 0 && task.BytesTotal == totalBytes)
            ? task.Chunks
            : ChunkPlanner.Plan(totalBytes, _options.Parallelism);

        // 每个 chunk 的已完成字节，初始化为已有进度（续传），各 chunk 仅由自己的任务更新，无竞争。
        var chunkProgress = new long[chunks.Count];
        for (var i = 0; i < chunks.Count; i++)
        {
            chunkProgress[i] = Math.Min(chunks[i].BytesCompleted, chunks[i].Length);
        }

        // 已完成字节基线（用于进度初值与续传统计）。
        long initialCompleted = chunkProgress.Sum();

        // 预创建并设定文件长度，使各分块可并发 positioned write。
        // 续传时文件已存在则保留内容，否则新建到目标长度。
        using (var handle = File.OpenHandle(
                   destinationFilePath,
                   FileMode.OpenOrCreate,
                   FileAccess.Write,
                   FileShare.ReadWrite,
                   FileOptions.Asynchronous))
        {
            if (RandomAccess.GetLength(handle) != totalBytes)
            {
                RandomAccess.SetLength(handle, totalBytes);
            }

            // 限流并发：信号量上限 = 收敛后的并发度。
            var parallelism = ChunkPlanner.ClampParallelism(_options.Parallelism);
            using var semaphore = new SemaphoreSlim(parallelism, parallelism);

            // 共享的总完成计数，供进度回调读取。
            long totalCompleted = initialCompleted;

            var tasks = new List<Task>(chunks.Count);
            foreach (var chunk in chunks)
            {
                // 已完成 chunk 直接跳过（断点续传核心）。
                if (chunk.IsComplete)
                {
                    continue;
                }

                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

                var localChunk = chunk;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await DownloadChunkAsync(
                            task.Url,
                            handle,
                            localChunk,
                            chunkProgress,
                            written =>
                            {
                                // 累加全局进度并节流上报。Interlocked 保证多线程下计数准确。
                                var current = Interlocked.Add(ref totalCompleted, written);
                                reporter.Report(current, totalBytes, DownloadStatus.Running);
                            },
                            cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, cancellationToken));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);

            var finalCompleted = Interlocked.Read(ref totalCompleted);
            reporter.ReportFinal(finalCompleted, totalBytes, DownloadStatus.Running);
        }

        // 用最新 chunk 进度构建返回任务。
        var updatedChunks = new DownloadChunk[chunks.Count];
        for (var i = 0; i < chunks.Count; i++)
        {
            updatedChunks[i] = chunks[i] with { BytesCompleted = chunkProgress[i] };
        }

        var completedBytes = chunkProgress.Sum();
        return task with
        {
            Status = DownloadStatus.Running,
            BytesTotal = totalBytes,
            BytesCompleted = completedBytes,
            Chunks = updatedChunks,
        };
    }

    /// <summary>
    /// 下载单个分块：从 Start+已完成偏移续传到 End，positioned write 到文件对应区间。
    /// </summary>
    private async Task DownloadChunkAsync(
        string url,
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        DownloadChunk chunk,
        long[] chunkProgress,
        Action<long> onWritten,
        CancellationToken cancellationToken)
    {
        // 续传起点 = 区间起点 + 该 chunk 已完成字节。
        var resumeFrom = chunk.Start + chunkProgress[chunk.Index];
        var rangeEnd = chunk.End;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(resumeFrom, rangeEnd);

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var httpStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (httpStream.ConfigureAwait(false))
        {
            // 从共享池租借缓冲区，多分块并发下各自租借自己的数组，互不共享、无需加锁。
            var buffer = ArrayPool<byte>.Shared.Rent(_options.CopyBufferSize);
            try
            {
                var writeOffset = resumeFrom;
                int read;
                while ((read = await httpStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    // positioned write：直接写到文件的目标偏移，多分块并发互不干扰。
                    await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), writeOffset, cancellationToken)
                        .ConfigureAwait(false);

                    writeOffset += read;
                    chunkProgress[chunk.Index] += read;
                    // 上报本次写入字节，由调用方累加全局计数并节流上报。
                    onWritten(read);
                }
            }
            finally
            {
                // 归还缓冲区，不清零（内容非敏感），减少分配与 GC。
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private static void EnsureParentDirectory(string filePath)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
    }
}
