// 文件用途：验证 UpdateCheckService 的主源/备用源回退、有无更新判定、失败处理与「不主动联网」契约。
// 创建/修改日期：2026-06-10
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit、DevSwitch.Core
// NOTE: 合法授权学习使用，仅限本地环境。所有获取委托均为假实现，绝不发起真实网络请求。

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class UpdateCheckServiceTests
{
    // 含 0.1.0 / 0.2.0 两个稳定版的 releases 样例。
    private const string ReleasesJson = """
    [
      {
        "tag_name": "v0.2.0",
        "prerelease": false,
        "draft": false,
        "html_url": "https://github.com/devswitch/devswitch/releases/tag/v0.2.0",
        "assets": []
      },
      {
        "tag_name": "v0.1.0",
        "prerelease": false,
        "draft": false,
        "html_url": "https://github.com/devswitch/devswitch/releases/tag/v0.1.0",
        "assets": []
      }
    ]
    """;

    // 记录被请求 URL 的假获取委托，用于断言「只在显式调用时联网、且请求了预期 URL」。
    private sealed class RecordingFetcher
    {
        private readonly Func<string, string?> _responder;

        public RecordingFetcher(Func<string, string?> responder) => _responder = responder;

        public List<string> RequestedUrls { get; } = new();

        public Task<string> FetchAsync(string url, CancellationToken cancellationToken)
        {
            RequestedUrls.Add(url);
            var response = _responder(url);
            if (response is null)
            {
                // 用异常模拟该源不可用。
                throw new InvalidOperationException($"source unavailable: {url}");
            }

            return Task.FromResult(response);
        }
    }

    [Fact]
    public void ConstructorDoesNotFetch()
    {
        var fetcher = new RecordingFetcher(_ => ReleasesJson);

        // 仅构造，不调用 CheckAsync。
        _ = new UpdateCheckService(fetcher.FetchAsync);

        // 契约：构造期间绝不联网。
        Assert.Empty(fetcher.RequestedUrls);
    }

    [Fact]
    public async Task CheckAsyncDetectsUpdateFromPrimarySource()
    {
        var fetcher = new RecordingFetcher(_ => ReleasesJson);
        var service = new UpdateCheckService(fetcher.FetchAsync);
        var settings = new UpdateSettings("github-releases", FallbackSource: null);

        var result = await service.CheckAsync("v0.1.0", settings);

        Assert.True(result.HasUpdate);
        Assert.Equal("v0.2.0", result.LatestVersion);
        Assert.Equal("v0.1.0", result.CurrentVersion);
        Assert.Equal("https://github.com/devswitch/devswitch/releases/tag/v0.2.0", result.ReleaseUrl);
        Assert.False(result.UsedFallback);
        Assert.False(result.Failed);

        // 不联网契约：只请求了一次，且命中 github-releases 别名解析出的默认 URL。
        var url = Assert.Single(fetcher.RequestedUrls);
        Assert.Equal(UpdateCheckService.DefaultGitHubReleasesUrl, url);
    }

    [Fact]
    public async Task CheckAsyncReportsNoUpdateWhenCurrentIsLatest()
    {
        var fetcher = new RecordingFetcher(_ => ReleasesJson);
        var service = new UpdateCheckService(fetcher.FetchAsync);
        var settings = new UpdateSettings("github-releases", null);

        var result = await service.CheckAsync("v0.2.0", settings);

        Assert.False(result.HasUpdate);
        Assert.Equal("v0.2.0", result.LatestVersion);
        Assert.False(result.Failed);
    }

    [Fact]
    public async Task CheckAsyncFallsBackToSecondarySourceWhenPrimaryFails()
    {
        const string primary = "https://primary.example/releases";
        const string fallback = "https://fallback.example/releases";

        // 主源抛异常（返回 null 触发异常），备用源返回数据。
        var fetcher = new RecordingFetcher(url => url == fallback ? ReleasesJson : null);
        var service = new UpdateCheckService(fetcher.FetchAsync);
        var settings = new UpdateSettings(primary, fallback);

        var result = await service.CheckAsync("v0.1.0", settings);

        Assert.True(result.HasUpdate);
        Assert.True(result.UsedFallback);
        Assert.Equal("v0.2.0", result.LatestVersion);
        Assert.False(result.Failed);

        // 验证请求顺序：先主源，后备用源。
        Assert.Equal(new[] { primary, fallback }, fetcher.RequestedUrls);
    }

    [Fact]
    public async Task CheckAsyncFallsBackWhenPrimaryReturnsEmpty()
    {
        const string primary = "https://primary.example/releases";
        const string fallback = "https://fallback.example/releases";

        // 主源返回空字符串（非异常），也应触发回退。
        var fetcher = new RecordingFetcher(url => url == primary ? string.Empty : ReleasesJson);
        var service = new UpdateCheckService(fetcher.FetchAsync);
        var settings = new UpdateSettings(primary, fallback);

        var result = await service.CheckAsync("v0.1.0", settings);

        Assert.True(result.UsedFallback);
        Assert.True(result.HasUpdate);
    }

    [Fact]
    public async Task CheckAsyncFallsBackWhenPrimaryHasNoUsableRelease()
    {
        const string primary = "https://primary.example/releases";
        const string fallback = "https://fallback.example/releases";

        // 主源只有草稿（无可用版本），备用源有正式版。
        const string draftOnly = """[ { "tag_name": "v9.0.0", "draft": true } ]""";
        var fetcher = new RecordingFetcher(url => url == primary ? draftOnly : ReleasesJson);
        var service = new UpdateCheckService(fetcher.FetchAsync);
        var settings = new UpdateSettings(primary, fallback);

        var result = await service.CheckAsync("v0.1.0", settings);

        Assert.True(result.UsedFallback);
        Assert.Equal("v0.2.0", result.LatestVersion);
    }

    [Fact]
    public async Task CheckAsyncReturnsFailedWhenBothSourcesFail()
    {
        const string primary = "https://primary.example/releases";
        const string fallback = "https://fallback.example/releases";

        // 两源都抛异常。
        var fetcher = new RecordingFetcher(_ => null);
        var service = new UpdateCheckService(fetcher.FetchAsync);
        var settings = new UpdateSettings(primary, fallback);

        var result = await service.CheckAsync("v0.1.0", settings);

        Assert.True(result.Failed);
        Assert.False(result.HasUpdate);
        Assert.Null(result.LatestVersion);
        Assert.NotNull(result.FailureReason);

        // 两源都被尝试过。
        Assert.Equal(new[] { primary, fallback }, fetcher.RequestedUrls);
    }

    [Fact]
    public async Task CheckAsyncReturnsFailedWhenPrimaryFailsAndNoFallbackConfigured()
    {
        var fetcher = new RecordingFetcher(_ => null);
        var service = new UpdateCheckService(fetcher.FetchAsync);
        var settings = new UpdateSettings("github-releases", FallbackSource: null);

        var result = await service.CheckAsync("v0.1.0", settings);

        Assert.True(result.Failed);
        Assert.NotNull(result.FailureReason);

        // 无备用源：只尝试主源一次。
        Assert.Single(fetcher.RequestedUrls);
    }

    [Fact]
    public async Task CheckAsyncIncludesPrereleaseWhenRequested()
    {
        const string json = """
        [
          { "tag_name": "v0.2.0", "prerelease": false, "draft": false, "html_url": "https://x/0.2.0" },
          { "tag_name": "v0.3.0-rc.1", "prerelease": true, "draft": false, "html_url": "https://x/0.3.0-rc.1" }
        ]
        """;
        var fetcher = new RecordingFetcher(_ => json);
        var service = new UpdateCheckService(fetcher.FetchAsync);
        var settings = new UpdateSettings("github-releases", null);

        var result = await service.CheckAsync("v0.2.0", settings, includePrerelease: true);

        Assert.True(result.HasUpdate);
        Assert.Equal("v0.3.0-rc.1", result.LatestVersion);
    }

    [Fact]
    public async Task CheckAsyncThrowsForEmptyCurrentVersion()
    {
        var service = new UpdateCheckService((_, _) => Task.FromResult(ReleasesJson));
        var settings = new UpdateSettings("github-releases", null);

        await Assert.ThrowsAsync<ArgumentException>(() => service.CheckAsync("  ", settings));
    }

    [Fact]
    public async Task CheckAsyncPropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // 获取委托遵循取消令牌。
        var service = new UpdateCheckService((_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ReleasesJson);
        });
        var settings = new UpdateSettings("github-releases", null);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CheckAsync("v0.1.0", settings, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task CheckAsyncWorksWithReleaseFeedClientOverload()
    {
        var settings = new UpdateSettings("github-releases", null);
        var service = new UpdateCheckService(new StubClient(ReleasesJson));

        var result = await service.CheckAsync("v0.1.0", settings);

        Assert.True(result.HasUpdate);
        Assert.Equal("v0.2.0", result.LatestVersion);
    }

    // 简单的 IReleaseFeedClient 桩，固定返回给定 JSON。
    private sealed class StubClient : IReleaseFeedClient
    {
        private readonly string _json;

        public StubClient(string json) => _json = json;

        public Task<string> FetchAsync(string url, CancellationToken cancellationToken) =>
            Task.FromResult(_json);
    }
}
