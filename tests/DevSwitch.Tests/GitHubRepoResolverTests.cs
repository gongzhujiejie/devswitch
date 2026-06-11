// 文件用途：验证 GitHubRepoResolver 的仓库标识解析与 owner/repo 形态校验逻辑。
// 创建/修改日期：2026-06-10
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit、DevSwitch.Core
// NOTE: 合法授权学习使用，仅限本地环境。纯逻辑测试，无任何网络请求。

using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class GitHubRepoResolverTests
{
    private const string ExpectedApi = "https://api.github.com/repos/owner/repo/releases";

    [Fact]
    public void ResolveReleasesApiUrl_OwnerRepo_BuildsApiUrl()
    {
        // "owner/repo" 形态应拼装为标准 Releases API URL。
        Assert.Equal(ExpectedApi, GitHubRepoResolver.ResolveReleasesApiUrl("owner/repo"));
    }

    [Fact]
    public void ResolveReleasesApiUrl_TrimsSurroundingWhitespace()
    {
        // 首尾空格应被容错处理。
        Assert.Equal(ExpectedApi, GitHubRepoResolver.ResolveReleasesApiUrl("  owner/repo  "));
    }

    [Fact]
    public void ResolveReleasesApiUrl_GitHubUrl_Plain_BuildsApiUrl()
    {
        Assert.Equal(ExpectedApi, GitHubRepoResolver.ResolveReleasesApiUrl("https://github.com/owner/repo"));
    }

    [Fact]
    public void ResolveReleasesApiUrl_GitHubUrl_WithGitSuffix_BuildsApiUrl()
    {
        Assert.Equal(ExpectedApi, GitHubRepoResolver.ResolveReleasesApiUrl("https://github.com/owner/repo.git"));
    }

    [Fact]
    public void ResolveReleasesApiUrl_GitHubUrl_WithTrailingSlash_BuildsApiUrl()
    {
        Assert.Equal(ExpectedApi, GitHubRepoResolver.ResolveReleasesApiUrl("https://github.com/owner/repo/"));
    }

    [Fact]
    public void ResolveReleasesApiUrl_GitHubUrl_WithGitSuffixAndTrailingSlash_BuildsApiUrl()
    {
        Assert.Equal(ExpectedApi, GitHubRepoResolver.ResolveReleasesApiUrl("https://github.com/owner/repo.git/"));
    }

    [Fact]
    public void ResolveReleasesApiUrl_AlreadyApiUrl_ReturnsAsIs()
    {
        // 已经是 api.github.com 地址：原样返回。
        const string api = "https://api.github.com/repos/foo/bar/releases";
        Assert.Equal(api, GitHubRepoResolver.ResolveReleasesApiUrl(api));
    }

    [Fact]
    public void ResolveReleasesApiUrl_Alias_ReturnsDefaultUrl()
    {
        // 旧别名 "github-releases" → 默认 API 地址。
        Assert.Equal(
            UpdateCheckService.DefaultGitHubReleasesUrl,
            GitHubRepoResolver.ResolveReleasesApiUrl(UpdateCheckService.GitHubReleasesAlias));
    }

    [Fact]
    public void ResolveReleasesApiUrl_Alias_IsCaseInsensitive()
    {
        Assert.Equal(
            UpdateCheckService.DefaultGitHubReleasesUrl,
            GitHubRepoResolver.ResolveReleasesApiUrl("GitHub-Releases"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveReleasesApiUrl_NullOrWhitespace_ReturnsNull(string? input)
    {
        // 空/空白表示未配置。
        Assert.Null(GitHubRepoResolver.ResolveReleasesApiUrl(input));
    }

    [Theory]
    [InlineData("noslash")]
    [InlineData("a/b/c")]
    [InlineData("owner/")]
    [InlineData("/repo")]
    public void ResolveReleasesApiUrl_Invalid_ReturnsNull(string input)
    {
        // 非 owner/repo、非已知 URL 形态：无法解析。
        Assert.Null(GitHubRepoResolver.ResolveReleasesApiUrl(input));
    }

    [Theory]
    [InlineData("owner/repo")]
    [InlineData("Owner-1/repo_name.v2")]
    [InlineData("  owner/repo  ")]
    public void IsValidRepository_ValidForms_ReturnsTrue(string input)
    {
        Assert.True(GitHubRepoResolver.IsValidRepository(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("noslash")]
    [InlineData("a/b/c")]
    [InlineData("owner/")]
    [InlineData("/repo")]
    [InlineData("https://github.com/owner/repo")]
    public void IsValidRepository_InvalidForms_ReturnsFalse(string? input)
    {
        // 含协议头的完整 URL 不属于 owner/repo 形态，校验应判负（解析交给 ResolveReleasesApiUrl）。
        Assert.False(GitHubRepoResolver.IsValidRepository(input));
    }
}
