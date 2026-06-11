// 文件用途：验证 GoDownloadParser 对 go.dev/dl/?mode=json 的公开解析行为。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit、DevSwitch.Core、DevSwitch.Sources
// NOTE: 合法授权学习使用，仅限本地环境。本测试只喂内嵌样例 JSON，不发任何真实网络请求。

using DevSwitch.Core;
using DevSwitch.Sources;
using Xunit;

namespace DevSwitch.Tests;

public sealed class SourcesGoDownloadParserTests
{
    // 精简样例：含 windows/amd64/arm64 archive 与需被忽略的 installer/linux/386。
    private const string SampleJson = """
    [
      {
        "version": "go1.22.5",
        "stable": true,
        "files": [
          { "filename": "go1.22.5.windows-amd64.zip", "os": "windows", "arch": "amd64", "kind": "archive", "sha256": "aaaa1111" },
          { "filename": "go1.22.5.windows-arm64.zip", "os": "windows", "arch": "arm64", "kind": "archive", "sha256": "bbbb2222" },
          { "filename": "go1.22.5.windows-amd64.msi", "os": "windows", "arch": "amd64", "kind": "installer", "sha256": "cccc3333" },
          { "filename": "go1.22.5.linux-amd64.tar.gz", "os": "linux", "arch": "amd64", "kind": "archive", "sha256": "dddd4444" },
          { "filename": "go1.22.5.windows-386.zip", "os": "windows", "arch": "386", "kind": "archive", "sha256": "eeee5555" }
        ]
      },
      {
        "version": "go1.21.13",
        "stable": true,
        "files": [
          { "filename": "go1.21.13.windows-amd64.zip", "os": "windows", "arch": "amd64", "kind": "archive", "sha256": "ffff6666" }
        ]
      }
    ]
    """;

    [Fact]
    public void ParseExtractsWindowsAmd64ArchiveWithSha256AndUrl()
    {
        var versions = GoDownloadParser.Parse(SampleJson, SdkArchitecture.X64);

        var v = Assert.Single(versions, x => x.Version == "1.22.5");
        Assert.Equal(SdkType.Go, v.SdkType);
        Assert.Equal("go", v.Distribution);
        Assert.Equal(SdkArchitecture.X64, v.Architecture);
        Assert.Equal("https://go.dev/dl/go1.22.5.windows-amd64.zip", v.DownloadUrl);
        Assert.Equal("aaaa1111", v.Sha256);
    }

    [Fact]
    public void ParseMapsArm64Architecture()
    {
        var versions = GoDownloadParser.Parse(SampleJson, SdkArchitecture.Arm64);

        var v = Assert.Single(versions);
        Assert.Equal("1.22.5", v.Version);
        Assert.Equal(SdkArchitecture.Arm64, v.Architecture);
        Assert.Equal("bbbb2222", v.Sha256);
    }

    [Fact]
    public void ParseIgnoresInstallerLinuxAnd386()
    {
        var versions = GoDownloadParser.Parse(SampleJson, SdkArchitecture.Any);

        // 合法：1.22.5 amd64+arm64、1.21.13 amd64 = 3 条；其余被忽略。
        Assert.Equal(3, versions.Count);
        Assert.All(versions, v => Assert.Contains(".zip", v.DownloadUrl));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{}")]
    public void ParseReturnsEmptyForInvalidInput(string input)
    {
        Assert.Empty(GoDownloadParser.Parse(input, SdkArchitecture.Any));
    }

    [Fact]
    public void ParseSkipsEntriesMissingFilenameOrVersion()
    {
        const string dirty = """
        [
          { "files": [ { "os": "windows", "arch": "amd64", "kind": "archive" } ] },
          { "version": "go1.20.0", "files": [ { "os": "windows", "arch": "amd64", "kind": "archive" } ] }
        ]
        """;

        // 第一条无 version 跳过；第二条 file 无 filename 跳过 -> 空。
        Assert.Empty(GoDownloadParser.Parse(dirty, SdkArchitecture.X64));
    }
}
