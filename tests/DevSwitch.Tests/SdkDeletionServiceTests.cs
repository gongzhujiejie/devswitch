// 文件用途：验证 SdkDeletionService 的删除业务行为与安全红线。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit
// NOTE: 合法授权学习使用，仅限本地环境。本测试用 fake remover，不真实删盘。

using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class SdkDeletionServiceTests
{
    [Fact]
    public async Task ExternalSdkDeletionRemovesRecordButNeverDeletesEntity()
    {
        var dataRoot = CreateTemporaryDirectory();
        var externalPath = Path.Combine(dataRoot, "external", "jdk-21");
        var record = CreateRecord("ext-java", SdkType.Java, SdkSourceKind.External, externalPath);
        await new SdkCatalogStore().SaveAsync(dataRoot, new SdkCatalog(1, ActiveSdkSet.Empty, new[] { record }));
        var remover = new FakeManagedFileRemover();
        var service = new SdkDeletionService(new SdkCatalogStore(), remover);

        var result = await service.DeleteAsync(dataRoot, record.Id, confirmDeleteManagedFiles: true);
        var catalog = await new SdkCatalogStore().LoadOrCreateAsync(dataRoot);

        Assert.True(result.Success);
        Assert.True(result.RecordRemoved);
        Assert.False(result.EntityDeleted);
        // 安全红线：外部 SDK 即便 confirm=true 也不调用删除。
        Assert.Equal(0, remover.RemoveCalls.Count);
        Assert.Empty(catalog.Items);
    }

    [Fact]
    public async Task ManagedSdkWithoutConfirmationKeepsEntityAndFlagsPending()
    {
        var dataRoot = CreateTemporaryDirectory();
        var managedPath = Path.Combine(dataRoot, "sdks", "java", "temurin-21");
        var record = CreateRecord("mgd-java", SdkType.Java, SdkSourceKind.Managed, managedPath);
        await new SdkCatalogStore().SaveAsync(dataRoot, new SdkCatalog(1, ActiveSdkSet.Empty, new[] { record }));
        var remover = new FakeManagedFileRemover();
        var service = new SdkDeletionService(new SdkCatalogStore(), remover);

        var result = await service.DeleteAsync(dataRoot, record.Id, confirmDeleteManagedFiles: false);

        Assert.True(result.Success);
        Assert.True(result.RecordRemoved);
        Assert.False(result.EntityDeleted);
        Assert.True(result.EntityPreservedPendingConfirmation);
        Assert.Equal(0, remover.RemoveCalls.Count);
    }

    [Fact]
    public async Task ManagedSdkWithConfirmationDeletesEntity()
    {
        var dataRoot = CreateTemporaryDirectory();
        var managedPath = Path.Combine(dataRoot, "sdks", "java", "temurin-21");
        var record = CreateRecord("mgd-java", SdkType.Java, SdkSourceKind.Managed, managedPath);
        await new SdkCatalogStore().SaveAsync(dataRoot, new SdkCatalog(1, ActiveSdkSet.Empty, new[] { record }));
        var remover = new FakeManagedFileRemover();
        var service = new SdkDeletionService(new SdkCatalogStore(), remover);

        var result = await service.DeleteAsync(dataRoot, record.Id, confirmDeleteManagedFiles: true);

        Assert.True(result.Success);
        Assert.True(result.EntityDeleted);
        Assert.False(result.EntityPreservedPendingConfirmation);
        Assert.Single(remover.RemoveCalls);
        Assert.Equal(Path.GetFullPath(managedPath), Path.GetFullPath(remover.RemoveCalls[0]));
    }

    [Fact]
    public async Task DeletingActiveRecordClearsActivePointer()
    {
        var dataRoot = CreateTemporaryDirectory();
        var managedPath = Path.Combine(dataRoot, "sdks", "go", "go-1.22");
        var record = CreateRecord("mgd-go", SdkType.Go, SdkSourceKind.Managed, managedPath);
        await new SdkCatalogStore().SaveAsync(dataRoot, new SdkCatalog(1, new ActiveSdkSet(null, null, null, record.Id), new[] { record }));
        var remover = new FakeManagedFileRemover();
        var service = new SdkDeletionService(new SdkCatalogStore(), remover);

        var result = await service.DeleteAsync(dataRoot, record.Id, confirmDeleteManagedFiles: false);
        var catalog = await new SdkCatalogStore().LoadOrCreateAsync(dataRoot);

        Assert.True(result.ActivePointerCleared);
        Assert.Null(catalog.Active.Go);
        Assert.Empty(catalog.Items);
    }

    [Fact]
    public async Task ManagedSdkWithPathOutsideDataRootIsRefusedAndEntityKept()
    {
        var dataRoot = CreateTemporaryDirectory();
        // 越界路径：托管来源但 Path 落在 dataRoot\sdks 之外。
        var outsidePath = Path.Combine(Path.GetTempPath(), "evil", "jdk");
        var record = CreateRecord("mgd-evil", SdkType.Java, SdkSourceKind.Managed, outsidePath);
        await new SdkCatalogStore().SaveAsync(dataRoot, new SdkCatalog(1, ActiveSdkSet.Empty, new[] { record }));
        var remover = new FakeManagedFileRemover();
        var service = new SdkDeletionService(new SdkCatalogStore(), remover);

        var result = await service.DeleteAsync(dataRoot, record.Id, confirmDeleteManagedFiles: true);

        // 记录仍被移除，但实体绝不删，且不调用 remover。
        Assert.True(result.Success);
        Assert.True(result.RecordRemoved);
        Assert.False(result.EntityDeleted);
        Assert.Equal(0, remover.RemoveCalls.Count);
    }

    [Fact]
    public async Task DeleteAsyncReturnsNotFoundForMissingRecord()
    {
        var dataRoot = CreateTemporaryDirectory();
        await new SdkCatalogStore().SaveAsync(dataRoot, SdkCatalog.CreateEmpty());
        var service = new SdkDeletionService(new SdkCatalogStore(), new FakeManagedFileRemover());

        var result = await service.DeleteAsync(dataRoot, "missing", confirmDeleteManagedFiles: true);

        Assert.False(result.Success);
        Assert.Equal("sdk-record-not-found", result.ErrorCode);
    }

    // ---- 纯函数裁决测试（最优先） ----

    [Theory]
    [InlineData(SdkSourceKind.External, true, false, false)]   // 外部：永不删
    [InlineData(SdkSourceKind.External, false, false, false)]
    [InlineData(SdkSourceKind.Managed, false, false, true)]    // 托管未确认：保留待确认
    [InlineData(SdkSourceKind.Managed, true, true, false)]     // 托管确认+路径内：允许删
    public void EvaluateEntityDeletionCoversCoreDecisions(SdkSourceKind source, bool confirm, bool expectedAllow, bool expectedPending)
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "ds-eval");
        var path = source == SdkSourceKind.Managed
            ? Path.Combine(dataRoot, "sdks", "java", "x")
            : Path.Combine(dataRoot, "external", "x");
        var record = CreateRecord("r", SdkType.Java, source, path);

        var decision = SdkDeletionService.EvaluateEntityDeletion(record, dataRoot, confirm);

        Assert.Equal(expectedAllow, decision.AllowDelete);
        Assert.Equal(expectedPending, decision.PendingConfirmation);
    }

    [Fact]
    public void EvaluateEntityDeletionRefusesManagedPathOutsideRoot()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "ds-eval");
        var record = CreateRecord("r", SdkType.Java, SdkSourceKind.Managed, Path.Combine(Path.GetTempPath(), "other", "x"));

        var decision = SdkDeletionService.EvaluateEntityDeletion(record, dataRoot, confirmDeleteManagedFiles: true);

        Assert.False(decision.AllowDelete);
        Assert.Equal("managed-path-outside-data-root", decision.Reason);
    }

    [Theory]
    [InlineData(@"C:\data", @"C:\data\sdks\java\x", true)]
    [InlineData(@"C:\data", @"C:\data\sdks", false)]            // 不允许删整个 sdks 根
    [InlineData(@"C:\data", @"C:\data\external\x", false)]
    [InlineData(@"C:\data", @"C:\data\sdks-evil\x", false)]     // 前缀伪装不被误判
    [InlineData(@"C:\data", @"C:\data\sdks\..\evil", false)]    // .. 越界被规范化拒绝
    public void IsWithinManagedRootGuardsBoundary(string dataRoot, string candidate, bool expected)
    {
        Assert.Equal(expected, SdkDeletionService.IsWithinManagedRoot(dataRoot, candidate));
    }

    private static SdkRecord CreateRecord(string id, SdkType type, SdkSourceKind source, string path)
    {
        return new SdkRecord(
            Id: id,
            Type: type,
            Name: id,
            Version: "unknown",
            Distribution: "test",
            Architecture: SdkArchitecture.X64,
            Source: source,
            Path: path,
            Status: SdkRecordStatus.Usable,
            CreatedAt: DateTimeOffset.Parse("2026-06-09T00:00:00Z"),
            LastVerifiedAt: null);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// 记录调用、从不真实删盘的 fake 实体删除器。
    /// </summary>
    private sealed class FakeManagedFileRemover : IManagedFileRemover
    {
        public List<string> RemoveCalls { get; } = new();

        public Task RemoveDirectoryAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            RemoveCalls.Add(directoryPath);
            return Task.CompletedTask;
        }
    }
}
