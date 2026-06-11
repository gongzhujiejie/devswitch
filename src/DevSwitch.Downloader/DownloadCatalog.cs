// 文件用途：定义 DevSwitch downloads.json 的下载任务持久化模型。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.Collections.Generic、DevSwitch.Core
// NOTE: 合法授权学习使用，仅限本地环境。本模型只描述下载任务状态，不直接执行 IO。

using DevSwitch.Core;

namespace DevSwitch.Downloader;

/// <summary>
/// 下载任务状态机。状态取值与设计文档第 10 节一致。
/// </summary>
public enum DownloadStatus
{
    /// <summary>
    /// 已入队，尚未开始传输。
    /// </summary>
    Queued,

    /// <summary>
    /// 正在传输字节。
    /// </summary>
    Running,

    /// <summary>
    /// 用户暂停，已完成的 chunk 保留以便续传。
    /// </summary>
    Paused,

    /// <summary>
    /// 传输完成，正在计算 SHA256 或执行校验。
    /// </summary>
    Verifying,

    /// <summary>
    /// 校验通过，正在解压到目标目录。
    /// </summary>
    Extracting,

    /// <summary>
    /// 全流程完成（已解压、可登记）。
    /// </summary>
    Completed,

    /// <summary>
    /// 传输、校验或解压失败。
    /// </summary>
    Failed,

    /// <summary>
    /// 已被取消。
    /// </summary>
    Cancelled,
}

/// <summary>
/// 单个分块的字节区间与进度。区间使用闭区间 [Start, End]（含端点）。
/// </summary>
/// <param name="Index">分块序号，从 0 开始，用于稳定排序和续传定位。</param>
/// <param name="Start">分块起始字节偏移（含）。</param>
/// <param name="End">分块结束字节偏移（含）。</param>
/// <param name="BytesCompleted">该分块已写入的字节数，用于断点续传。</param>
public sealed record DownloadChunk(long Index, long Start, long End, long BytesCompleted)
{
    /// <summary>
    /// 分块总字节数（闭区间长度）。
    /// </summary>
    public long Length => End - Start + 1;

    /// <summary>
    /// 该分块是否已完整下载完成。
    /// </summary>
    public bool IsComplete => BytesCompleted >= Length;
}

/// <summary>
/// 单个下载任务记录，对应 downloads.json tasks[] 中的一项。
/// </summary>
/// <param name="Id">稳定任务 ID。</param>
/// <param name="SdkType">目标 SDK 类型。</param>
/// <param name="Version">目标版本号。</param>
/// <param name="Distribution">发行版标识，例如 temurin、nodejs。</param>
/// <param name="Arch">目标架构。</param>
/// <param name="Url">下载地址。</param>
/// <param name="ExpectedSha256">期望的 SHA256（十六进制小写）；为空表示缺失校验信息。</param>
/// <param name="Status">当前任务状态。</param>
/// <param name="BytesTotal">文件总字节数；0 表示尚未探测。</param>
/// <param name="BytesCompleted">已完成字节数（所有分块累加）。</param>
/// <param name="Chunks">分块列表；回退单线程下载时可为空。</param>
public sealed record DownloadTask(
    string Id,
    SdkType SdkType,
    string Version,
    string Distribution,
    SdkArchitecture Arch,
    string Url,
    string? ExpectedSha256,
    DownloadStatus Status,
    long BytesTotal,
    long BytesCompleted,
    IReadOnlyList<DownloadChunk> Chunks)
{
    /// <summary>
    /// 创建一个初始 queued 状态的任务。
    /// </summary>
    public static DownloadTask CreateQueued(
        string id,
        SdkType sdkType,
        string version,
        string distribution,
        SdkArchitecture arch,
        string url,
        string? expectedSha256)
    {
        return new DownloadTask(
            Id: id,
            SdkType: sdkType,
            Version: version,
            Distribution: distribution,
            Arch: arch,
            Url: url,
            ExpectedSha256: expectedSha256,
            Status: DownloadStatus.Queued,
            BytesTotal: 0,
            BytesCompleted: 0,
            Chunks: Array.Empty<DownloadChunk>());
    }
}

/// <summary>
/// downloads.json 根模型。
/// </summary>
/// <param name="SchemaVersion">下载目录 schema 版本。</param>
/// <param name="Tasks">所有下载任务记录。</param>
public sealed record DownloadCatalog(int SchemaVersion, IReadOnlyList<DownloadTask> Tasks)
{
    /// <summary>
    /// 创建空下载目录。
    /// </summary>
    /// <returns>schemaVersion=1 且没有任何任务的目录。</returns>
    public static DownloadCatalog CreateEmpty()
    {
        return new DownloadCatalog(SchemaVersion: 1, Tasks: Array.Empty<DownloadTask>());
    }
}
