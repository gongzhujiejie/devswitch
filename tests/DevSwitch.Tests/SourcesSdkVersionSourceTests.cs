// 文件用途：验证 HttpSdkVersionSource 与 SdkSourceCatalog 的离线聚合/排序行为。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit、DevSwitch.Core、DevSwitch.Sources
// NOTE: 合法授权学习使用，仅限本地环境。用假抓取委托注入内嵌样例，不发任何真实网络请求。

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DevSwitch.Core;
using DevSwitch.Sources;
using Xunit;

namespace DevSwitch.Tests;

public sealed class SourcesSdkVersionSourceTests
{
    // 乱序版本，验证源会按版本降序返回。
    private const string NodeJson = """
    [
      { "version": "v20.18.0", "files": ["win-x64-zip"] },
      { "version": "v22.11.0", "files": ["win-x64-zip"] },
      { "version": "v18.20.4", "files": ["win-x64-zip"] }
    ]
    """;

    private const string GoJson = """
    [
      { "version": "go1.22.5", "files": [ { "filename": "go1.22.5.windows-amd64.zip", "os": "windows", "arch": "amd64", "kind": "archive", "sha256": "a" } ] }
    ]
    """;

    /// <summary>
    /// 构造返回固定文本的假抓取委托。
    /// </summary>
    private static SourceTextFetcher StaticFetcher(string payload)
        => (_, _) => Task.FromResult(payload);

    [Fact]
    public async Task ListVersionsReturnsNewestFirst()
    {
        var source = HttpSdkVersionSource.CreateNode(StaticFetcher(NodeJson));

        var versions = await source.ListVersionsAsync(SdkArchitecture.X64);

        Assert.Equal(new[] { "22.11.0", "20.18.0", "18.20.4" }, versions.Select(v => v.Version).ToArray());
    }

    [Fact]
    public async Task ListVersionsPassesArchitectureToParser()
    {
        var source = HttpSdkVersionSource.CreateGo(StaticFetcher(GoJson));

        var versions = await source.ListVersionsAsync(SdkArchitecture.X64);

        var v = Assert.Single(versions);
        Assert.Equal(SdkType.Go, v.SdkType);
        Assert.Equal(SdkArchitecture.X64, v.Architecture);
    }

    [Fact]
    public async Task ListVersionsHonorsCancellationBeforeFetch()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // 抓取委托观察取消标记并抛出 OperationCanceledException。
        SourceTextFetcher cancelAware = (_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NodeJson);
        };
        var source = HttpSdkVersionSource.CreateNode(cancelAware);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => source.ListVersionsAsync(SdkArchitecture.X64, cts.Token));
    }

    [Fact]
    public async Task CreateTemurinFeatureBuildsOfficialEndpointWithFeatureAndPageSize()
    {
        // 捕获实际请求的端点，验证按 feature 版本与 pageSize 构造官方 api.adoptium.net 地址。
        string? requested = null;
        SourceTextFetcher capture = (url, _) =>
        {
            requested = url;
            return Task.FromResult("[]");
        };

        var source = HttpSdkVersionSource.CreateTemurinFeature(capture, featureVersion: 17, pageSize: 3);
        _ = await source.ListVersionsAsync(SdkArchitecture.X64);

        Assert.NotNull(requested);
        Assert.Contains("api.adoptium.net", requested!);
        Assert.Contains("/feature_releases/17/ga", requested!);
        Assert.Contains("page_size=3", requested!);
        Assert.Contains("image_type=jdk", requested!);
        Assert.Equal(SdkType.Java, source.SdkType);
    }

    [Fact]
    public void CreateTemurinFeatureRejectsNonPositiveFeatureVersion()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => HttpSdkVersionSource.CreateTemurinFeature(StaticFetcher("[]"), featureVersion: 0));
    }

    [Fact]
    public async Task RustupSourceBuildsOfficialWindowsUrlsForX64()
    {
        // NOTE: Rustup 官方 Windows 安装入口固定在 static.rust-lang.org/rustup/dist/{triple}/rustup-init.exe。
        var source = new RustupSource();

        var versions = await source.ListVersionsAsync(SdkArchitecture.X64);

        var version = Assert.Single(versions);
        Assert.Equal(SdkType.Rust, version.SdkType);
        Assert.Equal("rustup", version.Distribution);
        Assert.Equal("stable", version.Version);
        Assert.Equal(SdkArchitecture.X64, version.Architecture);
        Assert.Equal("https://static.rust-lang.org/rustup/dist/x86_64-pc-windows-msvc/rustup-init.exe", version.DownloadUrl);
        Assert.Equal("https://static.rust-lang.org/rustup/dist/x86_64-pc-windows-msvc/rustup-init.exe.sha256", version.ChecksumUrl);
        Assert.Contains("Rust stable", version.DisplayName);
    }

    [Fact]
    public async Task RustupSourceBuildsOfficialWindowsUrlsForArm64()
    {
        var source = new RustupSource();

        var versions = await source.ListVersionsAsync(SdkArchitecture.Arm64);

        var version = Assert.Single(versions);
        Assert.Equal(SdkArchitecture.Arm64, version.Architecture);
        Assert.Equal("https://static.rust-lang.org/rustup/dist/aarch64-pc-windows-msvc/rustup-init.exe", version.DownloadUrl);
        Assert.Equal("https://static.rust-lang.org/rustup/dist/aarch64-pc-windows-msvc/rustup-init.exe.sha256", version.ChecksumUrl);
    }

    [Fact]
    public async Task CatalogQueriesOnlyMatchingSdkType()
    {
        var node = HttpSdkVersionSource.CreateNode(StaticFetcher(NodeJson));
        var go = HttpSdkVersionSource.CreateGo(StaticFetcher(GoJson));
        var catalog = new SdkSourceCatalog(new ISdkVersionSource[] { node, go });

        var nodeVersions = await catalog.ListVersionsAsync(SdkType.Node, SdkArchitecture.X64);

        Assert.All(nodeVersions, v => Assert.Equal(SdkType.Node, v.SdkType));
        Assert.Equal(3, nodeVersions.Count);
    }

    [Fact]
    public async Task CatalogMergesAndDeduplicatesAcrossSources()
    {
        // 两个 Node 源返回部分重叠版本，聚合后应去重。
        var sourceA = HttpSdkVersionSource.CreateNode(StaticFetcher(NodeJson));
        var sourceB = HttpSdkVersionSource.CreateNode(StaticFetcher(
            """
            [
              { "version": "v22.11.0", "files": ["win-x64-zip"] },
              { "version": "v21.7.3",  "files": ["win-x64-zip"] }
            ]
            """));
        var catalog = new SdkSourceCatalog(new ISdkVersionSource[] { sourceA, sourceB });

        var versions = await catalog.ListVersionsAsync(SdkType.Node, SdkArchitecture.X64);

        // 并集 {22.11.0, 21.7.3, 20.18.0, 18.20.4}，22.11.0 去重一次 -> 4 条且降序。
        Assert.Equal(new[] { "22.11.0", "21.7.3", "20.18.0", "18.20.4" }, versions.Select(v => v.Version).ToArray());
    }

    [Fact]
    public async Task CatalogReturnsEmptyForUnregisteredSdkType()
    {
        var node = HttpSdkVersionSource.CreateNode(StaticFetcher(NodeJson));
        var catalog = new SdkSourceCatalog(new ISdkVersionSource[] { node });

        var mavenVersions = await catalog.ListVersionsAsync(SdkType.Maven, SdkArchitecture.Any);

        Assert.Empty(mavenVersions);
    }
}
