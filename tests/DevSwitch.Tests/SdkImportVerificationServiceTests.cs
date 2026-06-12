// 文件用途：验证导入/下载登记时的 SDK 自动验证服务。
// 创建/修改日期：2026-06-12
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit、DevSwitch.Core
// NOTE: 合法授权学习使用，仅限本地环境。测试用 fake command runner，不运行真实 SDK 命令。

using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class SdkImportVerificationServiceTests
{
    [Fact]
    public async Task VerifyAsyncUpdatesUnknownNodeVersionFromCommandOutput()
    {
        var nodeRoot = CreateNodeRoot("nodejs");
        var record = NewRecord(SdkType.Node, nodeRoot, version: SdkVersionResolver.UnknownVersion);
        var runner = new FakeCommandRunner(new SdkCommandResult(
            Started: true,
            ExitCode: 0,
            StdOut: "v20.11.1\n",
            StdErr: string.Empty,
            TimedOut: false));
        var service = new SdkImportVerificationService(runner);

        var result = await service.VerifyAsync(record);

        Assert.True(result.StructureOk);
        Assert.Empty(result.MissingKeyFiles);
        Assert.Equal(CommandVerificationOutcome.Verified, result.Command?.Outcome);
        Assert.Equal("20.11.1", result.Record.Version);
        Assert.Equal(SdkRecordStatus.Usable, result.Record.Status);
        Assert.NotNull(result.Record.LastVerifiedAt);
        Assert.Equal(Path.Combine(nodeRoot, "node.exe"), runner.LastFileName);
    }

    [Fact]
    public async Task VerifyAsyncMarksRecordUnavailableWhenCommandTimesOut()
    {
        var nodeRoot = CreateNodeRoot("nodejs");
        var record = NewRecord(SdkType.Node, nodeRoot, version: SdkVersionResolver.UnknownVersion);
        var runner = new FakeCommandRunner(new SdkCommandResult(
            Started: true,
            ExitCode: null,
            StdOut: string.Empty,
            StdErr: string.Empty,
            TimedOut: true));
        var service = new SdkImportVerificationService(runner);

        var result = await service.VerifyAsync(record);

        Assert.True(result.StructureOk);
        Assert.Equal(CommandVerificationOutcome.TimedOut, result.Command?.Outcome);
        Assert.Equal(SdkVersionResolver.UnknownVersion, result.Record.Version);
        Assert.Equal(SdkRecordStatus.Unavailable, result.Record.Status);
        Assert.NotNull(result.Record.LastVerifiedAt);
    }

    [Fact]
    public async Task VerifyAsyncDoesNotRunCommandWhenKeyFilesAreMissing()
    {
        var nodeRoot = CreateTemporaryDirectory("nodejs");
        await File.WriteAllTextAsync(Path.Combine(nodeRoot, "node.exe"), string.Empty);
        var record = NewRecord(SdkType.Node, nodeRoot, version: SdkVersionResolver.UnknownVersion);
        var runner = new FakeCommandRunner(new SdkCommandResult(true, 0, "v20.11.1", string.Empty, false));
        var service = new SdkImportVerificationService(runner);

        var result = await service.VerifyAsync(record);

        Assert.False(result.StructureOk);
        Assert.Contains("npm.cmd", result.MissingKeyFiles);
        Assert.Contains("npx.cmd", result.MissingKeyFiles);
        Assert.Equal(0, runner.InvocationCount);
        Assert.Equal(SdkRecordStatus.Unavailable, result.Record.Status);
        Assert.NotNull(result.Record.LastVerifiedAt);
    }

    private static SdkRecord NewRecord(SdkType type, string path, string version)
        => new(
            Id: $"{type.ToString().ToLowerInvariant()}-test",
            Type: type,
            Name: type == SdkType.Node ? "Node.js" : type.ToString(),
            Version: version,
            Distribution: "test",
            Architecture: SdkArchitecture.Unknown,
            Source: SdkSourceKind.External,
            Path: path,
            Status: SdkRecordStatus.Usable,
            CreatedAt: DateTimeOffset.UtcNow,
            LastVerifiedAt: null);

    private static string CreateNodeRoot(string leafName)
    {
        var root = CreateTemporaryDirectory(leafName);
        File.WriteAllText(Path.Combine(root, "node.exe"), string.Empty);
        File.WriteAllText(Path.Combine(root, "npm.cmd"), string.Empty);
        File.WriteAllText(Path.Combine(root, "npx.cmd"), string.Empty);
        return root;
    }

    private static string CreateTemporaryDirectory(string leafName)
    {
        var parent = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(parent, leafName);
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeCommandRunner : ISdkCommandRunner
    {
        private readonly SdkCommandResult result;

        public FakeCommandRunner(SdkCommandResult result)
        {
            this.result = result;
        }

        public int InvocationCount { get; private set; }

        public string? LastFileName { get; private set; }

        public Task<SdkCommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            LastFileName = fileName;
            return Task.FromResult(result);
        }
    }
}
