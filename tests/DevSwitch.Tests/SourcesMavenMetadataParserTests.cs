// 文件用途：验证 MavenMetadataParser 对 maven-metadata.xml 的公开解析行为。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit、DevSwitch.Core、DevSwitch.Sources
// NOTE: 合法授权学习使用，仅限本地环境。本测试只喂内嵌样例 XML，不发任何真实网络请求。

using DevSwitch.Core;
using DevSwitch.Sources;
using Xunit;

namespace DevSwitch.Tests;

public sealed class SourcesMavenMetadataParserTests
{
    private const string SampleXml = """
    <?xml version="1.0" encoding="UTF-8"?>
    <metadata>
      <groupId>org.apache.maven</groupId>
      <artifactId>apache-maven</artifactId>
      <versioning>
        <latest>4.0.0-rc-1</latest>
        <release>3.9.9</release>
        <versions>
          <version>3.8.8</version>
          <version>3.9.9</version>
          <version>4.0.0-rc-1</version>
        </versions>
        <lastUpdated>20240901000000</lastUpdated>
      </versioning>
    </metadata>
    """;

    [Fact]
    public void ParseExtractsAllVersionsAsArchitectureAny()
    {
        var versions = MavenMetadataParser.Parse(SampleXml);

        Assert.Equal(3, versions.Count);
        Assert.All(versions, v =>
        {
            Assert.Equal(SdkType.Maven, v.SdkType);
            Assert.Equal("apache-maven", v.Distribution);
            Assert.Equal(SdkArchitecture.Any, v.Architecture);
        });
    }

    [Fact]
    public void ParseBuildsBinariesZipUrlPerMajorVersion()
    {
        var versions = MavenMetadataParser.Parse(SampleXml);

        var v399 = Assert.Single(versions, v => v.Version == "3.9.9");
        Assert.Equal(
            "https://archive.apache.org/dist/maven/maven-3/3.9.9/binaries/apache-maven-3.9.9-bin.zip",
            v399.DownloadUrl);
        Assert.EndsWith(".sha512", v399.ChecksumUrl);

        var v4 = Assert.Single(versions, v => v.Version == "4.0.0-rc-1");
        Assert.Contains("/maven-4/", v4.DownloadUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<broken")]
    public void ParseReturnsEmptyForInvalidXml(string input)
    {
        Assert.Empty(MavenMetadataParser.Parse(input));
    }

    [Fact]
    public void ParseReturnsEmptyWhenNoVersionElements()
    {
        const string empty = "<metadata><versioning><versions/></versioning></metadata>";

        Assert.Empty(MavenMetadataParser.Parse(empty));
    }
}
