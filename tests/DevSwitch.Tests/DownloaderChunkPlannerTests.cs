// 文件用途：验证 ChunkPlanner 分块规划器在各边界场景下的切分正确性。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit
// NOTE: 合法授权学习使用，仅限本地环境。纯算法测试，不涉及 IO。

using DevSwitch.Downloader;
using Xunit;

namespace DevSwitch.Tests;

public sealed class DownloaderChunkPlannerTests
{
    [Fact]
    public void PlanReturnsEmptyForZeroBytes()
    {
        // 边界：0 字节文件不应产生任何分块。
        var chunks = ChunkPlanner.Plan(0, 4);
        Assert.Empty(chunks);
    }

    [Fact]
    public void PlanSplitsEvenlyDivisibleBytes()
    {
        // 100 字节 / 并发 4 = 每块 25 字节，连续覆盖 [0,99]。
        var chunks = ChunkPlanner.Plan(100, 4);

        Assert.Equal(4, chunks.Count);
        Assert.All(chunks, c => Assert.Equal(25, c.Length));
        AssertContiguous(chunks, 100);
    }

    [Fact]
    public void PlanDistributesRemainderToLeadingChunks()
    {
        // 不能整除：10 字节 / 并发 3 => 4,3,3，余数分到前面的块。
        var chunks = ChunkPlanner.Plan(10, 3);

        Assert.Equal(3, chunks.Count);
        Assert.Equal(4, chunks[0].Length);
        Assert.Equal(3, chunks[1].Length);
        Assert.Equal(3, chunks[2].Length);
        AssertContiguous(chunks, 10);
    }

    [Fact]
    public void PlanLimitsChunkCountWhenParallelismExceedsBytes()
    {
        // 并发数大于字节数：3 字节、并发 8 => 只切 3 块，每块 1 字节。
        var chunks = ChunkPlanner.Plan(3, 8);

        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, c => Assert.Equal(1, c.Length));
        AssertContiguous(chunks, 3);
    }

    [Fact]
    public void PlanHandlesSingleByte()
    {
        // 边界：单字节文件只有一块 [0,0]。
        var chunks = ChunkPlanner.Plan(1, 4);

        var only = Assert.Single(chunks);
        Assert.Equal(0, only.Start);
        Assert.Equal(0, only.End);
        Assert.Equal(1, only.Length);
    }

    [Theory]
    [InlineData(0, ChunkPlanner.MinParallelism)]
    [InlineData(-5, ChunkPlanner.MinParallelism)]
    [InlineData(100, ChunkPlanner.MaxParallelism)]
    [InlineData(4, 4)]
    public void ClampParallelismKeepsWithinAllowedRange(int input, int expected)
    {
        // 并发度越界应被收敛到 [1,8]，而不是抛异常。
        Assert.Equal(expected, ChunkPlanner.ClampParallelism(input));
    }

    [Fact]
    public void PlanThrowsForNegativeTotalBytes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChunkPlanner.Plan(-1, 4));
    }

    /// <summary>
    /// 断言分块连续无缝覆盖 [0, total-1] 且互不重叠。
    /// </summary>
    private static void AssertContiguous(IReadOnlyList<DownloadChunk> chunks, long total)
    {
        long expectedStart = 0;
        for (var i = 0; i < chunks.Count; i++)
        {
            Assert.Equal(i, chunks[i].Index);
            Assert.Equal(expectedStart, chunks[i].Start);
            Assert.True(chunks[i].End >= chunks[i].Start);
            expectedStart = chunks[i].End + 1;
        }

        Assert.Equal(total, expectedStart);
    }
}
