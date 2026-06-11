// 文件用途：解析 GitHub Releases API JSON，并提供 SemVer-ish 版本比较与「最新稳定版」提取等纯函数。
// 创建/修改日期：2026-06-10
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.Text.Json（BCL，无第三方包）
// NOTE: 合法授权学习使用，仅限本地环境。本文件全部为纯函数，不发起任何网络请求。

using System.Text.Json;

namespace DevSwitch.Core;

/// <summary>
/// GitHub Releases 响应解析器（纯函数集合）。
/// 负责把原始 JSON 解析为 <see cref="GitHubRelease"/>，并据此挑选最新稳定/可用版本。
/// 对缺字段、空输入、非法 JSON 一律稳健返回空集合或 null，不抛异常。
/// </summary>
public static class GitHubReleaseParser
{
    /// <summary>
    /// 解析 GitHub Releases API 返回的 JSON 数组。
    /// </summary>
    /// <param name="json">releases API 的原始响应体。</param>
    /// <returns>解析出的发布列表；输入非法或为空时返回空列表。</returns>
    public static IReadOnlyList<GitHubRelease> Parse(string? json)
    {
        // 防御：空字符串/空白直接返回空列表，避免后续解析抛错。
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<GitHubRelease>();
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            // GitHub Releases API 顶层应为数组；其它形态视为无可用数据。
            if (root.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<GitHubRelease>();
            }

            var releases = new List<GitHubRelease>();
            foreach (var element in root.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                // tag_name 是版本判定的核心字段，缺失则该项无意义，跳过。
                var tagName = GetString(element, "tag_name");
                if (string.IsNullOrWhiteSpace(tagName))
                {
                    continue;
                }

                var release = new GitHubRelease(
                    TagName: tagName,
                    Name: GetString(element, "name"),
                    Prerelease: GetBool(element, "prerelease"),
                    Draft: GetBool(element, "draft"),
                    HtmlUrl: GetString(element, "html_url"),
                    Assets: ParseAssets(element));

                releases.Add(release);
            }

            return releases;
        }
        catch (JsonException)
        {
            // 非法 JSON：保持稳健，返回空列表而非抛出。
            return Array.Empty<GitHubRelease>();
        }
    }

    /// <summary>
    /// 从已解析的发布列表中挑选「最新版本」。
    /// </summary>
    /// <param name="releases">发布列表。</param>
    /// <param name="includePrerelease">是否把预发布版本纳入候选；默认仅取稳定版。</param>
    /// <returns>按语义版本比较得到的最新发布；无候选时返回 null。</returns>
    public static GitHubRelease? SelectLatest(
        IReadOnlyList<GitHubRelease> releases,
        bool includePrerelease = false)
    {
        if (releases is null || releases.Count == 0)
        {
            return null;
        }

        GitHubRelease? latest = null;
        foreach (var release in releases)
        {
            // 草稿一律跳过：尚未正式公开，不应提示用户更新。
            if (release.Draft)
            {
                continue;
            }

            // 不包含预发布时，过滤掉 prerelease 项。
            if (!includePrerelease && release.Prerelease)
            {
                continue;
            }

            // 取语义版本最大的一项作为最新版本。
            if (latest is null || CompareVersions(release.TagName, latest.TagName) > 0)
            {
                latest = release;
            }
        }

        return latest;
    }

    /// <summary>
    /// 直接从原始 JSON 解析并挑选最新版本的便捷组合方法。
    /// </summary>
    /// <param name="json">releases API 原始响应体。</param>
    /// <param name="includePrerelease">是否纳入预发布版本。</param>
    /// <returns>最新发布；无可用数据时返回 null。</returns>
    public static GitHubRelease? ParseAndSelectLatest(string? json, bool includePrerelease = false) =>
        SelectLatest(Parse(json), includePrerelease);

    /// <summary>
    /// SemVer-ish 版本比较纯函数。去掉前缀 v；按 major.minor.patch 数值比较；
    /// 预发布版本（带 -suffix）低于同主版本号的正式版。
    /// </summary>
    /// <param name="left">左版本，例如 "v0.1.0"。</param>
    /// <param name="right">右版本，例如 "0.2.0"。</param>
    /// <returns>left&lt;right 返回负数，相等返回 0，left&gt;right 返回正数。</returns>
    public static int CompareVersions(string? left, string? right)
    {
        // null/空白归一化到空串，保证比较稳健。
        var leftRaw = (left ?? string.Empty).Trim();
        var rightRaw = (right ?? string.Empty).Trim();

        var (leftCore, leftPre) = SplitVersion(leftRaw);
        var (rightCore, rightPre) = SplitVersion(rightRaw);

        // 先比较主体数值段 major.minor.patch...
        var coreComparison = CompareNumericSegments(leftCore, rightCore);
        if (coreComparison != 0)
        {
            return coreComparison;
        }

        // 主体相等时：无预发布后缀者更高（正式版 > 预发布版）。
        var leftHasPre = !string.IsNullOrEmpty(leftPre);
        var rightHasPre = !string.IsNullOrEmpty(rightPre);
        if (leftHasPre && !rightHasPre)
        {
            return -1;
        }

        if (!leftHasPre && rightHasPre)
        {
            return 1;
        }

        // 两者都有预发布后缀：按字典序粗比较（rc.1 < rc.2 这类常见场景可用）。
        return string.CompareOrdinal(leftPre, rightPre);
    }

    /// <summary>
    /// 判断 candidate 是否比 current 更新（纯函数，便于单测）。
    /// </summary>
    /// <param name="current">当前版本。</param>
    /// <param name="candidate">候选最新版本。</param>
    /// <returns>candidate 严格大于 current 时为 true。</returns>
    public static bool IsNewer(string? current, string? candidate) =>
        CompareVersions(candidate, current) > 0;

    // 解析单个发布的 assets 数组；缺失或非数组时返回空列表。
    private static IReadOnlyList<GitHubReleaseAsset> ParseAssets(JsonElement releaseElement)
    {
        if (!releaseElement.TryGetProperty("assets", out var assetsElement)
            || assetsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<GitHubReleaseAsset>();
        }

        var assets = new List<GitHubReleaseAsset>();
        foreach (var assetElement in assetsElement.EnumerateArray())
        {
            if (assetElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = GetString(assetElement, "name");
            var url = GetString(assetElement, "browser_download_url");

            // 资产必须同时有名称和下载地址才有意义。
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            assets.Add(new GitHubReleaseAsset(name, url));
        }

        return assets;
    }

    // 拆分版本字符串为「数值主体」与「预发布后缀」，并去掉前导 v/V。
    private static (string Core, string Pre) SplitVersion(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return (string.Empty, string.Empty);
        }

        // 去掉前缀 v（如 v0.2.0 -> 0.2.0）。
        var trimmed = raw;
        if (trimmed.Length > 0 && (trimmed[0] == 'v' || trimmed[0] == 'V'))
        {
            trimmed = trimmed[1..];
        }

        // 预发布以第一个 '-' 分界（如 1.0.0-rc.1）。
        var dashIndex = trimmed.IndexOf('-');
        if (dashIndex < 0)
        {
            return (trimmed, string.Empty);
        }

        var core = trimmed[..dashIndex];
        var pre = trimmed[(dashIndex + 1)..];
        return (core, pre);
    }

    // 比较点分数值段，缺失尾段按 0 处理（1.2 == 1.2.0）。
    private static int CompareNumericSegments(string left, string right)
    {
        var leftParts = left.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var rightParts = right.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var max = Math.Max(leftParts.Length, rightParts.Length);

        for (var i = 0; i < max; i++)
        {
            var leftValue = ParseSegment(leftParts, i);
            var rightValue = ParseSegment(rightParts, i);

            var comparison = leftValue.CompareTo(rightValue);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    // 取第 index 段并解析为数值；越界或非数字按 0 处理，保证稳健。
    private static long ParseSegment(string[] parts, int index)
    {
        if (index >= parts.Length)
        {
            return 0;
        }

        return long.TryParse(parts[index], out var value) ? value : 0;
    }

    // 读取字符串属性，缺失或类型不符时返回 null。
    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    // 读取布尔属性，缺失或类型不符时返回 false。
    private static bool GetBool(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var value))
        {
            if (value.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (value.ValueKind == JsonValueKind.False)
            {
                return false;
            }
        }

        return false;
    }
}
