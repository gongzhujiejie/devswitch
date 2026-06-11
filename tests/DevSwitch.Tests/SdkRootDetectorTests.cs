// 文件用途：验证 SDK 根目录识别的公开行为。
// 创建日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit
// NOTE: 合法授权学习使用，仅限本地环境。本测试只构造临时 SDK 目录，不访问真实系统 SDK。

using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class SdkRootDetectorTests
{
    [Fact]
    public void DetectReturnsUsableJavaJdkWhenRootContainsReleaseAndJavaCompilerExecutables()
    {
        // NOTE: 公开行为只关心用户导入的 SDK 根目录能否被识别为可用 JDK。
        // 这里创建最小 JDK 形态：release、bin/java.exe、bin/javac.exe，避免依赖本机真实 Java 安装。
        var jdkRoot = CreateTemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(jdkRoot, "bin"));
        File.WriteAllText(Path.Combine(jdkRoot, "release"), "JAVA_VERSION=\"21\"");
        File.WriteAllText(Path.Combine(jdkRoot, "bin", "java.exe"), string.Empty);
        File.WriteAllText(Path.Combine(jdkRoot, "bin", "javac.exe"), string.Empty);

        var result = SdkRootDetector.Detect(jdkRoot);

        Assert.Equal(SdkType.Java, result.Type);
        Assert.Equal(SdkStatus.Usable, result.Status);
        Assert.Equal(jdkRoot, result.RootPath);
    }

    [Fact]
    public void DetectReturnsUsableGoWhenRootContainsGoAndGofmtExecutables()
    {
        // NOTE: Go Windows 发行包的 SDK 根目录通过 bin/go.exe 和 bin/gofmt.exe 作为最小可用入口识别。
        // 这里只构造临时 Go 根目录，确保测试通过公开接口 Detect 验证公开行为。
        var goRoot = CreateTemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(goRoot, "bin"));
        File.WriteAllText(Path.Combine(goRoot, "bin", "go.exe"), string.Empty);
        File.WriteAllText(Path.Combine(goRoot, "bin", "gofmt.exe"), string.Empty);

        var result = SdkRootDetector.Detect(goRoot);

        Assert.Equal(SdkType.Go, result.Type);
        Assert.Equal(SdkStatus.Usable, result.Status);
        Assert.Equal(goRoot, result.RootPath);
    }

    [Fact]
    public void DetectReturnsUsableMavenWhenRootContainsMavenCommand()
    {
        // NOTE: Maven Windows 发行包的 SDK 根目录通过 bin/mvn.cmd 作为最小可用入口识别。
        // 这里只构造临时 Maven 根目录，确保测试通过公开接口 Detect 验证公开行为。
        var mavenRoot = CreateTemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(mavenRoot, "bin"));
        File.WriteAllText(Path.Combine(mavenRoot, "bin", "mvn.cmd"), string.Empty);

        var result = SdkRootDetector.Detect(mavenRoot);

        Assert.Equal(SdkType.Maven, result.Type);
        Assert.Equal(SdkStatus.Usable, result.Status);
        Assert.Equal(mavenRoot, result.RootPath);
    }

    [Fact]
    public void DetectReturnsUsableNodeWhenRootContainsNodeAndPackageManagerCommands()
    {
        // NOTE: Node.js Windows zip 解压后的 SDK 根目录直接包含 node.exe、npm.cmd、npx.cmd。
        // 这里只构造最小可用形态，确保测试通过公开接口 Detect 验证根目录识别行为。
        var nodeRoot = CreateTemporaryDirectory();
        File.WriteAllText(Path.Combine(nodeRoot, "node.exe"), string.Empty);
        File.WriteAllText(Path.Combine(nodeRoot, "npm.cmd"), string.Empty);
        File.WriteAllText(Path.Combine(nodeRoot, "npx.cmd"), string.Empty);

        var result = SdkRootDetector.Detect(nodeRoot);

        Assert.Equal(SdkType.Node, result.Type);
        Assert.Equal(SdkStatus.Usable, result.Status);
        Assert.Equal(nodeRoot, result.RootPath);
    }

    [Fact]
    public void DetectReturnsUnavailableWhenDirectoryDoesNotMatchSupportedSdkShape()
    {
        // NOTE: 识别失败属于普通导入校验结果，不应以异常中断调用方流程。
        // 空临时目录没有任何 SDK 标志文件，因此应返回不可用状态并保留用户输入路径。
        var unknownRoot = CreateTemporaryDirectory();

        var result = SdkRootDetector.Detect(unknownRoot);

        Assert.Equal(SdkType.Unknown, result.Type);
        Assert.Equal(SdkStatus.Unavailable, result.Status);
        Assert.Equal(unknownRoot, result.RootPath);
    }

    [Fact]
    public void DetectFallsBackToParentJavaRootWhenUserSelectsJdkBinDirectory()
    {
        // NOTE: 用户误选 JDK 的 bin 目录时，产品需要自动回退到父级 SDK 根目录。
        // 父目录仍按 release + bin/java.exe + bin/javac.exe 的最小 JDK 形态验证。
        var jdkRoot = CreateTemporaryDirectory();
        var binPath = Path.Combine(jdkRoot, "bin");
        Directory.CreateDirectory(binPath);
        File.WriteAllText(Path.Combine(jdkRoot, "release"), "JAVA_VERSION=\"21\"");
        File.WriteAllText(Path.Combine(binPath, "java.exe"), string.Empty);
        File.WriteAllText(Path.Combine(binPath, "javac.exe"), string.Empty);

        var result = SdkRootDetector.Detect(binPath);

        Assert.Equal(SdkType.Java, result.Type);
        Assert.Equal(SdkStatus.Usable, result.Status);
        Assert.Equal(jdkRoot, result.RootPath);
    }

    private static string CreateTemporaryDirectory()
    {
        // NOTE: 每个测试使用独立目录，避免测试间状态污染，便于 TDD 重复执行。
        var path = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
