// 文件用途：验证 OracleJdkSource 的 NFTC /latest/ 直链合成与存活探测组合行为。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit、DevSwitch.Core、DevSwitch.Sources
// NOTE: 合法授权学习使用，仅限本地环境。所有测试都用注入的假探活器，不发任何真实网络请求。

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DevSwitch.Core;
using DevSwitch.Sources;
using Xunit;

namespace DevSwitch.Tests;

public sealed class SourcesOracleJdkSourceTests
{
    /// <summary>
    /// 构造一个"全部存活"的探活器：任何 URL 都返回 true。
    /// </summary>
    private static Func<string, CancellationToken, Task<bool>> AllAlive()
        => (_, _) => Task.FromResult(true);

    /// <summary>
    /// 构造一个"全部失败"的探活器：任何 URL 都返回 false。
    /// </summary>
    private static Func<string, CancellationToken, Task<bool>> AllDead()
        => (_, _) => Task.FromResult(false);

    /// <summary>
    /// 构造一个"白名单存活"的探活器：仅 URL 中包含指定大版本号的请求返回 true。
    /// </summary>
    private static Func<string, CancellationToken, Task<bool>> AliveOnly(params int[] aliveFeatures)
    {
        var set = new HashSet<int>(aliveFeatures);
        return (url, _) =>
        {
            // URL 形如 https://download.oracle.com/java/{N}/latest/jdk-{N}_windows-x64_bin.zip
            // 简单从 "/java/{N}/" 段抽取大版本号即可。
            var marker = "/java/";
            var idx = url.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0)
            {
                return Task.FromResult(false);
            }

            var rest = url[(idx + marker.Length)..];
            var slash = rest.IndexOf('/');
            if (slash <= 0)
            {
                return Task.FromResult(false);
            }

            var token = rest[..slash];
            return Task.FromResult(int.TryParse(token, out var feature) && set.Contains(feature));
        };
    }

    [Fact]
    public async Task ListVersionsReturnsAllThreeWhenEveryFeatureIsAlive()
    {
        var source = new OracleJdkSource(AllAlive());

        var versions = await source.ListVersionsAsync(SdkArchitecture.X64, CancellationToken.None);

        // 默认白名单为 21/25/26，三个全活就应该有三条。
        Assert.Equal(3, versions.Count);

        // 降序：26 > 25 > 21（按首段数值比较）。
        Assert.Equal("26.latest", versions[0].Version);
        Assert.Equal("25.latest", versions[1].Version);
        Assert.Equal("21.latest", versions[2].Version);

        // URL pattern、distribution、SdkType 一致性检查。
        Assert.All(versions, v =>
        {
            Assert.Equal(SdkType.Java, v.SdkType);
            Assert.Equal("oracle-jdk", v.Distribution);
            Assert.Equal(SdkArchitecture.X64, v.Architecture);
            Assert.Null(v.Sha256);
            Assert.NotNull(v.ChecksumUrl);
            Assert.EndsWith(".sha256", v.ChecksumUrl);
        });

        // 抽 21 这一条做精确 URL 校验。
        var v21 = versions.Single(v => v.Version == "21.latest");
        Assert.Equal(
            "https://download.oracle.com/java/21/latest/jdk-21_windows-x64_bin.zip",
            v21.DownloadUrl);
        Assert.Equal(
            "https://download.oracle.com/java/21/latest/jdk-21_windows-x64_bin.zip.sha256",
            v21.ChecksumUrl);
    }

    [Fact]
    public async Task ListVersionsReturnsEmptyWhenAllProbesFail()
    {
        var source = new OracleJdkSource(AllDead());

        var versions = await source.ListVersionsAsync(SdkArchitecture.X64, CancellationToken.None);

        // 全部探活失败：返回空列表，不抛异常。
        Assert.Empty(versions);
    }

    [Fact]
    public async Task ListVersionsReturnsOnlyAliveFeatures()
    {
        // 仅 25 存活，21、26 应被跳过。
        var source = new OracleJdkSource(AliveOnly(25));

        var versions = await source.ListVersionsAsync(SdkArchitecture.X64, CancellationToken.None);

        var v = Assert.Single(versions);
        Assert.Equal("25.latest", v.Version);
        Assert.Equal(
            "https://download.oracle.com/java/25/latest/jdk-25_windows-x64_bin.zip",
            v.DownloadUrl);
    }

    [Fact]
    public async Task ListVersionsReturnsEmptyForArm64RegardlessOfProbe()
    {
        // 即使探活器报 alive，arm64 也应返回空（Oracle NFTC 不提供 Windows arm64 包）。
        var probedUrls = new List<string>();
        Func<string, CancellationToken, Task<bool>> spy = (url, _) =>
        {
            probedUrls.Add(url);
            return Task.FromResult(true);
        };

        var source = new OracleJdkSource(spy);

        var versions = await source.ListVersionsAsync(SdkArchitecture.Arm64, CancellationToken.None);

        Assert.Empty(versions);
        // 短路特性：arm64 时不应触发任何探活请求，避免无谓 IO。
        Assert.Empty(probedUrls);
    }

    [Fact]
    public void SynthesizeVersionsBuildsExpectedUrlsAndOrdering()
    {
        var versions = OracleJdkSource.SynthesizeVersions(
            new[] { 21, 25, 26 },
            SdkArchitecture.X64);

        // URL pattern 与 distribution 校验。
        Assert.All(versions, v =>
        {
            Assert.Equal("oracle-jdk", v.Distribution);
            Assert.Equal(SdkType.Java, v.SdkType);
            Assert.Equal(SdkArchitecture.X64, v.Architecture);
            Assert.Matches(
                @"^https://download\.oracle\.com/java/\d+/latest/jdk-\d+_windows-x64_bin\.zip$",
                v.DownloadUrl);
        });

        // 版本号格式与降序检查。
        Assert.Equal(new[] { "26.latest", "25.latest", "21.latest" }, versions.Select(v => v.Version).ToArray());

        // 26 这一条做完整 URL 精确比对。
        var v26 = versions.First();
        Assert.Equal(
            "https://download.oracle.com/java/26/latest/jdk-26_windows-x64_bin.zip",
            v26.DownloadUrl);
    }

    [Fact]
    public void SynthesizeVersionsReturnsEmptyForNonX64()
    {
        // arm64 与 Any 都应该不出 Oracle 条目。
        Assert.Empty(OracleJdkSource.SynthesizeVersions(new[] { 21 }, SdkArchitecture.Arm64));
        Assert.Empty(OracleJdkSource.SynthesizeVersions(new[] { 21 }, SdkArchitecture.Any));
        Assert.Empty(OracleJdkSource.SynthesizeVersions(Array.Empty<int>(), SdkArchitecture.X64));
    }

    [Fact]
    public async Task CustomFeatureVersionsReplaceDefaults()
    {
        // 用自定义大版本号集合（仅 21）覆盖默认；无论 25/26 是否存活，都不应出现在结果里。
        var source = new OracleJdkSource(AllAlive(), new[] { 21 });

        var versions = await source.ListVersionsAsync(SdkArchitecture.X64, CancellationToken.None);

        var v = Assert.Single(versions);
        Assert.Equal("21.latest", v.Version);
    }
}
