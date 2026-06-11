// 文件用途：验证 SdkVersionResolver 的纯文件/目录名版本解析逻辑。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit
// NOTE: 合法授权学习使用，仅限本地环境。核心断言喂入字符串/临时目录，不访问真实系统 SDK。

using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class SdkVersionResolverTests
{
    // ---- release 文本解析（纯函数，不依赖磁盘）----

    [Theory]
    // corretto 样例：JAVA_VERSION 带引号，需取引号内的完整版本（含 _ 修订号）。
    [InlineData("JAVA_VERSION=\"1.8.0_442\"\nOS_NAME=\"Windows\"", "1.8.0_442")]
    // 引号内为主版本号。
    [InlineData("JAVA_VERSION=\"17.0.11\"", "17.0.11")]
    // 无引号也应能解析（现有导入测试写入的就是无引号形态）。
    [InlineData("JAVA_VERSION=21", "21")]
    // 字段不在首行，需逐行匹配。
    [InlineData("OS_ARCH=\"amd64\"\r\nJAVA_VERSION=\"11.0.24\"\r\n", "11.0.24")]
    public void ParseJavaReleaseVersionExtractsQuotedAndUnquotedValues(string content, string expected)
    {
        Assert.Equal(expected, SdkVersionResolver.ParseJavaReleaseVersion(content));
    }

    [Theory]
    [InlineData("")]
    [InlineData("OS_NAME=\"Windows\"\nLIBC=\"\"")]
    [InlineData("JAVA_VERSION=\"\"")]
    public void ParseJavaReleaseVersionReturnsNullWhenFieldMissingOrEmpty(string content)
    {
        Assert.Null(SdkVersionResolver.ParseJavaReleaseVersion(content));
    }

    // ---- 目录名解析（纯函数）----

    [Theory]
    [InlineData("jdk-17.0.1", "17.0.1")]
    [InlineData("jdk-11.0.24", "11.0.24")]
    [InlineData("jdk-21.0.1", "21.0.1")]
    // 优先取 jdk 后的版本，而非前缀 zulu 的内部编号。
    [InlineData("zulu17.50.19-ca-jdk17.0.11", "17.0.11")]
    [InlineData("zulu11.72.19-ca-jdk11.0.23", "11.0.23")]
    // 无 jdk 子串时回退到完整版本 token，含 _ 修订号。
    [InlineData("corretto-1.8.0_442", "1.8.0_442")]
    public void ParseDirectoryNameVersionForJava(string dirName, string expected)
    {
        Assert.Equal(expected, SdkVersionResolver.ParseVersionFromDirectoryName(SdkType.Java, dirName));
    }

    [Theory]
    [InlineData("apache-maven-3.9.16", "3.9.16")]
    [InlineData("apache-maven-3.9.9", "3.9.9")]
    [InlineData("apache-maven-3.9.4", "3.9.4")]
    public void ParseDirectoryNameVersionForMaven(string dirName, string expected)
    {
        Assert.Equal(expected, SdkVersionResolver.ParseVersionFromDirectoryName(SdkType.Maven, dirName));
    }

    [Theory]
    [InlineData("node-v22.11.0-win-x64", "22.11.0")]
    [InlineData("node-v20.18.1-win-x64", "20.18.1")]
    public void ParseDirectoryNameVersionForNode(string dirName, string expected)
    {
        Assert.Equal(expected, SdkVersionResolver.ParseVersionFromDirectoryName(SdkType.Node, dirName));
    }

    [Theory]
    [InlineData("go1.23.0", "1.23.0")]
    [InlineData("go", null)]
    public void ParseDirectoryNameVersionForGo(string dirName, string? expected)
    {
        Assert.Equal(expected, SdkVersionResolver.ParseVersionFromDirectoryName(SdkType.Go, dirName));
    }

    [Theory]
    [InlineData("random-folder")]
    [InlineData("")]
    public void ParseDirectoryNameVersionReturnsNullWhenNoVersionPresent(string dirName)
    {
        Assert.Null(SdkVersionResolver.ParseVersionFromDirectoryName(SdkType.Java, dirName));
    }

    // ---- Go VERSION 文件解析 ----

    [Theory]
    [InlineData("go1.23.0\ntime 2024-08-07T19:21:44Z", "1.23.0")]
    [InlineData("go1.21.5", "1.21.5")]
    public void ParseGoVersionStripsGoPrefix(string content, string expected)
    {
        Assert.Equal(expected, SdkVersionResolver.ParseGoVersion(content));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n")]
    public void ParseGoVersionReturnsNullWhenEmpty(string content)
    {
        Assert.Null(SdkVersionResolver.ParseGoVersion(content));
    }

    // ---- ResolveVersion 集成（临时目录）----

    [Fact]
    public void ResolveVersionReadsJavaReleaseFile()
    {
        var root = CreateTempDir("jdk-irrelevant");
        File.WriteAllText(Path.Combine(root, "release"), "JAVA_VERSION=\"1.8.0_442\"\nOS_NAME=\"Windows\"");

        Assert.Equal("1.8.0_442", SdkVersionResolver.ResolveVersion(SdkType.Java, root));
    }

    [Fact]
    public void ResolveVersionFallsBackToDirectoryNameWhenReleaseEmpty()
    {
        // NOTE: 真实样本 jdk-17.0.1 的 release 文件为空，必须回退目录名解析。
        var root = CreateTempDir("jdk-17.0.1");
        File.WriteAllText(Path.Combine(root, "release"), string.Empty);

        Assert.Equal("17.0.1", SdkVersionResolver.ResolveVersion(SdkType.Java, root));
    }

    [Fact]
    public void ResolveVersionFallsBackToDirectoryNameWhenReleaseMissing()
    {
        var root = CreateTempDir("zulu17.50.19-ca-jdk17.0.11");

        Assert.Equal("17.0.11", SdkVersionResolver.ResolveVersion(SdkType.Java, root));
    }

    [Fact]
    public void ResolveVersionReadsGoVersionFile()
    {
        var root = CreateTempDir("go");
        File.WriteAllText(Path.Combine(root, "VERSION"), "go1.23.0\ntime 2024-08-07T19:21:44Z");

        Assert.Equal("1.23.0", SdkVersionResolver.ResolveVersion(SdkType.Go, root));
    }

    [Fact]
    public void ResolveVersionResolvesMavenFromDirectoryName()
    {
        var root = CreateTempDir("apache-maven-3.9.16");

        Assert.Equal("3.9.16", SdkVersionResolver.ResolveVersion(SdkType.Maven, root));
    }

    [Fact]
    public void ResolveVersionResolvesNodeFromDirectoryName()
    {
        var root = CreateTempDir("node-v22.11.0-win-x64");

        Assert.Equal("22.11.0", SdkVersionResolver.ResolveVersion(SdkType.Node, root));
    }

    [Fact]
    public void ResolveVersionReturnsUnknownWhenNothingResolvable()
    {
        var root = CreateTempDir("mystery");

        Assert.Equal("unknown", SdkVersionResolver.ResolveVersion(SdkType.Java, root));
    }

    [Fact]
    public void ResolveVersionReturnsUnknownForInvalidPathWithoutThrowing()
    {
        // NOTE: 解析器必须稳健：不存在的路径不抛异常，直接返回 unknown。
        var missing = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"), "nope");

        Assert.Equal("unknown", SdkVersionResolver.ResolveVersion(SdkType.Java, missing));
    }

    private static string CreateTempDir(string leafName)
    {
        var path = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"), leafName);
        Directory.CreateDirectory(path);
        return path;
    }
}
