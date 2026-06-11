// 文件用途：进度回调节流器，按最小时间间隔合并高频进度上报，避免拖垮 UI 线程。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.Diagnostics、System.Threading
// NOTE: 合法授权学习使用，仅限本地环境。线程安全：多分块并发上报时用锁保护时间戳判断。

using System.Diagnostics;

namespace DevSwitch.Downloader;

/// <summary>
/// 节流进度上报器。多线程下载时各分块高频调用 Report，
/// 这里按 ProgressThrottleMilliseconds 合并回调，最终再强制上报一次最新值，
/// 既保证 UI 流畅又不丢失最终进度。
/// </summary>
internal sealed class ThrottledProgressReporter
{
    private readonly string _taskId;
    private readonly IProgress<DownloadProgress>? _progress;
    private readonly long _throttleTicks;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly object _gate = new();

    private long _lastReportTicks;

    /// <summary>
    /// 构造节流器。
    /// </summary>
    /// <param name="taskId">任务 ID，写入每次进度快照。</param>
    /// <param name="progress">底层进度回调；为空时所有上报都是空操作。</param>
    /// <param name="throttleMilliseconds">最小回调间隔毫秒；&lt;=0 表示不节流。</param>
    public ThrottledProgressReporter(string taskId, IProgress<DownloadProgress>? progress, int throttleMilliseconds)
    {
        _taskId = taskId;
        _progress = progress;
        // 把毫秒间隔换算为 Stopwatch tick，避免每次回调都做除法。
        _throttleTicks = throttleMilliseconds <= 0
            ? 0
            : (long)(throttleMilliseconds * (Stopwatch.Frequency / 1000.0));
    }

    /// <summary>
    /// 节流上报。距上次上报不足间隔时跳过，减少 UI 刷新压力。
    /// </summary>
    public void Report(long bytesCompleted, long bytesTotal, DownloadStatus status)
    {
        if (_progress is null)
        {
            return;
        }

        // 节流关闭时直接上报。
        if (_throttleTicks <= 0)
        {
            _progress.Report(new DownloadProgress(_taskId, bytesCompleted, bytesTotal, status));
            return;
        }

        // 仅在超过最小间隔时上报；用锁保证多线程读改时间戳一致。
        bool shouldReport;
        lock (_gate)
        {
            var now = _stopwatch.ElapsedTicks;
            shouldReport = now - _lastReportTicks >= _throttleTicks;
            if (shouldReport)
            {
                _lastReportTicks = now;
            }
        }

        if (shouldReport)
        {
            _progress.Report(new DownloadProgress(_taskId, bytesCompleted, bytesTotal, status));
        }
    }

    /// <summary>
    /// 强制上报最终进度，绕过节流，保证 UI 收到 100% 的那一帧。
    /// </summary>
    public void ReportFinal(long bytesCompleted, long bytesTotal, DownloadStatus status)
    {
        _progress?.Report(new DownloadProgress(_taskId, bytesCompleted, bytesTotal, status));
    }
}
