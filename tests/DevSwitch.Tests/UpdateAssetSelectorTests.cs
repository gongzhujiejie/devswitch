// 文件用途：验证 UpdateAssetSelector 从 release 资产列表挑选 Windows x64 安装包及 sha256 校验资产的逻辑。
// 创建/修改日期：2026-06-10
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit、DevSwitch.Core
// NOTE: 合法授权学习使用，仅限本地环境。纯逻辑测试，无任何网络/磁盘副作用。

using System.Collections.Generic;
using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class UpdateAssetSelectorTests
{
    // 构造资产的小工具：下载地址用占位 URL，测试只关心选择逻辑。
    private static GitHubReleaseAsset Asset(string name) =>
        new(name, $"https://example.com/dl/{name}");

    [Fact]
    public void Select_EmptyList_ReturnsNull()
    {
        Assert.Null(UpdateAssetSelector.Select(new List<GitHubReleaseAsset>()));
    }

    [Fact]
    public void Select_NoZip_ReturnsNull()
    {
        // 没有任何 zip 包：无法选中。
        var assets = new List<GitHubReleaseAsset>
        {
            Asset("DevSwitch-win-x64.exe"),
            Asset("notes.txt"),
        };
        Assert.Null(UpdateAssetSelector.Select(assets));
    }

    [Fact]
    public void Select_NoWindowsPackage_ReturnsNull()
    {
        // 有 zip 但都不是 Windows 包：无法选中。
        var assets = new List<GitHubReleaseAsset>
        {
            Asset("DevSwitch-linux-x64.zip"),
            Asset("DevSwitch-osx-arm64.zip"),
        };
        Assert.Null(UpdateAssetSelector.Select(assets));
    }

    [Fact]
    public void Select_SingleWinX64Zip_NoChecksum()
    {
        // 仅一个 win-x64 zip：选中且无校验资产。
        var pkg = Asset("DevSwitch-win-x64.zip");
        var result = UpdateAssetSelector.Select(new List<GitHubReleaseAsset> { pkg });

        Assert.NotNull(result);
        Assert.Equal(pkg, result!.Package);
        Assert.Null(result.Checksum);
    }

    [Fact]
    public void Select_WithSha256Asset_MatchesChecksum_FullName()
    {
        // 校验资产名 = 包名 + ".sha256"。
        var pkg = Asset("DevSwitch-win-x64.zip");
        var sum = Asset("DevSwitch-win-x64.zip.sha256");
        var result = UpdateAssetSelector.Select(new List<GitHubReleaseAsset> { pkg, sum });

        Assert.NotNull(result);
        Assert.Equal(pkg, result!.Package);
        Assert.Equal(sum, result.Checksum);
    }

    [Fact]
    public void Select_WithSha256Asset_MatchesChecksum_StemName()
    {
        // 校验资产名 = 去扩展名包名 + ".sha256"。
        var pkg = Asset("DevSwitch-win-x64.zip");
        var sum = Asset("DevSwitch-win-x64.sha256");
        var result = UpdateAssetSelector.Select(new List<GitHubReleaseAsset> { pkg, sum });

        Assert.NotNull(result);
        Assert.Equal(sum, result!.Checksum);
    }

    [Fact]
    public void Select_PrefersX64_OverArm64()
    {
        // 同时存在 win-x64 与 win-arm64：应优先 x64。
        var arm = Asset("DevSwitch-win-arm64.zip");
        var x64 = Asset("DevSwitch-win-x64.zip");
        var result = UpdateAssetSelector.Select(new List<GitHubReleaseAsset> { arm, x64 });

        Assert.NotNull(result);
        Assert.Equal(x64, result!.Package);
    }

    [Fact]
    public void Select_PrefersX64_OverPlainWin()
    {
        // 同时含 win+x64 与仅含 win：匹配度高者（x64）胜出。
        var plain = Asset("DevSwitch-windows.zip");
        var x64 = Asset("DevSwitch-windows-x64.zip");
        var result = UpdateAssetSelector.Select(new List<GitHubReleaseAsset> { plain, x64 });

        Assert.NotNull(result);
        Assert.Equal(x64, result!.Package);
    }

    [Fact]
    public void Select_MixedCaseNames_StillMatches()
    {
        // 大小写混合名也应命中（包与校验均不区分大小写）。
        var pkg = Asset("DevSwitch-Win10-X64.ZIP");
        var sum = Asset("DevSwitch-Win10-X64.ZIP.SHA256");
        var result = UpdateAssetSelector.Select(new List<GitHubReleaseAsset> { pkg, sum });

        Assert.NotNull(result);
        Assert.Equal(pkg, result!.Package);
        Assert.Equal(sum, result.Checksum);
    }
}
