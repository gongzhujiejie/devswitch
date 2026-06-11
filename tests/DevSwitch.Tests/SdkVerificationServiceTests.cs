// 文件用途：验证 SdkVerificationService 的公开行为——版本解析、状态推导、轻量验证、命令验证。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit、DevSwitch.Core
// NOTE: 合法授权学习使用，仅限本地环境。测试通过 fake 抽象与临时目录验证公开行为，不真实运行外部命令。

using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class SdkVerificationServiceTests
{
    // === 版本解析（纯函数，最重点可测）===

    [Fact]
    public void ParseVersionExtractsJavaVersionFromStdErr()
    {
        // java -version 把版本信息写到 stderr。
        const string stderr = "openjdk version \"21.0.2\" 2024-01-16\nOpenJDK Runtime Environment";

        var version = SdkVerificationService.ParseVersion(SdkType.Java, stdout: string.Empty, stderr: stderr);

        Assert.Equal("21.0.2", version);
    }

    [Fact]
    public void ParseVersionExtractsLegacyJavaVersion()
    {
        const string stderr = "java version \"1.8.0_392\"";

        var version = SdkVerificationService.ParseVersion(SdkType.Java, string.Empty, stderr);

        Assert.Equal("1.8.0_392", version);
    }

    [Fact]
    public void ParseVersionExtractsMavenVersionFromStdOut()
    {
        const string stdout = "Apache Maven 3.9.6 (bc0240f3c744dd6b6ec2920b3cd08dcc295161ae)";

        var version = SdkVerificationService.ParseVersion(SdkType.Maven, stdout, string.Empty);

        Assert.Equal("3.9.6", version);
    }

    [Fact]
    public void ParseVersionExtractsNodeVersionWithoutVPrefix()
    {
        var version = SdkVerificationService.ParseVersion(SdkType.Node, "v20.11.1\n", string.Empty);

        Assert.Equal("20.11.1", version);
    }

    [Fact]
    public void ParseVersionExtractsGoVersion()
    {
        const string stdout = "go version go1.22.0 windows/amd64";

        var version = SdkVerificationService.ParseVersion(SdkType.Go, stdout, string.Empty);

        Assert.Equal("1.22.0", version);
    }

    [Fact]
    public void ParseVersionReturnsNullForUnparseableOutput()
    {
        var version = SdkVerificationService.ParseVersion(SdkType.Node, "not a version", string.Empty);

        Assert.Null(version);
    }

    // === 状态推导（纯函数）===

    [Theory]
    [InlineData(false, false, false, SdkRecordStatus.Unavailable)] // 路径不存在
    [InlineData(true, false, false, SdkRecordStatus.Unavailable)]  // 关键文件缺失
    [InlineData(true, true, false, SdkRecordStatus.Usable)]        // 结构完整、非 current
    [InlineData(true, true, true, SdkRecordStatus.Active)]         // 结构完整且 current
    public void DeriveStatusCoversAllBranches(bool pathExists, bool keyFilesComplete, bool isCurrent, SdkRecordStatus expected)
    {
        Assert.Equal(expected, SdkVerificationService.DeriveStatus(pathExists, keyFilesComplete, isCurrent));
    }

    // === 轻量验证（fake inspector + 临时目录关键文件）===

    [Fact]
    public async Task LightweightVerifyReportsActiveWhenCurrentAndKeyFilesPresent()
    {
        using var temp = new TempJavaHome(includeKeyFiles: true);
        var record = NewJavaRecord(temp.Root);
        // current 指向该记录路径 -> 使用中。
        var service = new SdkVerificationService(
            new FakeLinkInspector(new CurrentLinkInfo(true, temp.Root)),
            new FakeCommandRunner());

        var result = await service.LightweightVerifyAsync(record);

        Assert.Equal(SdkRecordStatus.Active, result.Status);
        Assert.True(result.IsCurrent);
        Assert.Empty(result.MissingKeyFiles);
    }

    [Fact]
    public async Task LightweightVerifyReportsUnavailableWhenKeyFilesMissing()
    {
        using var temp = new TempJavaHome(includeKeyFiles: false);
        var record = NewJavaRecord(temp.Root);
        var service = new SdkVerificationService(
            new FakeLinkInspector(CurrentLinkInfo.Missing),
            new FakeCommandRunner());

        var result = await service.LightweightVerifyAsync(record);

        Assert.Equal(SdkRecordStatus.Unavailable, result.Status);
        Assert.False(result.IsCurrent);
        Assert.NotEmpty(result.MissingKeyFiles);
    }

    [Fact]
    public async Task LightweightVerifyDoesNotRunCommands()
    {
        using var temp = new TempJavaHome(includeKeyFiles: true);
        var record = NewJavaRecord(temp.Root);
        var runner = new FakeCommandRunner();
        var service = new SdkVerificationService(new FakeLinkInspector(CurrentLinkInfo.Missing), runner);

        await service.LightweightVerifyAsync(record);

        // 轻量验证绝不运行外部命令。
        Assert.Equal(0, runner.InvocationCount);
    }

    // === 命令绝对路径解析（纯函数，修复核心）===

    [Fact]
    public void ResolveVersionCommandReturnsAbsoluteJavaExecutable()
    {
        // Java 命令验证必须跑记录自身 bin\java.exe，而非系统 PATH 的 java。
        var (fileName, args) = SdkVerificationService.ResolveVersionCommand(SdkType.Java, "C:\\jdks\\jdk-11");

        Assert.Equal(Path.Combine("C:\\jdks\\jdk-11", "bin", "java.exe"), fileName);
        Assert.Equal(new[] { "-version" }, args);
    }

    [Fact]
    public void ResolveVersionCommandReturnsAbsoluteMavenExecutable()
    {
        // Maven 跑记录目录的 mvn.cmd，不依赖系统 PATH。
        var (fileName, args) = SdkVerificationService.ResolveVersionCommand(SdkType.Maven, "C:\\tools\\maven");

        Assert.Equal(Path.Combine("C:\\tools\\maven", "bin", "mvn.cmd"), fileName);
        Assert.Equal(new[] { "-v" }, args);
    }

    [Fact]
    public void ResolveVersionCommandReturnsAbsoluteNodeExecutable()
    {
        // Node Windows zip 根目录直接有 node.exe。
        var (fileName, args) = SdkVerificationService.ResolveVersionCommand(SdkType.Node, "C:\\tools\\node-v20");

        Assert.Equal(Path.Combine("C:\\tools\\node-v20", "node.exe"), fileName);
        Assert.Equal(new[] { "-v" }, args);
    }

    [Fact]
    public void ResolveVersionCommandReturnsAbsoluteGoExecutable()
    {
        var (fileName, args) = SdkVerificationService.ResolveVersionCommand(SdkType.Go, "C:\\tools\\go");

        Assert.Equal(Path.Combine("C:\\tools\\go", "bin", "go.exe"), fileName);
        Assert.Equal(new[] { "version" }, args);
    }

    // === 命令验证（fake runner）===

    [Fact]
    public async Task RunCommandVerificationUsesRecordPathAbsoluteExecutable()
    {
        // 修复核心证明：验证某 JDK 记录时，传给 runner 的 fileName 必须是该记录路径下的
        // 绝对 java.exe，而非裸 "java"（否则永远解析系统 PATH 的 java，三个 JDK 报同一版本）。
        const string root = "C:\\jdks\\jdk-11";
        var record = NewJavaRecord(root);
        var runner = new FakeCommandRunner(new SdkCommandResult(
            Started: true, ExitCode: 0, StdOut: string.Empty,
            StdErr: "openjdk version \"11.0.22\" 2024-01-16", TimedOut: false));
        var service = new SdkVerificationService(new FakeLinkInspector(CurrentLinkInfo.Missing), runner);

        await service.RunCommandVerificationAsync(record);

        Assert.NotNull(runner.LastFileName);
        Assert.StartsWith(root, runner.LastFileName!);
        Assert.EndsWith("java.exe", runner.LastFileName!);
        Assert.NotEqual("java", runner.LastFileName);
        Assert.Equal(Path.Combine(root, "bin", "java.exe"), runner.LastFileName);
    }

    [Fact]
    public async Task RunCommandVerificationUsesRecordPathForMaven()
    {
        // Maven 同理：必须跑记录目录下的 mvn.cmd，不依赖系统 PATH。
        const string root = "C:\\tools\\maven";
        var record = NewMavenRecord(root);
        var runner = new FakeCommandRunner(new SdkCommandResult(
            Started: true, ExitCode: 0,
            StdOut: "Apache Maven 3.9.6 (bc0240f3c744dd6b6ec2920b3cd08dcc295161ae)",
            StdErr: string.Empty, TimedOut: false));
        var service = new SdkVerificationService(new FakeLinkInspector(CurrentLinkInfo.Missing), runner);

        await service.RunCommandVerificationAsync(record);

        Assert.Equal(Path.Combine(root, "bin", "mvn.cmd"), runner.LastFileName);
        Assert.NotEqual("mvn", runner.LastFileName);
    }

    [Fact]
    public async Task RunCommandVerificationReturnsVerifiedWithParsedVersion()
    {
        var record = NewJavaRecord("C:\\does\\not\\matter");
        var runner = new FakeCommandRunner(new SdkCommandResult(
            Started: true, ExitCode: 0, StdOut: string.Empty,
            StdErr: "openjdk version \"21.0.2\" 2024-01-16", TimedOut: false));
        var service = new SdkVerificationService(new FakeLinkInspector(CurrentLinkInfo.Missing), runner);

        var result = await service.RunCommandVerificationAsync(record);

        Assert.Equal(CommandVerificationOutcome.Verified, result.Outcome);
        Assert.Equal("21.0.2", result.ParsedVersion);
    }

    [Fact]
    public async Task RunCommandVerificationReportsNotStarted()
    {
        var record = NewJavaRecord("C:\\x");
        var runner = new FakeCommandRunner(new SdkCommandResult(false, null, string.Empty, string.Empty, false));
        var service = new SdkVerificationService(new FakeLinkInspector(CurrentLinkInfo.Missing), runner);

        var result = await service.RunCommandVerificationAsync(record);

        Assert.Equal(CommandVerificationOutcome.NotStarted, result.Outcome);
    }

    [Fact]
    public async Task RunCommandVerificationReportsTimeout()
    {
        var record = NewJavaRecord("C:\\x");
        var runner = new FakeCommandRunner(new SdkCommandResult(true, null, string.Empty, string.Empty, TimedOut: true));
        var service = new SdkVerificationService(new FakeLinkInspector(CurrentLinkInfo.Missing), runner);

        var result = await service.RunCommandVerificationAsync(record);

        Assert.Equal(CommandVerificationOutcome.TimedOut, result.Outcome);
    }

    [Fact]
    public async Task RunCommandVerificationReportsNonZeroExit()
    {
        var record = NewJavaRecord("C:\\x");
        var runner = new FakeCommandRunner(new SdkCommandResult(true, 1, string.Empty, "boom", false));
        var service = new SdkVerificationService(new FakeLinkInspector(CurrentLinkInfo.Missing), runner);

        var result = await service.RunCommandVerificationAsync(record);

        Assert.Equal(CommandVerificationOutcome.NonZeroExit, result.Outcome);
    }

    // === 测试辅助 ===

    private static SdkRecord NewJavaRecord(string path) => new(
        Id: "java-test",
        Type: SdkType.Java,
        Name: "Temurin 21",
        Version: "21.0.2",
        Distribution: "temurin",
        Architecture: SdkArchitecture.X64,
        Source: SdkSourceKind.External,
        Path: path,
        Status: SdkRecordStatus.Unverified,
        CreatedAt: DateTimeOffset.UnixEpoch,
        LastVerifiedAt: null);

    private static SdkRecord NewMavenRecord(string path) => new(
        Id: "maven-test",
        Type: SdkType.Maven,
        Name: "Apache Maven 3.9.6",
        Version: "3.9.6",
        Distribution: "apache",
        Architecture: SdkArchitecture.X64,
        Source: SdkSourceKind.External,
        Path: path,
        Status: SdkRecordStatus.Unverified,
        CreatedAt: DateTimeOffset.UnixEpoch,
        LastVerifiedAt: null);

    /// <summary>构造一个临时 JDK 目录，可选写入关键命令文件。</summary>
    private sealed class TempJavaHome : IDisposable
    {
        public string Root { get; }

        public TempJavaHome(bool includeKeyFiles)
        {
            Root = Path.Combine(Path.GetTempPath(), "devswitch-verify-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "bin"));
            if (includeKeyFiles)
            {
                File.WriteAllText(Path.Combine(Root, "release"), "JAVA_VERSION=\"21.0.2\"");
                File.WriteAllText(Path.Combine(Root, "bin", "java.exe"), string.Empty);
                File.WriteAllText(Path.Combine(Root, "bin", "javac.exe"), string.Empty);
            }
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch { /* 测试清理失败不影响断言。 */ }
        }
    }

    /// <summary>固定返回指定 current 链接信息的 fake。</summary>
    private sealed class FakeLinkInspector(CurrentLinkInfo info) : ISdkLinkInspector
    {
        public Task<CurrentLinkInfo> InspectAsync(SdkType sdkType, CancellationToken cancellationToken = default)
            => Task.FromResult(info);
    }

    /// <summary>记录调用次数并返回固定结果的 fake 命令执行器。</summary>
    private sealed class FakeCommandRunner(SdkCommandResult? result = null) : ISdkCommandRunner
    {
        public int InvocationCount { get; private set; }

        // 捕获最近一次传入的可执行文件名，用于断言验证使用的是记录绝对路径而非裸命令名。
        public string? LastFileName { get; private set; }

        public Task<SdkCommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            LastFileName = fileName;
            return Task.FromResult(result ?? new SdkCommandResult(true, 0, string.Empty, string.Empty, false));
        }
    }
}
