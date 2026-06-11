// 文件用途：定义「自更新」编排层使用的数据模型——下载计划、阶段枚举与最终结果。
// 创建/修改日期：2026-06-10
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：仅 BCL（无第三方包）
// NOTE: 合法授权学习使用，仅限本地环境。本文件只承载纯数据，不触发任何网络/磁盘副作用。

namespace DevSwitch.Core;

/// <summary>
/// 自更新整体计划：从一个 release 解析出的可执行下载信息。
/// 由编排层（主代理）结合 <see cref="UpdateAssetSelector"/> 与 release 元数据组装。
/// </summary>
/// <param name="Version">release 的版本标签（如 "v0.2.0"），用于显示与比较。</param>
/// <param name="DownloadUrl">选中安装包的下载地址（Windows x64 zip）。</param>
/// <param name="AssetFileName">选中安装包的文件名，供落盘命名与日志使用。</param>
/// <param name="ChecksumUrl">sha256 校验资产的下载地址；无校验资产时为空。</param>
/// <param name="ReleaseUrl">release 页面地址；自动更新失败时回退引导用户手动下载。</param>
public sealed record SelfUpdatePlan(
    string Version,
    string DownloadUrl,
    string AssetFileName,
    string? ChecksumUrl,
    string? ReleaseUrl);

/// <summary>
/// 自更新各阶段。供 UI 显示进度文案，按正常流程从上到下推进。
/// </summary>
public enum SelfUpdateStage
{
    /// <summary>空闲（尚未开始）。</summary>
    Idle,

    /// <summary>正在下载安装包。</summary>
    Downloading,

    /// <summary>正在校验 sha256。</summary>
    Verifying,

    /// <summary>正在解压安装包。</summary>
    Extracting,

    /// <summary>正在启动外部 updater 进程。</summary>
    LaunchingUpdater,

    /// <summary>流程失败（任一阶段出错）。</summary>
    Failed,

    /// <summary>流程完成（updater 已成功拉起，主程序准备退出）。</summary>
    Completed,
}

/// <summary>
/// 自更新结果。聚合最终状态、所处阶段、错误码与可读消息。
/// </summary>
/// <param name="Success">整体是否成功。</param>
/// <param name="Stage">结束时所处阶段；成功时通常为 <see cref="SelfUpdateStage.LaunchingUpdater"/> 或 <see cref="SelfUpdateStage.Completed"/>，失败时为出错阶段。</param>
/// <param name="ErrorCode">机器可读的错误码（如 "download_failed"/"checksum_mismatch"）；成功时为空。</param>
/// <param name="Message">面向用户/日志的可读消息。</param>
public sealed record SelfUpdateResult(
    bool Success,
    SelfUpdateStage Stage,
    string? ErrorCode,
    string Message);
