// 文件用途：纯算法分块规划器，把总字节数按并发度切分为闭区间 chunk。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.Collections.Generic
// NOTE: 合法授权学习使用，仅限本地环境。本类不做任何 IO，便于纯单元测试覆盖所有边界。

namespace DevSwitch.Downloader;

/// <summary>
/// 分块下载规划器：输入总字节数与并发度，输出 chunk 区间集合。
/// 区间使用闭区间 [Start, End]，与 HTTP Range 头 "bytes=start-end" 语义一致。
/// </summary>
public static class ChunkPlanner
{
    /// <summary>
    /// 默认并发度，对应设计文档第 10 节 parallelism=4。
    /// </summary>
    public const int DefaultParallelism = 4;

    /// <summary>
    /// 最小并发度。
    /// </summary>
    public const int MinParallelism = 1;

    /// <summary>
    /// 最大并发度，对应设计文档「可调并发 1-8」。
    /// </summary>
    public const int MaxParallelism = 8;

    /// <summary>
    /// 把并发度收敛到合法范围 [1, 8]。
    /// </summary>
    /// <param name="parallelism">请求的并发度。</param>
    /// <returns>收敛后的并发度。</returns>
    public static int ClampParallelism(int parallelism)
    {
        // NOTE: 不抛异常而是收敛，避免上层 settings.json 写入越界值时直接中断下载。
        if (parallelism < MinParallelism)
        {
            return MinParallelism;
        }

        return parallelism > MaxParallelism ? MaxParallelism : parallelism;
    }

    /// <summary>
    /// 把总字节数按并发度切分为 chunk 区间。
    /// </summary>
    /// <param name="totalBytes">文件总字节数，必须 &gt;= 0。</param>
    /// <param name="parallelism">期望并发度，会被收敛到 [1, 8]。</param>
    /// <returns>
    /// chunk 列表，按 Index 升序。区间互不重叠且连续覆盖 [0, totalBytes-1]。
    /// 当 totalBytes 为 0 时返回空列表。
    /// </returns>
    public static IReadOnlyList<DownloadChunk> Plan(long totalBytes, int parallelism = DefaultParallelism)
    {
        if (totalBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalBytes), totalBytes, "Total bytes must be non-negative.");
        }

        // 边界：0 字节文件没有任何区间，交由上层按空文件处理。
        if (totalBytes == 0)
        {
            return Array.Empty<DownloadChunk>();
        }

        var effectiveParallelism = ClampParallelism(parallelism);

        // 边界：并发数大于字节数时，多余的线程没有字节可分，因此实际分块数不超过字节数。
        // 例如 3 字节、并发 4 => 只切 3 块，每块 1 字节。
        var chunkCount = (int)Math.Min(effectiveParallelism, totalBytes);

        // 基础块大小与余数。不能整除时，把余数分摊到前面的若干块（每块 +1 字节），
        // 保证区间连续且大小尽量均衡，而不是把余数全堆到最后一块。
        var baseSize = totalBytes / chunkCount;
        var remainder = totalBytes % chunkCount;

        var chunks = new List<DownloadChunk>(chunkCount);
        long start = 0;
        for (var index = 0; index < chunkCount; index++)
        {
            // 前 remainder 块多分 1 字节，实现均衡切分。
            var size = baseSize + (index < remainder ? 1 : 0);
            var end = start + size - 1;

            chunks.Add(new DownloadChunk(Index: index, Start: start, End: end, BytesCompleted: 0));
            start = end + 1;
        }

        return chunks;
    }
}
