// 文件用途：验证 GitHubReleaseParser 对 GitHub Releases JSON 的解析、草稿/预发布过滤、版本比较等公开行为。
// 创建/修改日期：2026-06-10
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit、DevSwitch.Core
// NOTE: 合法授权学习使用，仅限本地环境。本测试只喂内嵌样例 JSON，不发任何真实网络请求。

using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class GitHubReleaseParserTests
{
    // 混合样例：稳定版 0.2.0、预发布 0.3.0-rc.1、草稿 0.4.0、旧版 0.1.0。
    private const string SampleJson = """
    [
      {
        "tag_name": "v0.2.0",
        "name": "DevSwitch 0.2.0",
        "prerelease": false,
        "draft": false,
        "html_url": "https://github.com/devswitch/devswitch/releases/tag/v0.2.0",
        "assets": [
          { "name": "DevSwitch-0.2.0-win-x64.zip", "browser_download_url": "https://example.org/0.2.0.zip" }
        ]
      },
      {
        "tag_name": "v0.3.0-rc.1",
        "name": "DevSwitch 0.3.0 RC1",
        "prerelease": true,
        "draft": false,
        "html_url": "https://github.com/devswitch/devswitch/releases/tag/v0.3.0-rc.1",
        "assets": []
      },
      {
        "tag_name": "v0.4.0",
        "name": "Draft 0.4.0",
        "prerelease": false,
        "draft": true,
        "html_url": "https://github.com/devswitch/devswitch/releases/tag/v0.4.0",
        "assets": []
      },
      {
        "tag_name": "v0.1.0",
        "name": "DevSwitch 0.1.0",
        "prerelease": false,
        "draft": false,
        "html_url": "https://github.com/devswitch/devswitch/releases/tag/v0.1.0",
        "assets": []
      }
    ]
    """;

    [Fact]
    public void ParseReadsAllReleasesWithFields()
    {
        var releases = GitHubReleaseParser.Parse(SampleJson);

        Assert.Equal(4, releases.Count);
        var stable = Assert.Single(releases, r => r.TagName == "v0.2.0");
        Assert.Equal("DevSwitch 0.2.0", stable.Name);
        Assert.False(stable.Prerelease);
        Assert.False(stable.Draft);
        Assert.Equal("https://github.com/devswitch/devswitch/releases/tag/v0.2.0", stable.HtmlUrl);
        var asset = Assert.Single(stable.Assets);
        Assert.Equal("DevSwitch-0.2.0-win-x64.zip", asset.Name);
        Assert.Equal("https://example.org/0.2.0.zip", asset.BrowserDownloadUrl);
    }

    [Fact]
    public void SelectLatestSkipsDraftAndPrereleaseByDefault()
    {
        var releases = GitHubReleaseParser.Parse(SampleJson);

        var latest = GitHubReleaseParser.SelectLatest(releases);

        // 跳过草稿 0.4.0 与预发布 0.3.0-rc.1，最新稳定版应为 0.2.0。
        Assert.NotNull(latest);
        Assert.Equal("v0.2.0", latest!.TagName);
    }

    [Fact]
    public void SelectLatestIncludesPrereleaseWhenRequested()
    {
        var releases = GitHubReleaseParser.Parse(SampleJson);

        var latest = GitHubReleaseParser.SelectLatest(releases, includePrerelease: true);

        // 纳入预发布但仍跳过草稿；0.3.0-rc.1 > 0.2.0。
        Assert.NotNull(latest);
        Assert.Equal("v0.3.0-rc.1", latest!.TagName);
    }

    [Fact]
    public void SelectLatestNeverReturnsDraft()
    {
        // 仅含一个草稿时应返回 null（草稿永不作为可提示版本）。
        const string draftOnly = """
        [ { "tag_name": "v9.9.9", "prerelease": false, "draft": true } ]
        """;

        var latest = GitHubReleaseParser.SelectLatest(
            GitHubReleaseParser.Parse(draftOnly), includePrerelease: true);

        Assert.Null(latest);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("123")]
    public void ParseReturnsEmptyForInvalidInput(string? input)
    {
        Assert.Empty(GitHubReleaseParser.Parse(input));
    }

    [Fact]
    public void ParseSkipsItemsMissingTagAndIsRobustToMissingFields()
    {
        const string dirty = """
        [
          { "name": "no tag here", "draft": false },
          { "tag_name": "v1.0.0" }
        ]
        """;

        var releases = GitHubReleaseParser.Parse(dirty);

        // 缺 tag_name 的项被跳过；保留项的缺失字段稳健回退。
        var only = Assert.Single(releases);
        Assert.Equal("v1.0.0", only.TagName);
        Assert.Null(only.Name);
        Assert.Null(only.HtmlUrl);
        Assert.False(only.Prerelease);
        Assert.False(only.Draft);
        Assert.Empty(only.Assets);
    }

    [Fact]
    public void ParseSkipsAssetsMissingNameOrUrl()
    {
        const string json = """
        [
          {
            "tag_name": "v1.0.0",
            "assets": [
              { "name": "ok.zip", "browser_download_url": "https://example.org/ok.zip" },
              { "name": "no-url.zip" },
              { "browser_download_url": "https://example.org/no-name.zip" }
            ]
          }
        ]
        """;

        var release = Assert.Single(GitHubReleaseParser.Parse(json));
        var asset = Assert.Single(release.Assets);
        Assert.Equal("ok.zip", asset.Name);
    }

    [Theory]
    [InlineData("v0.1.0", "0.2.0")]   // 去前缀 v 后 0.1 < 0.2
    [InlineData("0.2.0", "1.0.0")]
    [InlineData("1.0.0-rc.1", "1.0.0")] // 预发布 < 正式
    [InlineData("1.2.0", "1.10.0")]     // 数值比较 2 < 10
    [InlineData("1.0.0-rc.1", "1.0.0-rc.2")]
    public void CompareVersionsOrdersLowerBeforeHigher(string lower, string higher)
    {
        Assert.True(GitHubReleaseParser.CompareVersions(lower, higher) < 0);
        Assert.True(GitHubReleaseParser.CompareVersions(higher, lower) > 0);
    }

    [Fact]
    public void CompareVersionsTreatsMissingTrailingSegmentsAsZero()
    {
        Assert.Equal(0, GitHubReleaseParser.CompareVersions("v1.2", "1.2.0"));
    }

    [Fact]
    public void CompareVersionsHandlesNullAndEmptyWithoutThrowing()
    {
        Assert.Equal(0, GitHubReleaseParser.CompareVersions(null, null));
        Assert.Equal(0, GitHubReleaseParser.CompareVersions("", ""));
        Assert.True(GitHubReleaseParser.CompareVersions(null, "1.0.0") < 0);
    }

    [Theory]
    [InlineData("v0.1.0", "v0.2.0", true)]   // 有更新
    [InlineData("v0.2.0", "v0.2.0", false)]  // 已是最新
    [InlineData("v0.3.0", "v0.2.0", false)]  // 本地比远端还新
    public void IsNewerReflectsComparison(string current, string candidate, bool expected)
    {
        Assert.Equal(expected, GitHubReleaseParser.IsNewer(current, candidate));
    }
}
