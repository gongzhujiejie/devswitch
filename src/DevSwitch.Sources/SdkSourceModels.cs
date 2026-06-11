// 文件用途：定义版本源适配层的统一模型与抽象接口。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.Threading、System.Collections.Generic、DevSwitch.Core
// NOTE: 合法授权学习使用，仅限本地环境。

using DevSwitch.Core;

namespace DevSwitch.Sources;

/// <summary>
/// 版本源返回的单个可下载 SDK 版本元数据。
/// </summary>
/// <param name="SdkType">所属 SDK 类型（Java/Maven/Node/Go）。</param>
/// <param name="Version">规范化版本号，例如 22.11.0、1.22.5、21.0.4+7。</param>
/// <param name="Distribution">发行版标识，例如 temurin、zulu、corretto、nodejs、go、apache-maven。</param>
/// <param name="Architecture">目标 CPU 架构。</param>
/// <param name="DownloadUrl">安装包下载地址。</param>
/// <param name="Sha256">可选：安装包 SHA256（小写十六进制）。</param>
/// <param name="ChecksumUrl">可选：单独的校验文件地址，当未内联 SHA256 时使用。</param>
/// <param name="ReleaseDate">可选：发布时间。</param>
/// <param name="DisplayName">UI 友好显示名，例如 "Temurin 21.0.4+7 (x64)"。</param>
public sealed record SdkSourceVersion(
    SdkType SdkType,
    string Version,
    string Distribution,
    SdkArchitecture Architecture,
    string DownloadUrl,
    string? Sha256 = null,
    string? ChecksumUrl = null,
    DateTimeOffset? ReleaseDate = null,
    string? DisplayName = null);

/// <summary>
/// 单一版本源：针对某种发行版/来源提供版本列表查询。
/// </summary>
public interface ISdkVersionSource
{
    /// <summary>
    /// 该源负责的 SDK 类型。
    /// </summary>
    SdkType SdkType { get; }

    /// <summary>
    /// 该源的发行版标识，例如 temurin、nodejs、go、apache-maven。
    /// </summary>
    string Distribution { get; }

    /// <summary>
    /// 拉取并解析版本列表，按版本降序返回。
    /// </summary>
    /// <param name="architecture">期望架构；传 <see cref="SdkArchitecture.Any"/> 表示不过滤架构。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>按版本降序排列的可下载版本列表；失败的条目会被跳过而不抛出。</returns>
    Task<IReadOnlyList<SdkSourceVersion>> ListVersionsAsync(
        SdkArchitecture architecture,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 多源聚合目录：按 SDK 类型聚合所有已注册版本源。
/// </summary>
public interface ISdkSourceCatalog
{
    /// <summary>
    /// 列出指定 SDK 类型、指定架构下所有源的版本，合并去重并按版本降序返回。
    /// </summary>
    /// <param name="sdkType">SDK 类型。</param>
    /// <param name="architecture">期望架构；<see cref="SdkArchitecture.Any"/> 表示不过滤。</param>
    /// <param name="cancellationToken">取消标记。</param>
    Task<IReadOnlyList<SdkSourceVersion>> ListVersionsAsync(
        SdkType sdkType,
        SdkArchitecture architecture,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 文本资源抓取委托：输入 URL，返回响应正文文本。
/// </summary>
/// <remarks>
/// 抽象出抓取动作，便于在测试中注入假数据而不真实联网；
/// 生产实现可基于 <see cref="System.Net.Http.HttpClient"/> 或可注入的 HttpMessageHandler。
/// </remarks>
/// <param name="url">资源地址。</param>
/// <param name="cancellationToken">取消标记。</param>
/// <returns>响应正文文本。</returns>
public delegate Task<string> SourceTextFetcher(string url, CancellationToken cancellationToken);
