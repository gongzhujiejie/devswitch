// 文件用途：定义「更新检查」能力的数据模型——GitHub Releases 反序列化结构与对外的检查结果。
// 创建/修改日期：2026-06-10
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：仅 BCL（无第三方包）
// NOTE: 合法授权学习使用，仅限本地环境。本文件只承载纯数据，不触发任何网络请求。

namespace DevSwitch.Core;

/// <summary>
/// 单个 GitHub Release 的精简模型；只保留更新检查所需字段。
/// </summary>
/// <param name="TagName">发布标签，例如 "v0.2.0"。</param>
/// <param name="Name">发布标题，可能为空。</param>
/// <param name="Prerelease">是否为预发布版本。</param>
/// <param name="Draft">是否为草稿（未公开）发布。</param>
/// <param name="HtmlUrl">发布页面 URL，供用户跳转下载。</param>
/// <param name="Assets">该发布关联的下载资产列表。</param>
public sealed record GitHubRelease(
    string TagName,
    string? Name,
    bool Prerelease,
    bool Draft,
    string? HtmlUrl,
    IReadOnlyList<GitHubReleaseAsset> Assets);

/// <summary>
/// GitHub Release 资产（可下载文件）。
/// </summary>
/// <param name="Name">资产文件名。</param>
/// <param name="BrowserDownloadUrl">浏览器可直接下载的地址。</param>
public sealed record GitHubReleaseAsset(string Name, string BrowserDownloadUrl);

/// <summary>
/// 更新检查结果。供 UI 与上层逻辑判断是否提示用户更新。
/// </summary>
/// <param name="HasUpdate">是否存在比当前版本更新的可用版本。</param>
/// <param name="LatestVersion">解析到的最新版本标签；检查失败时为空。</param>
/// <param name="CurrentVersion">本次检查所基于的当前版本。</param>
/// <param name="ReleaseUrl">最新版本的发布页 URL；用于引导用户下载。</param>
/// <param name="UsedFallback">最新版本信息是否来自备用更新源。</param>
/// <param name="Failed">检查是否整体失败（主源与备用源都无法得到结果）。</param>
/// <param name="FailureReason">失败原因描述；成功时为空。</param>
public sealed record UpdateCheckResult(
    bool HasUpdate,
    string? LatestVersion,
    string CurrentVersion,
    string? ReleaseUrl,
    bool UsedFallback,
    bool Failed,
    string? FailureReason)
{
    /// <summary>
    /// 构造一个「检查失败」结果的便捷工厂。
    /// </summary>
    /// <param name="currentVersion">当前版本，用于结果回填。</param>
    /// <param name="reason">失败原因。</param>
    /// <returns>Failed=true 的结果对象。</returns>
    public static UpdateCheckResult Failure(string currentVersion, string reason) =>
        new(
            HasUpdate: false,
            LatestVersion: null,
            CurrentVersion: currentVersion,
            ReleaseUrl: null,
            UsedFallback: false,
            Failed: true,
            FailureReason: reason);
}
