// 文件用途：实现手动「检查更新」服务——按主源/备用源顺序拉取 releases，解析最新版本并与当前版本比较。
// 创建/修改日期：2026-06-10
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：仅 BCL（无第三方包）
// NOTE: 合法授权学习使用，仅限本地环境。
//       本服务绝不在构造或启动时联网；只有显式调用 CheckAsync 才会触发注入的获取委托。

namespace DevSwitch.Core;

/// <summary>
/// releases 数据获取抽象。把网络获取从更新检查逻辑中剥离，便于以假数据单测、保证不真实联网。
/// </summary>
public interface IReleaseFeedClient
{
    /// <summary>
    /// 获取指定 URL 的 releases JSON。
    /// </summary>
    /// <param name="url">已解析出的 releases 数据源 URL。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>releases API 的原始响应文本。</returns>
    Task<string> FetchAsync(string url, CancellationToken cancellationToken);
}

/// <summary>
/// 手动检查更新服务。
/// 构造时不进行任何网络访问；调用 <see cref="CheckAsync"/> 时才按主源 -> 备用源顺序获取数据。
/// </summary>
public sealed class UpdateCheckService
{
    /// <summary>
    /// 主源别名 "github-releases" 对应的默认 GitHub Releases API URL。
    /// </summary>
    public const string GitHubReleasesAlias = "github-releases";

    /// <summary>
    /// github-releases 别名解析出的默认 API 地址。仅用于构造请求 URL，不在此处发起请求。
    /// </summary>
    public const string DefaultGitHubReleasesUrl =
        "https://api.github.com/repos/devswitch/devswitch/releases";

    // 实际执行获取的委托：输入已解析的 URL 与取消令牌，返回原始 JSON 文本。
    private readonly Func<string, CancellationToken, Task<string>> _fetch;

    // 把更新源标识（如 "github-releases" 别名或直接 URL）解析为可请求的 URL。
    private readonly Func<string, string> _sourceUrlResolver;

    /// <summary>
    /// 使用获取委托构造服务。
    /// </summary>
    /// <param name="fetch">获取 releases JSON 的委托；测试可传入假实现以断言被请求的 URL。</param>
    /// <param name="sourceUrlResolver">可选的「源标识 -> URL」解析器；为空时使用内置默认解析。</param>
    /// <exception cref="ArgumentNullException">fetch 为空时抛出。</exception>
    public UpdateCheckService(
        Func<string, CancellationToken, Task<string>> fetch,
        Func<string, string>? sourceUrlResolver = null)
    {
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        _sourceUrlResolver = sourceUrlResolver ?? DefaultResolveSourceUrl;
    }

    /// <summary>
    /// 使用 <see cref="IReleaseFeedClient"/> 构造服务。
    /// </summary>
    /// <param name="client">releases 数据获取客户端。</param>
    /// <param name="sourceUrlResolver">可选的「源标识 -> URL」解析器。</param>
    /// <exception cref="ArgumentNullException">client 为空时抛出。</exception>
    public UpdateCheckService(IReleaseFeedClient client, Func<string, string>? sourceUrlResolver = null)
        : this(
            (client ?? throw new ArgumentNullException(nameof(client))).FetchAsync,
            sourceUrlResolver)
    {
    }

    /// <summary>
    /// 手动检查更新：先查主源，主源失败（抛异常 / 空响应 / 无可用版本）时若配置了备用源则回退。
    /// </summary>
    /// <param name="currentVersion">当前应用版本，例如 "v0.1.0"。</param>
    /// <param name="settings">更新源设置（主源与可选备用源）。</param>
    /// <param name="includePrerelease">是否把预发布版本纳入候选；默认仅取稳定版。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>更新检查结果。两源都无法得到结果时 Failed=true。</returns>
    /// <exception cref="ArgumentException">currentVersion 为空时抛出。</exception>
    /// <exception cref="ArgumentNullException">settings 为空时抛出。</exception>
    public async Task<UpdateCheckResult> CheckAsync(
        string currentVersion,
        UpdateSettings settings,
        bool includePrerelease = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(currentVersion))
        {
            throw new ArgumentException("Current version is required.", nameof(currentVersion));
        }

        ArgumentNullException.ThrowIfNull(settings);

        // 先尝试主源。
        var primaryFailure = await TryResolveLatestAsync(
            settings.Source, includePrerelease, cancellationToken).ConfigureAwait(false);

        if (primaryFailure.Release is not null)
        {
            return BuildResult(currentVersion, primaryFailure.Release, usedFallback: false);
        }

        // 主源失败：若配置了备用源则回退。
        var primaryReason = primaryFailure.Reason ?? "Primary update source returned no usable release.";
        if (string.IsNullOrWhiteSpace(settings.FallbackSource))
        {
            return UpdateCheckResult.Failure(currentVersion, primaryReason);
        }

        var fallbackFailure = await TryResolveLatestAsync(
            settings.FallbackSource!, includePrerelease, cancellationToken).ConfigureAwait(false);

        if (fallbackFailure.Release is not null)
        {
            return BuildResult(currentVersion, fallbackFailure.Release, usedFallback: true);
        }

        // 两源都失败：汇总原因。
        var fallbackReason = fallbackFailure.Reason ?? "Fallback update source returned no usable release.";
        return UpdateCheckResult.Failure(
            currentVersion,
            $"Primary source failed ({primaryReason}); fallback source failed ({fallbackReason}).");
    }

    // 从单个源获取并解析出最新发布；失败时返回原因，不抛异常给上层。
    private async Task<(GitHubRelease? Release, string? Reason)> TryResolveLatestAsync(
        string source,
        bool includePrerelease,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return (null, "Update source is empty.");
        }

        string json;
        try
        {
            // 把源标识解析为 URL 后才发起获取——这是唯一的联网触点。
            var url = _sourceUrlResolver(source);
            json = await _fetch(url, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消应原样向上抛出，区别于普通获取失败。
            throw;
        }
        catch (Exception ex)
        {
            return (null, $"Fetch failed: {ex.Message}");
        }

        // 空响应视为失败，触发回退。
        if (string.IsNullOrWhiteSpace(json))
        {
            return (null, "Empty response from update source.");
        }

        var latest = GitHubReleaseParser.ParseAndSelectLatest(json, includePrerelease);
        if (latest is null)
        {
            return (null, "No usable release found in response.");
        }

        return (latest, null);
    }

    // 依据当前版本与最新发布构造成功结果，包含「是否有更新」判定。
    private static UpdateCheckResult BuildResult(
        string currentVersion,
        GitHubRelease latest,
        bool usedFallback)
    {
        var hasUpdate = GitHubReleaseParser.IsNewer(currentVersion, latest.TagName);
        return new UpdateCheckResult(
            HasUpdate: hasUpdate,
            LatestVersion: latest.TagName,
            CurrentVersion: currentVersion,
            ReleaseUrl: latest.HtmlUrl,
            UsedFallback: usedFallback,
            Failed: false,
            FailureReason: null);
    }

    // 默认源解析：识别 github-releases 别名；以 http(s) 开头者按 URL 原样返回；其余原样透传。
    private static string DefaultResolveSourceUrl(string source)
    {
        if (string.Equals(source, GitHubReleasesAlias, StringComparison.OrdinalIgnoreCase))
        {
            return DefaultGitHubReleasesUrl;
        }

        return source;
    }
}
