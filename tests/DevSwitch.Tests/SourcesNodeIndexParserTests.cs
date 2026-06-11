// 文件用途：验证 NodeIndexParser 对 nodejs.org/dist/index.json 的公开解析行为。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit、DevSwitch.Core、DevSwitch.Sources
// NOTE: 合法授权学习使用，仅限本地环境。本测试只喂内嵌样例 JSON，不发任何真实网络请求。

using DevSwitch.Core;
using DevSwitch.Sources;
using Xunit;

namespace DevSwitch.Tests;

public sealed class SourcesNodeIndexParserTests
{
    // 精简但保留关键结构：version/date/files/lts。
    private const string SampleJson = """
    [
      { "version": "v22.11.0", "date": "2024-10-29", "files": ["win-x64-zip", "win-arm64-zip", "osx-x64-tar"], "lts": "Jod" },
      { "version": "v20.18.0", "date": "2024-10-03", "files": ["win-x64-zip"], "lts": "Iron" },
      { "version": "v23.1.0",  "date": "2024-10-24", "files": ["win-x64-zip", "win-arm64-zip"], "lts": false }
    ]
    """;

    [Fact]
    public void ParseExtractsWindowsX64ZipWithCorrectUrlAndArchitecture()
    {
        var versions = NodeIndexParser.Parse(SampleJson, SdkArchitecture.X64);

        var v22 = Assert.Single(versions, v => v.Version == "22.11.0");
        Assert.Equal(SdkType.Node, v22.SdkType);
        Assert.Equal("nodejs", v22.Distribution);
        Assert.Equal(SdkArchitecture.X64, v22.Architecture);
        Assert.Equal("https://nodejs.org/dist/v22.11.0/node-v22.11.0-win-x64.zip", v22.DownloadUrl);
        Assert.Equal("https://nodejs.org/dist/v22.11.0/SHASUMS256.txt", v22.ChecksumUrl);
    }

    [Fact]
    public void ParseFiltersByArm64Architecture()
    {
        var versions = NodeIndexParser.Parse(SampleJson, SdkArchitecture.Arm64);

        // 仅 22.11.0 与 23.1.0 提供 win-arm64-zip；20.18.0 只有 x64。
        Assert.All(versions, v => Assert.Equal(SdkArchitecture.Arm64, v.Architecture));
        Assert.Equal(2, versions.Count);
        Assert.Contains(versions, v => v.DownloadUrl == "https://nodejs.org/dist/v22.11.0/node-v22.11.0-win-arm64.zip");
    }

    [Fact]
    public void ParseAnyArchitectureReturnsBothX64AndArm64()
    {
        var versions = NodeIndexParser.Parse(SampleJson, SdkArchitecture.Any);

        // x64: 三个版本；arm64: 两个版本 -> 共 5 条。
        Assert.Equal(5, versions.Count);
    }

    [Fact]
    public void ParseAttachesLtsCodenameToDisplayName()
    {
        var versions = NodeIndexParser.Parse(SampleJson, SdkArchitecture.X64);

        var v22 = Assert.Single(versions, v => v.Version == "22.11.0");
        Assert.Contains("LTS Jod", v22.DisplayName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-json")]
    [InlineData("{}")]
    public void ParseReturnsEmptyForInvalidOrNonArrayInput(string input)
    {
        var versions = NodeIndexParser.Parse(input, SdkArchitecture.Any);

        Assert.Empty(versions);
    }

    [Fact]
    public void ParseSkipsMalformedEntriesWithoutThrowing()
    {
        // 混入缺 version、缺 files、类型错误的脏条目，应被跳过。
        const string dirty = """
        [
          { "version": "v18.20.4", "files": ["win-x64-zip"] },
          { "files": ["win-x64-zip"] },
          { "version": 123, "files": ["win-x64-zip"] },
          { "version": "v19.9.0" },
          "garbage",
          { "version": "v21.7.3", "files": "not-an-array" }
        ]
        """;

        var versions = NodeIndexParser.Parse(dirty, SdkArchitecture.X64);

        // 仅第一条合法。
        var only = Assert.Single(versions);
        Assert.Equal("18.20.4", only.Version);
    }
}
