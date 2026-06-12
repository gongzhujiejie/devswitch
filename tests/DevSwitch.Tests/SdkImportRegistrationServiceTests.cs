// 文件用途：验证本地 SDK 导入登记编排：结构识别、自动命令验证、版本/status 回写到 sdks.json。
// 创建/修改日期：2026-06-12
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit、DevSwitch.Core
// NOTE: 合法授权学习使用，仅限本地环境。测试只使用临时目录和 fake command runner。

using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class SdkImportRegistrationServiceTests
{
    [Fact]
    public async Task ImportAndVerifyAsyncPersistsNodeVersionResolvedByCommand()
    {
        var dataRoot = CreateTemporaryDirectory("data");
        var nodeRoot = CreateNodeRoot("nodejs");
        var runner = new FakeCommandRunner(new SdkCommandResult(
            Started: true,
            ExitCode: 0,
            StdOut: "v20.11.1\n",
            StdErr: string.Empty,
            TimedOut: false));
        var service = new SdkImportRegistrationService(
            dataRoot,
            new SdkCatalogStore(),
            new SdkImportVerificationService(runner));

        var result = await service.ImportAndVerifyAsync(nodeRoot);

        Assert.True(result.Success);
        Assert.NotNull(result.Record);
        Assert.Equal("20.11.1", result.Record.Version);
        Assert.Equal("Node.js 20.11.1", result.Record.Name);
        Assert.Equal(SdkRecordStatus.Usable, result.Record.Status);

        var catalog = await new SdkCatalogStore().LoadOrCreateAsync(dataRoot);
        var stored = Assert.Single(catalog.Items);
        Assert.Equal(result.Record, stored);
    }

    [Fact]
    public async Task ImportAndVerifyAsyncPersistsUnavailableWhenCommandFails()
    {
        var dataRoot = CreateTemporaryDirectory("data");
        var nodeRoot = CreateNodeRoot("nodejs");
        var runner = new FakeCommandRunner(new SdkCommandResult(
            Started: false,
            ExitCode: null,
            StdOut: string.Empty,
            StdErr: string.Empty,
            TimedOut: false));
        var service = new SdkImportRegistrationService(
            dataRoot,
            new SdkCatalogStore(),
            new SdkImportVerificationService(runner));

        var result = await service.ImportAndVerifyAsync(nodeRoot);

        Assert.True(result.Success);
        Assert.NotNull(result.Record);
        Assert.Equal(SdkVersionResolver.UnknownVersion, result.Record.Version);
        Assert.Equal(SdkRecordStatus.Unavailable, result.Record.Status);

        var catalog = await new SdkCatalogStore().LoadOrCreateAsync(dataRoot);
        var stored = Assert.Single(catalog.Items);
        Assert.Equal(SdkRecordStatus.Unavailable, stored.Status);
    }

    [Fact]
    public async Task ImportAndVerifyAsyncDoesNotPersistUnsupportedDirectory()
    {
        var dataRoot = CreateTemporaryDirectory("data");
        var unsupported = CreateTemporaryDirectory("empty");
        var runner = new FakeCommandRunner(new SdkCommandResult(true, 0, "v20.11.1", string.Empty, false));
        var service = new SdkImportRegistrationService(
            dataRoot,
            new SdkCatalogStore(),
            new SdkImportVerificationService(runner));

        var result = await service.ImportAndVerifyAsync(unsupported);

        Assert.False(result.Success);
        Assert.Null(result.Record);
        Assert.Equal(0, runner.InvocationCount);
        var catalog = await new SdkCatalogStore().LoadOrCreateAsync(dataRoot);
        Assert.Empty(catalog.Items);
    }

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

        public Task<SdkCommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            return Task.FromResult(result);
        }
    }
}
