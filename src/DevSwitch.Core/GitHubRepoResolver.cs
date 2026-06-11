// 文件用途：把用户填写的「仓库标识」解析为 GitHub Releases API URL，并提供输入校验。
// 创建/修改日期：2026-06-10
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：仅 BCL（无第三方包）
// NOTE: 合法授权学习使用，仅限本地环境。本类为纯逻辑，不发起任何网络请求。

using System.Text.RegularExpressions;

namespace DevSwitch.Core;

/// <summary>
/// 仓库标识解析器。把设置页填写的多种形态（owner/repo、github.com URL、api.github.com URL、旧别名）
/// 统一解析为可请求的 GitHub Releases API 地址；同时提供 owner/repo 形态校验。
/// </summary>
public static class GitHubRepoResolver
{
    // owner/repo 形态正则：owner 与 repo 均允许字母、数字、'.'、'_'、'-'，且恰好一个 '/' 分隔、无额外段。
    // NOTE: 锚定 ^...$ 保证整体匹配，避免 "a/b/c" 这类多段被误判为合法。
    private static readonly Regex OwnerRepoPattern = new(
        @"^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 把仓库标识解析为 GitHub Releases API URL。
    /// </summary>
    /// <param name="repository">
    /// 仓库标识，容错支持：
    /// <list type="bullet">
    /// <item>"owner/repo" → https://api.github.com/repos/owner/repo/releases</item>
    /// <item>"https://github.com/owner/repo"（含可选 .git 与结尾斜杠） → 同上</item>
    /// <item>已是 "https://api.github.com/..." → 原样返回（去除首尾空格）</item>
    /// <item>"github-releases" 旧别名 → <see cref="UpdateCheckService.DefaultGitHubReleasesUrl"/></item>
    /// <item>空/空白 → null（表示未配置）</item>
    /// </list>
    /// </param>
    /// <returns>解析出的 API URL；无法解析或未配置时返回 null。</returns>
    public static string? ResolveReleasesApiUrl(string? repository)
    {
        // 空/空白：视为未配置。
        if (string.IsNullOrWhiteSpace(repository))
        {
            return null;
        }

        var value = repository.Trim();

        // 1) 旧别名：直接映射到默认 API 地址（大小写不敏感）。
        if (string.Equals(value, UpdateCheckService.GitHubReleasesAlias, StringComparison.OrdinalIgnoreCase))
        {
            return UpdateCheckService.DefaultGitHubReleasesUrl;
        }

        // 2) 已经是 api.github.com 地址：原样返回（仅去除首尾空格）。
        if (value.StartsWith("https://api.github.com/", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("http://api.github.com/", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        // 3) github.com 网页地址：抽取 owner/repo 段后再拼装 API URL。
        var fromUrl = TryExtractOwnerRepoFromGitHubUrl(value);
        if (fromUrl is not null)
        {
            return BuildReleasesApiUrl(fromUrl.Value.Owner, fromUrl.Value.Repo);
        }

        // 4) 纯 owner/repo 形态。
        if (OwnerRepoPattern.IsMatch(value))
        {
            var parts = value.Split('/');
            return BuildReleasesApiUrl(parts[0], parts[1]);
        }

        // 其余无法识别：返回 null，交由上层提示输入无效。
        return null;
    }

    /// <summary>
    /// 校验仓库标识是否为合法的 "owner/repo" 形态（用于设置页输入实时校验）。
    /// </summary>
    /// <param name="repository">待校验文本；首尾空格容错。</param>
    /// <returns>合法返回 true，否则 false。空/空白视为非法。</returns>
    public static bool IsValidRepository(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            return false;
        }

        return OwnerRepoPattern.IsMatch(repository.Trim());
    }

    // 从 github.com 网页地址中抽取 (owner, repo)。
    // 容错：可选 http/https、可选结尾斜杠、repo 名可选 ".git" 后缀；多于两段（如含子路径）则视为不可解析。
    private static (string Owner, string Repo)? TryExtractOwnerRepoFromGitHubUrl(string value)
    {
        // 仅处理指向 github.com 的网页地址。
        const string httpsPrefix = "https://github.com/";
        const string httpPrefix = "http://github.com/";

        string? rest = null;
        if (value.StartsWith(httpsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            rest = value[httpsPrefix.Length..];
        }
        else if (value.StartsWith(httpPrefix, StringComparison.OrdinalIgnoreCase))
        {
            rest = value[httpPrefix.Length..];
        }

        if (rest is null)
        {
            return null;
        }

        // 去掉结尾斜杠，便于后续分段。
        rest = rest.TrimEnd('/');

        // 按 '/' 拆分，必须恰好得到 owner、repo 两段。
        var segments = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2)
        {
            return null;
        }

        var owner = segments[0];
        var repo = segments[1];

        // 去掉 repo 名可能携带的 ".git" 克隆后缀。
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            repo = repo[..^4];
        }

        // 抽取后仍需满足合法字符集，避免把异常字符带入 API URL。
        if (owner.Length == 0 || repo.Length == 0 ||
            !OwnerRepoPattern.IsMatch($"{owner}/{repo}"))
        {
            return null;
        }

        return (owner, repo);
    }

    // 统一拼装 Releases API URL。
    private static string BuildReleasesApiUrl(string owner, string repo) =>
        $"https://api.github.com/repos/{owner}/{repo}/releases";
}
