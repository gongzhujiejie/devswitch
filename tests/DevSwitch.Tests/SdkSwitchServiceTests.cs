// 文件用途：验证 Core SdkSwitchService 的公开业务行为。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit、System.Text.Json
// NOTE: 合法授权学习使用，仅限本地环境。本测试用 fake helper，不创建真实系统链接。

using System.Text.Json;
using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class SdkSwitchServiceTests
{
    [Fact]
    public async Task SwitchBatchUpdatesActiveAndUsesSingleBatchCall()
    {
        var dataRoot = CreateTemporaryDirectory();
        var targetRoot = CreateUsableJavaRoot();
        var record = CreateRecord("java-21", SdkType.Java, targetRoot, SdkRecordStatus.Usable);
        await new SdkCatalogStore().SaveAsync(dataRoot, new SdkCatalog(1, ActiveSdkSet.Empty, new[] { record }));
        var helper = new FakeHelperClient();
        var service = CreateService(dataRoot, helper);

        var result = await service.SwitchBatchAsync(SdkType.Java, record.Id, broadcast: true);
        var catalog = await new SdkCatalogStore().LoadOrCreateAsync(dataRoot);

        Assert.True(result.Success);
        Assert.Equal(record.Id, catalog.Active.Java);
        // 批处理只调一次 helper（合并切换+广播），不再走单独 switch+inspect 多进程。
        Assert.Single(helper.BatchSwitchCalls);
        Assert.True(helper.BatchSwitchCalls[0].Broadcast);
        Assert.Equal(0, helper.SwitchCalls.Count);
    }

    [Fact]
    public async Task SwitchBatchReturnsFailureWhenHelperFails()
    {
        var dataRoot = CreateTemporaryDirectory();
        var targetRoot = CreateUsableJavaRoot();
        var record = CreateRecord("java-21", SdkType.Java, targetRoot, SdkRecordStatus.Usable);
        await new SdkCatalogStore().SaveAsync(dataRoot, new SdkCatalog(1, ActiveSdkSet.Empty, new[] { record }));
        var helper = new FakeHelperClient { SwitchResponse = CreateResponse(false, "create-temp-link-failed", "boom", "{}") };
        var service = CreateService(dataRoot, helper);

        var result = await service.SwitchBatchAsync(SdkType.Java, record.Id, broadcast: true);
        var catalog = await new SdkCatalogStore().LoadOrCreateAsync(dataRoot);

        Assert.False(result.Success);
        Assert.Equal("create-temp-link-failed", result.ErrorCode);
        Assert.Null(catalog.Active.Java);
    }

    [Fact]
    public async Task SwitchAsyncReturnsFailureWhenTargetRecordMissing()
    {
        var dataRoot = CreateTemporaryDirectory();
        await new SdkCatalogStore().SaveAsync(dataRoot, SdkCatalog.CreateEmpty());
        var helper = new FakeHelperClient();
        var service = CreateService(dataRoot, helper);

        var result = await service.SwitchAsync(SdkType.Java, "missing-record");

        Assert.False(result.Success);
        Assert.Equal("sdk-record-not-found", result.ErrorCode);
        Assert.Equal(0, helper.SwitchCalls.Count);
    }

    [Fact]
    public async Task SwitchAsyncDoesNotCallHelperOrUpdateActiveWhenTargetPathInvalid()
    {
        var dataRoot = CreateTemporaryDirectory();
        var record = CreateRecord("java-invalid", SdkType.Java, Path.Combine(dataRoot, "missing-jdk"), SdkRecordStatus.Usable);
        await new SdkCatalogStore().SaveAsync(dataRoot, new SdkCatalog(1, ActiveSdkSet.Empty, new[] { record }));
        var helper = new FakeHelperClient();
        var service = CreateService(dataRoot, helper);

        var result = await service.SwitchAsync(SdkType.Java, record.Id);
        var catalog = await new SdkCatalogStore().LoadOrCreateAsync(dataRoot);

        Assert.False(result.Success);
        Assert.Equal("sdk-target-invalid", result.ErrorCode);
        Assert.Null(catalog.Active.Java);
        Assert.Equal(0, helper.SwitchCalls.Count);
    }

    [Fact]
    public async Task SwitchAsyncUpdatesActiveWhenCurrentMissingAndHelperSucceeds()
    {
        var dataRoot = CreateTemporaryDirectory();
        var targetRoot = CreateUsableJavaRoot();
        var record = CreateRecord("java-21", SdkType.Java, targetRoot, SdkRecordStatus.Usable);
        await new SdkCatalogStore().SaveAsync(dataRoot, new SdkCatalog(1, ActiveSdkSet.Empty, new[] { record }));
        var helper = new FakeHelperClient { InspectTargetPath = targetRoot };
        var service = CreateService(dataRoot, helper);

        var result = await service.SwitchAsync(SdkType.Java, record.Id);
        var catalog = await new SdkCatalogStore().LoadOrCreateAsync(dataRoot);

        Assert.True(result.Success);
        Assert.Equal(record.Id, catalog.Active.Java);
        Assert.Equal(SdkRecordStatus.Active, Assert.Single(catalog.Items).Status);
        Assert.Single(helper.SwitchCalls);
    }

    [Fact]
    public async Task SwitchAsyncUpdatesActiveFromPreviousRecordToTargetRecordAfterHelperSuccess()
    {
        var dataRoot = CreateTemporaryDirectory();
        var previousRoot = CreateUsableJavaRoot();
        var targetRoot = CreateUsableJavaRoot();
        var previous = CreateRecord("java-17", SdkType.Java, previousRoot, SdkRecordStatus.Active);
        var target = CreateRecord("java-21", SdkType.Java, targetRoot, SdkRecordStatus.Usable);
        await new SdkCatalogStore().SaveAsync(dataRoot, new SdkCatalog(1, new ActiveSdkSet(previous.Id, null, null, null), new[] { previous, target }));
        var helper = new FakeHelperClient { InspectTargetPath = targetRoot };
        var service = CreateService(dataRoot, helper);

        var result = await service.SwitchAsync(SdkType.Java, target.Id);
        var catalog = await new SdkCatalogStore().LoadOrCreateAsync(dataRoot);

        Assert.True(result.Success);
        Assert.Equal(target.Id, catalog.Active.Java);
        Assert.Equal(SdkRecordStatus.Usable, catalog.Items.Single(item => item.Id == previous.Id).Status);
        Assert.Equal(SdkRecordStatus.Active, catalog.Items.Single(item => item.Id == target.Id).Status);
    }

    [Fact]
    public async Task SwitchAsyncKeepsActiveWhenHelperFails()
    {
        var dataRoot = CreateTemporaryDirectory();
        var previousRoot = CreateUsableJavaRoot();
        var targetRoot = CreateUsableJavaRoot();
        var previous = CreateRecord("java-17", SdkType.Java, previousRoot, SdkRecordStatus.Active);
        var target = CreateRecord("java-21", SdkType.Java, targetRoot, SdkRecordStatus.Usable);
        await new SdkCatalogStore().SaveAsync(dataRoot, new SdkCatalog(1, new ActiveSdkSet(previous.Id, null, null, null), new[] { previous, target }));
        var helper = new FakeHelperClient
        {
            SwitchResponse = CreateResponse(false, "current-path-not-managed-link", "current path is a real directory", "{}"),
            InspectTargetPath = targetRoot,
        };
        var service = CreateService(dataRoot, helper);

        var result = await service.SwitchAsync(SdkType.Java, target.Id);
        var catalog = await new SdkCatalogStore().LoadOrCreateAsync(dataRoot);

        Assert.False(result.Success);
        Assert.Equal("current-path-not-managed-link", result.ErrorCode);
        Assert.Equal(previous.Id, catalog.Active.Java);
        Assert.Equal(SdkRecordStatus.Active, catalog.Items.Single(item => item.Id == previous.Id).Status);
        Assert.Equal(SdkRecordStatus.Usable, catalog.Items.Single(item => item.Id == target.Id).Status);
    }

    [Fact]
    public async Task SwitchAsyncRollsBackToPreviousActiveWhenPostSwitchValidationFails()
    {
        var dataRoot = CreateTemporaryDirectory();
        var previousRoot = CreateUsableJavaRoot();
        var targetRoot = CreateUsableJavaRoot();
        var previous = CreateRecord("java-17", SdkType.Java, previousRoot, SdkRecordStatus.Active);
        var target = CreateRecord("java-21", SdkType.Java, targetRoot, SdkRecordStatus.Usable);
        await new SdkCatalogStore().SaveAsync(dataRoot, new SdkCatalog(1, new ActiveSdkSet(previous.Id, null, null, null), new[] { previous, target }));
        var helper = new FakeHelperClient { InspectTargetPath = previousRoot };
        var service = CreateService(dataRoot, helper);

        var result = await service.SwitchAsync(SdkType.Java, target.Id);
        var catalog = await new SdkCatalogStore().LoadOrCreateAsync(dataRoot);

        Assert.False(result.Success);
        Assert.Equal("post-switch-validation-failed", result.ErrorCode);
        Assert.Equal(previous.Id, catalog.Active.Java);
        Assert.Equal(2, helper.SwitchCalls.Count);
        Assert.Equal(targetRoot, helper.SwitchCalls[0].TargetPath);
        Assert.Equal(previousRoot, helper.SwitchCalls[1].TargetPath);
    }

    [Fact]
    public async Task SwitchAsyncConvergesAllDirtyActiveRecordsToTargetOnly()
    {
        // NOTE: catalog 含多条同类型脏 Active 记录（历史切换没清干净）；切换到 target 后保存的 catalog 同类型应仅 target 为 Active。
        var dataRoot = CreateTemporaryDirectory();
        var dirtyRootA = CreateUsableJavaRoot();
        var dirtyRootB = CreateUsableJavaRoot();
        var targetRoot = CreateUsableJavaRoot();
        var dirtyA = CreateRecord("java-dirtyA", SdkType.Java, dirtyRootA, SdkRecordStatus.Active);
        var dirtyB = CreateRecord("java-dirtyB", SdkType.Java, dirtyRootB, SdkRecordStatus.Active);
        var target = CreateRecord("java-target", SdkType.Java, targetRoot, SdkRecordStatus.Usable);
        // active 指针指向 dirtyA，但 dirtyB 也残留 Active。
        await new SdkCatalogStore().SaveAsync(dataRoot, new SdkCatalog(1, new ActiveSdkSet("java-dirtyA", null, null, null), new[] { dirtyA, dirtyB, target }));
        var helper = new FakeHelperClient { InspectTargetPath = targetRoot };
        var service = CreateService(dataRoot, helper);

        var result = await service.SwitchAsync(SdkType.Java, target.Id);
        var catalog = await new SdkCatalogStore().LoadOrCreateAsync(dataRoot);

        Assert.True(result.Success);
        Assert.Equal(target.Id, catalog.Active.Java);
        Assert.Equal(SdkRecordStatus.Active, catalog.Items.Single(item => item.Id == target.Id).Status);
        Assert.Equal(SdkRecordStatus.Usable, catalog.Items.Single(item => item.Id == "java-dirtyA").Status);
        Assert.Equal(SdkRecordStatus.Usable, catalog.Items.Single(item => item.Id == "java-dirtyB").Status);
        Assert.Single(catalog.Items, item => item.Status == SdkRecordStatus.Active);
    }

    private static SdkSwitchService CreateService(string dataRoot, FakeHelperClient helper)
    {
        return new SdkSwitchService(dataRoot, new SdkCatalogStore(), helper);
    }

    private static SdkRecord CreateRecord(string id, SdkType type, string path, SdkRecordStatus status)
    {
        return new SdkRecord(
            Id: id,
            Type: type,
            Name: id,
            Version: "unknown",
            Distribution: "test",
            Architecture: SdkArchitecture.X64,
            Source: SdkSourceKind.External,
            Path: path,
            Status: status,
            CreatedAt: DateTimeOffset.Parse("2026-06-09T00:00:00Z"),
            LastVerifiedAt: null);
    }

    private static string CreateUsableJavaRoot()
    {
        var root = CreateTemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        File.WriteAllText(Path.Combine(root, "release"), "JAVA_VERSION=\"21\"");
        File.WriteAllText(Path.Combine(root, "bin", "java.exe"), string.Empty);
        File.WriteAllText(Path.Combine(root, "bin", "javac.exe"), string.Empty);
        return root;
    }

    private static HelperResponse CreateResponse(bool success, string? errorCode, string message, string detailsJson)
    {
        using var document = JsonDocument.Parse(detailsJson);
        return new HelperResponse("fake", success, errorCode, message, document.RootElement.Clone());
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeHelperClient : IHelperClient
    {
        public List<(SdkType SdkType, string CurrentPath, string TargetPath)> SwitchCalls { get; } = new();

        public HelperResponse? SwitchResponse { get; init; }

        public string? InspectTargetPath { get; init; }

        public Task<HelperResponse> SwitchSdkAsync(SdkType sdkType, string currentPath, string targetPath, CancellationToken cancellationToken = default)
        {
            SwitchCalls.Add((sdkType, currentPath, targetPath));
            return Task.FromResult(SwitchResponse ?? CreateResponse(true, null, "SDK switched.", "{}"));
        }

        public List<(SdkType SdkType, string CurrentPath, string TargetPath, bool Broadcast)> BatchSwitchCalls { get; } = new();

        public Task<HelperResponse> SwitchSdkBatchAsync(SdkType sdkType, string currentPath, string targetPath, bool broadcast, CancellationToken cancellationToken = default)
        {
            BatchSwitchCalls.Add((sdkType, currentPath, targetPath, broadcast));
            return Task.FromResult(SwitchResponse ?? CreateResponse(true, null, "SDK switched (batch).", "{\"switched\":true}"));
        }

        public Task<HelperResponse> InspectLinkAsync(string currentPath, CancellationToken cancellationToken = default)
        {
            var targetPath = InspectTargetPath ?? string.Empty;
            return Task.FromResult(CreateResponse(true, null, "link inspected", $"{{\"targetPath\":{JsonSerializer.Serialize(targetPath)}}}"));
        }
    }
}
