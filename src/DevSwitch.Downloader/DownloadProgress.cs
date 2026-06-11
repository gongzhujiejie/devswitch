// 文件用途：定义下载进度快照与下载引擎的可配置选项。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：无（纯模型）
// NOTE: 合法授权学习使用，仅限本地环境。进度快照为不可变值类型，便于跨线程传递给 UI。

namespace DevSwitch.Downloader;

/// <summary>
/// 下载进度快照。通过 IProgress&lt;DownloadProgress&gt; 高频上报，UI 据此刷新进度条。
/// </summary>
/// <param name="TaskId">关联的下载任务 ID。</param>
/// <param name="BytesCompleted">已完成字节数。</param>
/// <param name="BytesTotal">总字节数；-1 表示服务器未提供长度（无法计算百分比）。</param>
/// <param name="Status">当前任务状态。</param>
public readonly record struct DownloadProgress(
    string TaskId,
    long BytesCompleted,
    long BytesTotal,
    DownloadStatus Status)
{
    /// <summary>
    /// 完成百分比 [0, 1]；总长度未知时返回 null。
    /// </summary>
    public double? Fraction =>
        BytesTotal > 0 ? Math.Clamp((double)BytesCompleted / BytesTotal, 0d, 1d) : null;
}

/// <summary>
/// 下载引擎选项。
/// </summary>
public sealed class DownloadEngineOptions
{
    /// <summary>
    /// 并发分块数，会被收敛到 [1, 8]。
    /// </summary>
    public int Parallelism { get; init; } = ChunkPlanner.DefaultParallelism;

    /// <summary>
    /// 进度回调最小时间间隔（毫秒）。两次回调间隔小于该值时合并，避免高频回调拖垮 UI。
    /// </summary>
    public int ProgressThrottleMilliseconds { get; init; } = 100;

    /// <summary>
    /// 流拷贝缓冲区大小（字节）。默认 81920 与 BCL CopyToAsync 默认一致。
    /// </summary>
    public int CopyBufferSize { get; init; } = 81920;
}
