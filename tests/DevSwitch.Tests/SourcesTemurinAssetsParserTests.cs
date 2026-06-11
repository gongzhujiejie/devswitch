// 文件用途：验证 TemurinAssetsParser 对 Adoptium API v3 assets JSON 的公开解析行为。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit、DevSwitch.Core、DevSwitch.Sources
// NOTE: 合法授权学习使用，仅限本地环境。本测试只喂内嵌样例 JSON，不发任何真实网络请求。

using DevSwitch.Core;
using DevSwitch.Sources;
using Xunit;

namespace DevSwitch.Tests;

public sealed class SourcesTemurinAssetsParserTests
{
    // 精简样例：保留 version_data.semver、binaries[os/architecture/image_type/package]。
    private const string SampleJson = """
    [
      {
        "version_data": { "semver": "21.0.4+7" },
        "binaries": [
          {
            "os": "windows", "architecture": "x64", "image_type": "jdk",
            "package": { "name": "OpenJDK21U-jdk_x64_windows.zip", "link": "https://example.org/jdk21-x64.zip", "checksum": "deadbeef21" }
          },
          {
            "os": "windows", "architecture": "aarch64", "image_type": "jdk",
            "package": { "name": "OpenJDK21U-jdk_arm_windows.zip", "link": "https://example.org/jdk21-arm64.zip", "checksum": "deadbeef21arm" }
          },
          {
            "os": "windows", "architecture": "x64", "image_type": "jre",
            "package": { "name": "OpenJDK21U-jre.zip", "link": "https://example.org/jre21.zip", "checksum": "ignore" }
          },
          {
            "os": "linux", "architecture": "x64", "image_type": "jdk",
            "package": { "name": "linux.tar.gz", "link": "https://example.org/jdk21-linux.tar.gz", "checksum": "ignore" }
          }
        ]
      },
      {
        "version_data": { "semver": "17.0.12+7" },
        "binaries": [
          {
            "os": "windows", "architecture": "x64", "image_type": "jdk",
            "package": { "name": "OpenJDK17U-jdk_x64_windows.zip", "link": "https://example.org/jdk17-x64.zip", "checksum": "cafe17" }
          }
        ]
      }
    ]
    """;

    [Fact]
    public void ParseExtractsWindowsJdkX64WithSemverLinkAndChecksum()
    {
        var versions = TemurinAssetsParser.Parse(SampleJson, SdkArchitecture.X64);

        var v = Assert.Single(versions, x => x.Version == "21.0.4+7");
        Assert.Equal(SdkType.Java, v.SdkType);
        Assert.Equal("temurin", v.Distribution);
        Assert.Equal(SdkArchitecture.X64, v.Architecture);
        Assert.Equal("https://example.org/jdk21-x64.zip", v.DownloadUrl);
        Assert.Equal("deadbeef21", v.Sha256);
    }

    [Fact]
    public void ParseMapsAarch64ToArm64()
    {
        var versions = TemurinAssetsParser.Parse(SampleJson, SdkArchitecture.Arm64);

        var v = Assert.Single(versions);
        Assert.Equal("21.0.4+7", v.Version);
        Assert.Equal(SdkArchitecture.Arm64, v.Architecture);
    }

    [Fact]
    public void ParseIgnoresJreAndNonWindowsBinaries()
    {
        var versions = TemurinAssetsParser.Parse(SampleJson, SdkArchitecture.Any);

        // 合法：21 的 x64+arm64、17 的 x64 = 3；jre 与 linux 被忽略。
        Assert.Equal(3, versions.Count);
        Assert.DoesNotContain(versions, v => v.DownloadUrl.Contains("jre"));
        Assert.DoesNotContain(versions, v => v.DownloadUrl.Contains("linux"));
    }

    [Fact]
    public void ParseHonorsCustomDistribution()
    {
        var versions = TemurinAssetsParser.Parse(SampleJson, SdkArchitecture.X64, distribution: "corretto");

        Assert.All(versions, v => Assert.Equal("corretto", v.Distribution));
        Assert.Contains(versions, v => v.DisplayName!.StartsWith("Corretto"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{}")]
    public void ParseReturnsEmptyForInvalidInput(string input)
    {
        Assert.Empty(TemurinAssetsParser.Parse(input, SdkArchitecture.Any));
    }

    [Fact]
    public void ParseSkipsAssetsMissingSemverOrPackageLink()
    {
        const string dirty = """
        [
          { "binaries": [ { "os": "windows", "architecture": "x64", "image_type": "jdk", "package": { "link": "https://x/y.zip" } } ] },
          { "version_data": { "semver": "11.0.24+8" }, "binaries": [ { "os": "windows", "architecture": "x64", "image_type": "jdk", "package": {} } ] }
        ]
        """;

        // 第一条无 semver 跳过；第二条 package 无 link 跳过 -> 空。
        Assert.Empty(TemurinAssetsParser.Parse(dirty, SdkArchitecture.X64));
    }
}
