// 文件用途：基于注入的文本抓取委托实现各版本源与多源聚合目录。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.Threading、System.Linq、DevSwitch.Core
// NOTE: 合法授权学习使用，仅限本地环境。

using DevSwitch.Core;

namespace DevSwitch.Sources;

/// <summary>
/// 解析委托：把抓取到的文本解析为版本列表。
/// </summary>
/// <param name="payload">抓取到的响应文本。</param>
/// <param name="architecture">期望架构。</param>
/// <returns>解析出的版本列表。</returns>
internal delegate IReadOnlyList<SdkSourceVersion> SourcePayloadParser(string payload, SdkArchitecture architecture);

/// <summary>
/// 通用版本源：抓取 -> 解析 -> 按版本降序排序。
/// </summary>
/// <remarks>
/// 通过注入 <see cref="SourceTextFetcher"/> 与解析器解耦网络与解析逻辑，
/// 测试可注入返回内嵌样例的假抓取器，无需联网。
/// </remarks>
public sealed class HttpSdkVersionSource : ISdkVersionSource
{
    private readonly string _endpoint;
    private readonly SourceTextFetcher _fetcher;
    private readonly SourcePayloadParser _parser;

    private HttpSdkVersionSource(
        SdkType sdkType,
        string distribution,
        string endpoint,
        SourceTextFetcher fetcher,
        SourcePayloadParser parser)
    {
        SdkType = sdkType;
        Distribution = distribution;
        _endpoint = endpoint;
        _fetcher = fetcher;
        _parser = parser;
    }

    /// <inheritdoc />
    public SdkType SdkType { get; }

    /// <inheritdoc />
    public string Distribution { get; }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SdkSourceVersion>> ListVersionsAsync(
        SdkArchitecture architecture,
        CancellationToken cancellationToken = default)
    {
        // 抓取原始文本（网络/IO 阶段，可被取消）。
        var payload = await _fetcher(_endpoint, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        // 纯解析阶段：失败条目已在解析器内部跳过。
        var versions = _parser(payload, architecture);

        // 统一按版本降序返回（最新在前）。
        return SortDescending(versions);
    }

    /// <summary>
    /// 创建 Node.js 官方源。
    /// </summary>
    public static HttpSdkVersionSource CreateNode(
        SourceTextFetcher fetcher,
        string endpoint = "https://nodejs.org/dist/index.json")
    {
        ArgumentNullException.ThrowIfNull(fetcher);
        return new HttpSdkVersionSource(
            SdkType.Node,
            "nodejs",
            endpoint,
            fetcher,
            NodeIndexParser.Parse);
    }

    /// <summary>
    /// 创建 Go 官方源。
    /// </summary>
    public static HttpSdkVersionSource CreateGo(
        SourceTextFetcher fetcher,
        string endpoint = "https://go.dev/dl/?mode=json&include=all")
    {
        ArgumentNullException.ThrowIfNull(fetcher);
        return new HttpSdkVersionSource(
            SdkType.Go,
            "go",
            endpoint,
            fetcher,
            GoDownloadParser.Parse);
    }

    /// <summary>
    /// 创建 Apache Maven 源（maven-metadata.xml）。
    /// </summary>
    public static HttpSdkVersionSource CreateMaven(
        SourceTextFetcher fetcher,
        string endpoint = "https://repo.maven.apache.org/maven2/org/apache/maven/apache-maven/maven-metadata.xml")
    {
        ArgumentNullException.ThrowIfNull(fetcher);
        return new HttpSdkVersionSource(
            SdkType.Maven,
            "apache-maven",
            endpoint,
            fetcher,
            MavenMetadataParser.Parse);
    }

    /// <summary>
    /// 创建 Adoptium/Temurin 源（指定 feature 大版本，如 8/11/17/21/25）。
    /// </summary>
    /// <param name="fetcher">文本抓取委托。</param>
    /// <param name="featureVersion">Adoptium feature 版本号（JDK 主版本），如 21。</param>
    /// <param name="pageSize">每个大版本最多取多少个 GA 小版本（按时间倒序，最新在前），默认 5。</param>
    /// <param name="distribution">发行版标识，默认 temurin。</param>
    /// <remarks>
    /// 端点为官方 api.adoptium.net 的 assets feature_releases，按 release_date 倒序仅取 GA 版本，
    /// 限定 os=windows、image_type=jdk。pageSize 控制每个大版本的小版本数量，避免列表过长。
    /// </remarks>
    public static HttpSdkVersionSource CreateTemurinFeature(
        SourceTextFetcher fetcher,
        int featureVersion,
        int pageSize = 5,
        string distribution = TemurinAssetsParser.DefaultDistribution)
    {
        ArgumentNullException.ThrowIfNull(fetcher);
        if (featureVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(featureVersion), "Feature version must be positive.");
        }

        int clampedPageSize = pageSize < 1 ? 1 : (pageSize > 50 ? 50 : pageSize);
        // sort_method=DATE & sort_order=DESC：确保每个大版本取到的是最新的若干 GA 小版本。
        string endpoint =
            $"https://api.adoptium.net/v3/assets/feature_releases/{featureVersion}/ga" +
            $"?os=windows&image_type=jdk&page=0&page_size={clampedPageSize}&sort_method=DATE&sort_order=DESC";

        return new HttpSdkVersionSource(
            SdkType.Java,
            distribution,
            endpoint,
            fetcher,
            (payload, arch) => TemurinAssetsParser.Parse(payload, arch, distribution));
    }

    /// <summary>
    /// 创建 Adoptium/Temurin 源。
    /// </summary>
    /// <param name="fetcher">文本抓取委托。</param>
    /// <param name="endpoint">assets 端点。</param>
    /// <param name="distribution">发行版标识，默认 temurin。</param>
    public static HttpSdkVersionSource CreateTemurin(
        SourceTextFetcher fetcher,
        string endpoint = "https://api.adoptium.net/v3/assets/feature_releases/21/ga?os=windows&image_type=jdk",
        string distribution = TemurinAssetsParser.DefaultDistribution)
    {
        ArgumentNullException.ThrowIfNull(fetcher);
        return new HttpSdkVersionSource(
            SdkType.Java,
            distribution,
            endpoint,
            fetcher,
            (payload, arch) => TemurinAssetsParser.Parse(payload, arch, distribution));
    }

    /// <summary>
    /// 按版本降序排序版本列表。
    /// </summary>
    internal static IReadOnlyList<SdkSourceVersion> SortDescending(IReadOnlyList<SdkSourceVersion> versions)
    {
        // 降序：取语义化升序比较结果取反；版本相同再按架构稳定排序。
        var sorted = versions
            .OrderByDescending(v => v.Version, SemanticVersionComparer.Instance)
            .ThenBy(v => v.Architecture)
            .ToList();
        return sorted;
    }
}

/// <summary>
/// 多源聚合目录：把多个 <see cref="ISdkVersionSource"/> 按 SDK 类型聚合。
/// </summary>
public sealed class SdkSourceCatalog : ISdkSourceCatalog
{
    private readonly IReadOnlyList<ISdkVersionSource> _sources;

    /// <summary>
    /// 用一组版本源构造聚合目录。
    /// </summary>
    /// <param name="sources">已注册版本源集合。</param>
    public SdkSourceCatalog(IEnumerable<ISdkVersionSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = sources.ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SdkSourceVersion>> ListVersionsAsync(
        SdkType sdkType,
        SdkArchitecture architecture,
        CancellationToken cancellationToken = default)
    {
        // 只查询匹配该 SDK 类型的源。
        var matched = _sources.Where(s => s.SdkType == sdkType).ToList();
        if (matched.Count == 0)
        {
            return Array.Empty<SdkSourceVersion>();
        }

        // 并发查询各源，提升多源聚合吞吐。
        var tasks = matched.Select(s => s.ListVersionsAsync(architecture, cancellationToken)).ToArray();
        var lists = await Task.WhenAll(tasks).ConfigureAwait(false);

        // 合并去重：以 (发行版, 版本, 架构, 下载地址) 作为唯一键。
        var merged = new List<SdkSourceVersion>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in lists)
        {
            foreach (var version in list)
            {
                var key = $"{version.Distribution}|{version.Version}|{version.Architecture}|{version.DownloadUrl}";
                if (seen.Add(key))
                {
                    merged.Add(version);
                }
            }
        }

        // 聚合后统一降序。
        return HttpSdkVersionSource.SortDescending(merged);
    }
}
