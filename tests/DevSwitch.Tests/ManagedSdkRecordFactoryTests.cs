// 文件用途：验证下载完成后托管 SDK 登记记录的结构校验与自动命令验证结果应用。
// 创建/修改日期：2026-06-12
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit、DevSwitch.Core
// NOTE: 合法授权学习使用，仅限本地环境。只创建临时 SDK 目录，不联网、不下载。

using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class ManagedSdkRecordFactoryTests
{
    [Fact]
    public async Task CreateVerifiedRecordAsyncUsesParsedVersionWhenCommandVerificationSucceeds()
    {
        var nodeRoot = CreateNodeRoot("node-v26.0.0-win-x64");
        var runner = new FakeCommandRunner(new SdkCommandResult(true, 0, "v26.1.0\n", string.Empty, false));
        var verifier = new SdkImportVerificationService(runner);

        var record = await ManagedSdkRecordFactory.CreateVerifiedRecordAsync(
            sdkType: SdkType.Node,
            declaredVersion: "26.0.0",
            distribution: "nodejs",
            architecture: SdkArchitecture.X64,
            installDirectory: nodeRoot,
            verifier: verifier);

        Assert.Equal("26.1.0", record.Version);
        Assert.Equal("nodejs 26.1.0 (x64)", record.Name);
        Assert.Equal(SdkSourceKind.Managed, record.Source);
        Assert.Equal(SdkRecordStatus.Usable, record.Status);
        Assert.Equal(nodeRoot, record.Path);
    }

    [Fact]
    public async Task CreateVerifiedRecordAsyncRegistersUnavailableWhenStructureOkButCommandFails()
    {
        var nodeRoot = CreateNodeRoot("node-v26.0.0-win-x64");
        var runner = new FakeCommandRunner(new SdkCommandResult(false, null, string.Empty, string.Empty, false));
        var verifier = new SdkImportVerificationService(runner);

        var record = await ManagedSdkRecordFactory.CreateVerifiedRecordAsync(
            SdkType.Node,
            "26.0.0",
            "nodejs",
            SdkArchitecture.X64,
            nodeRoot,
            verifier);

        Assert.Equal("26.0.0", record.Version);
        Assert.Equal(SdkRecordStatus.Unavailable, record.Status);
        Assert.NotNull(record.LastVerifiedAt);
    }

    [Fact]
    public async Task CreateVerifiedRecordAsyncRejectsMismatchedSdkTypeBeforeCatalogPollution()
    {
        var nodeRoot = CreateNodeRoot("node-v26.0.0-win-x64");
        var runner = new FakeCommandRunner(new SdkCommandResult(true, 0, "v26.0.0", string.Empty, false));
        var verifier = new SdkImportVerificationService(runner);

        await Assert.ThrowsAsync<InvalidDataException>(() => ManagedSdkRecordFactory.CreateVerifiedRecordAsync(
            SdkType.Java,
            "21.0.2",
            "temurin",
            SdkArchitecture.X64,
            nodeRoot,
            verifier));
    }

    private static string CreateNodeRoot(string leafName)
    {
        var parent = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, leafName);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "node.exe"), string.Empty);
        File.WriteAllText(Path.Combine(root, "npm.cmd"), string.Empty);
        File.WriteAllText(Path.Combine(root, "npx.cmd"), string.Empty);
        return root;
    }

    private sealed class FakeCommandRunner : ISdkCommandRunner
    {
        private readonly SdkCommandResult result;

        public FakeCommandRunner(SdkCommandResult result)
        {
            this.result = result;
        }

        public Task<SdkCommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }
}
