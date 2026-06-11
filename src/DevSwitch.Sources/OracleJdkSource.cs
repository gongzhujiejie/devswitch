// 文件用途：实现 Oracle JDK 官方 NFTC 区直链源（无登录、无 Cookie）。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.Threading、System.Linq、DevSwitch.Core
// NOTE: 合法授权学习使用，仅限本地环境。Oracle NFTC 协议允许程序代用户从官源拉取，不需要登录。

using DevSwitch.Core;

namespace DevSwitch.Sources;

/// <summary>
/// Oracle JDK 官方源（NFTC /latest/ 直链）。
/// </summary>
/// <remarks>
/// 与基于 fetcher+parser 的 <see cref="HttpSdkVersionSource"/> 不同，
/// Oracle 不暴露版本列表 API，只提供形如
/// <c>https://download.oracle.com/java/{N}/latest/jdk-{N}_windows-x64_bin.zip</c>
/// 的稳定直链。该源的工作模式为：
/// 1. 内置 NFTC 大版本白名单（仅 21/25/26 走 NFTC 协议，17/11/8 走 OTN 登录墙，故不支持）。
/// 2. 对每个大版本调用注入的"存活探测器"做 HEAD/GET 探活，失败则跳过。
/// 3. 把存活的大版本合成为 <see cref="SdkSourceVersion"/>，版本号写为 <c>{N}.latest</c>。
/// 4. SHA256 单独提供 ChecksumUrl，由下载流水线运行时拉取（同名 .sha256，正文 64 字节裸 hex）。
///
/// /latest/ 路径不暴露具体小版本号，因此版本号统一为 "{N}.latest"；下载 pipeline 解压后
/// 可从 zip 内 release 文件读取真实小版本号，本源阶段不做解压。
/// 仅支持 x64：Oracle NFTC 不提供 Windows arm64 包；arm64 请求直接返回空列表。
/// </remarks>
public sealed class OracleJdkSource : ISdkVersionSource
{
    /// <summary>
    /// 默认发行版标识。
    /// </summary>
    public const string DefaultDistribution = "oracle-jdk";

    /// <summary>
    /// NFTC 协议覆盖的 Oracle JDK 大版本号白名单。
    /// </summary>
    /// <remarks>
    /// 当前为 21/25/26：21 是 LTS、25 是新 LTS、26 是非 LTS 滚动。未来若 Oracle 新增/下线 NFTC 大版本，仅需更新此处。
    /// 17/11/8 走 OTN 登录墙、不可匿名直连，不在此列。
    /// </remarks>
    public static readonly IReadOnlyList<int> DefaultFeatureVersions = new[] { 21, 25, 26 };

    private readonly IReadOnlyList<int> _featureVersions;
    private readonly Func<string, CancellationToken, Task<bool>> _aliveProbe;

    /// <summary>
    /// 构造 Oracle JDK 源。
    /// </summary>
    /// <param name="aliveProbe">
    /// 存活探测委托：输入 zip 直链，返回是否可达（一般实现为 HTTP HEAD == 200）。
    /// 抽出此委托是为了在测试中注入假探活器，不发真实网络请求。
    /// </param>
    /// <param name="featureVersions">
    /// 可选：自定义 NFTC 大版本号集合；缺省使用 <see cref="DefaultFeatureVersions"/>。
    /// </param>
    public OracleJdkSource(
        Func<string, CancellationToken, Task<bool>> aliveProbe,
        IReadOnlyList<int>? featureVersions = null)
    {
        ArgumentNullException.ThrowIfNull(aliveProbe);
        _aliveProbe = aliveProbe;
        // 若调用方传空集合，回退默认列表，避免出现"探活循环执行 0 次永远返回空"的静默故障。
        _featureVersions = featureVersions is { Count: > 0 } ? featureVersions : DefaultFeatureVersions;
    }

    /// <inheritdoc />
    public SdkType SdkType => SdkType.Java;

    /// <inheritdoc />
    public string Distribution => DefaultDistribution;

    /// <inheritdoc />
    public async Task<IReadOnlyList<SdkSourceVersion>> ListVersionsAsync(
        SdkArchitecture architecture,
        CancellationToken cancellationToken = default)
    {
        // Oracle NFTC 仅提供 Windows x64；其它架构（含 Any/Arm64/Unknown）一律返回空。
        // NOTE: 这里特意不接受 Any，因为 Oracle 官方 catalog 不能保证未来出 arm64；
        //       让 UI 在用户明确选 X64 时才显示 Oracle 项更稳。
        if (architecture != SdkArchitecture.X64)
        {
            return Array.Empty<SdkSourceVersion>();
        }

        // 并发探活：3 个版本量级很小，直接 Task.WhenAll 即可，避免顺序探活引入无谓延迟。
        var probeTasks = _featureVersions
            .Select(async feature =>
            {
                var url = BuildDownloadUrl(feature);
                try
                {
                    var alive = await _aliveProbe(url, cancellationToken).ConfigureAwait(false);
                    return (feature, alive);
                }
                catch (OperationCanceledException)
                {
                    // 取消语义需要冒泡到上层，让 ListVersionsAsync 整体取消。
                    throw;
                }
                catch
                {
                    // 任何探活异常都视为不可用，不让单个版本拖垮整张列表。
                    return (feature, alive: false);
                }
            })
            .ToArray();

        var results = await Task.WhenAll(probeTasks).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        // 收集存活的大版本号；保持源数组顺序，后续由 SynthesizeVersions 内部统一降序排序。
        var aliveFeatures = results
            .Where(r => r.alive)
            .Select(r => r.feature)
            .ToArray();

        return SynthesizeVersions(aliveFeatures, architecture);
    }

    /// <summary>
    /// 把"存活的大版本号列表"合成为按版本降序排序的 <see cref="SdkSourceVersion"/> 列表。
    /// </summary>
    /// <param name="aliveFeatures">已确认可下载的 Oracle JDK 大版本号集合。</param>
    /// <param name="architecture">目标架构；非 X64 一律视为不支持，返回空列表。</param>
    /// <returns>按版本降序的版本列表（最新在前）。</returns>
    /// <remarks>
    /// 纯函数：不依赖任何实例状态、不发起 IO，便于在单测中独立验证 URL pattern 与排序。
    /// </remarks>
    public static IReadOnlyList<SdkSourceVersion> SynthesizeVersions(
        IReadOnlyList<int> aliveFeatures,
        SdkArchitecture architecture)
    {
        ArgumentNullException.ThrowIfNull(aliveFeatures);

        if (architecture != SdkArchitecture.X64 || aliveFeatures.Count == 0)
        {
            return Array.Empty<SdkSourceVersion>();
        }

        var list = new List<SdkSourceVersion>(aliveFeatures.Count);
        foreach (var feature in aliveFeatures)
        {
            // feature 必须为正整数；非法值跳过而非抛异常，保持"列表式 API"的容错风格。
            if (feature <= 0)
            {
                continue;
            }

            var downloadUrl = BuildDownloadUrl(feature);
            // .sha256 文件正文为 64 字节裸 hex（无文件名前缀），由下载流水线运行时拉取并校验。
            var checksumUrl = downloadUrl + ".sha256";

            list.Add(new SdkSourceVersion(
                SdkType: SdkType.Java,
                // Version 使用 "{N}.latest" 格式：/latest/ 路径不暴露具体小版本号，
                // 但 SemanticVersionComparer 仍能按首段数值比较实现"26 > 25 > 21"的降序。
                Version: $"{feature}.latest",
                Distribution: DefaultDistribution,
                Architecture: SdkArchitecture.X64,
                DownloadUrl: downloadUrl,
                Sha256: null,
                ChecksumUrl: checksumUrl,
                ReleaseDate: null,
                DisplayName: $"Oracle JDK {feature} (latest, x64)"));
        }

        // 统一降序：复用 HttpSdkVersionSource.SortDescending，保证与其它源一致的排序行为。
        return HttpSdkVersionSource.SortDescending(list);
    }

    /// <summary>
    /// 构造 Oracle JDK NFTC /latest/ 的 Windows x64 zip 直链。
    /// </summary>
    /// <param name="featureVersion">JDK 大版本号，例如 21。</param>
    /// <returns>形如 <c>https://download.oracle.com/java/21/latest/jdk-21_windows-x64_bin.zip</c> 的直链。</returns>
    /// <remarks>
    /// 该 URL pattern 已稳定 5 年以上；外部调用者也可复用以构造 .sha256 URL（追加 ".sha256"）。
    /// </remarks>
    public static string BuildDownloadUrl(int featureVersion)
    {
        return $"https://download.oracle.com/java/{featureVersion}/latest/jdk-{featureVersion}_windows-x64_bin.zip";
    }
}
