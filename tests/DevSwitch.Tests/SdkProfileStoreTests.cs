// 文件用途：验证 DevSwitch profiles.json 配置档案的读写与增删改公开行为。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit
// NOTE: 合法授权学习使用，仅限本地环境。本测试只写入临时目录，不读取真实用户配置。

using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class SdkProfileStoreTests
{
    [Fact]
    public async Task LoadOrCreateAsyncReturnsEmptyCatalogWhenFileMissing()
    {
        // NOTE: 首次访问没有 profiles.json 时，应自动得到 schemaVersion=1 的空集合并写盘。
        var dataRoot = CreateTemporaryDirectory();
        var store = new SdkProfileStore();

        var catalog = await store.LoadOrCreateAsync(dataRoot);

        Assert.Equal(1, catalog.SchemaVersion);
        Assert.Empty(catalog.Profiles);
        Assert.True(File.Exists(Path.Combine(dataRoot, "config", "profiles.json")));
    }

    [Fact]
    public async Task AddAsyncPersistsProfileWithCorrectFields()
    {
        // NOTE: 新增后应能读回，且 id/name/entries/时间字段正确，CreatedAt==UpdatedAt。
        var dataRoot = CreateTemporaryDirectory();
        var store = new SdkProfileStore();
        var entries = new[]
        {
            new SdkProfileEntry(SdkType.Java, "java-record-1"),
            new SdkProfileEntry(SdkType.Maven, "maven-record-1"),
        };

        var created = await store.AddAsync(dataRoot, "后端项目", entries);

        Assert.False(string.IsNullOrWhiteSpace(created.Id));
        Assert.Equal("后端项目", created.Name);
        Assert.Equal(created.CreatedAt, created.UpdatedAt);

        var reloaded = await store.LoadOrCreateAsync(dataRoot);
        var profile = Assert.Single(reloaded.Profiles);
        Assert.Equal(created.Id, profile.Id);
        Assert.Equal("后端项目", profile.Name);
        Assert.Equal(2, profile.Entries.Count);
        Assert.Equal(SdkType.Java, profile.Entries[0].Type);
        Assert.Equal("java-record-1", profile.Entries[0].RecordId);
        Assert.Equal(SdkType.Maven, profile.Entries[1].Type);
        Assert.Equal("maven-record-1", profile.Entries[1].RecordId);
    }

    [Fact]
    public async Task AddAsyncThrowsWhenNameIsBlank()
    {
        // NOTE: 空白名称没有意义，应在写盘前以 ArgumentException 拒绝。
        var dataRoot = CreateTemporaryDirectory();
        var store = new SdkProfileStore();

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.AddAsync(dataRoot, "   ", Array.Empty<SdkProfileEntry>()));
    }

    [Fact]
    public async Task RemoveAsyncDeletesProfileById()
    {
        // NOTE: 按 id 移除后，集合中应不再包含该档案。
        var dataRoot = CreateTemporaryDirectory();
        var store = new SdkProfileStore();
        var created = await store.AddAsync(dataRoot, "待删除档案", Array.Empty<SdkProfileEntry>());

        await store.RemoveAsync(dataRoot, created.Id);

        var reloaded = await store.LoadOrCreateAsync(dataRoot);
        Assert.Empty(reloaded.Profiles);
    }

    [Fact]
    public async Task RemoveAsyncWithUnknownIdDoesNotThrowOrChangeOthers()
    {
        // NOTE: 移除不存在的 id 视为无操作，不抛异常，且不影响已有档案。
        var dataRoot = CreateTemporaryDirectory();
        var store = new SdkProfileStore();
        var kept = await store.AddAsync(dataRoot, "保留档案", Array.Empty<SdkProfileEntry>());

        await store.RemoveAsync(dataRoot, "non-existent-id");

        var reloaded = await store.LoadOrCreateAsync(dataRoot);
        var profile = Assert.Single(reloaded.Profiles);
        Assert.Equal(kept.Id, profile.Id);
    }

    [Fact]
    public async Task RenameAsyncUpdatesNameAndRefreshesUpdatedAt()
    {
        // NOTE: 改名应更新 Name 并刷新 UpdatedAt（晚于 CreatedAt），CreatedAt 保持不变。
        var dataRoot = CreateTemporaryDirectory();
        var store = new SdkProfileStore();
        var created = await store.AddAsync(dataRoot, "旧名称", Array.Empty<SdkProfileEntry>());

        // NOTE: 制造可观测的时间差，确保 UpdatedAt 变化能被断言到。
        await Task.Delay(20);
        var renamed = await store.RenameAsync(dataRoot, created.Id, "新名称");

        Assert.NotNull(renamed);
        Assert.Equal("新名称", renamed!.Name);
        Assert.Equal(created.CreatedAt, renamed.CreatedAt);
        Assert.True(renamed.UpdatedAt > created.UpdatedAt);

        var reloaded = await store.LoadOrCreateAsync(dataRoot);
        var profile = Assert.Single(reloaded.Profiles);
        Assert.Equal("新名称", profile.Name);
    }

    [Fact]
    public async Task RenameAsyncReturnsNullWhenIdNotFound()
    {
        // NOTE: 目标 id 不存在时返回 null，交由调用方决定提示，不抛异常。
        var dataRoot = CreateTemporaryDirectory();
        var store = new SdkProfileStore();

        var result = await store.RenameAsync(dataRoot, "non-existent-id", "任意名称");

        Assert.Null(result);
    }

    [Fact]
    public async Task RenameAsyncThrowsWhenNewNameIsBlank()
    {
        // NOTE: 改名同样不允许空白名称。
        var dataRoot = CreateTemporaryDirectory();
        var store = new SdkProfileStore();
        var created = await store.AddAsync(dataRoot, "原名称", Array.Empty<SdkProfileEntry>());

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.RenameAsync(dataRoot, created.Id, "  "));
    }

    [Fact]
    public async Task SchemaVersionIsPersistedAndReadBack()
    {
        // NOTE: schemaVersion 必须随文件持久化，保证后续版本迁移可识别。
        var dataRoot = CreateTemporaryDirectory();
        var store = new SdkProfileStore();
        await store.AddAsync(dataRoot, "档案", Array.Empty<SdkProfileEntry>());

        var reloaded = await store.LoadOrCreateAsync(dataRoot);

        Assert.Equal(1, reloaded.SchemaVersion);
    }

    [Fact]
    public async Task DataRootBlankThrowsArgumentException()
    {
        // NOTE: 所有公开方法在 dataRoot 空白时都应以 ArgumentException 拒绝。
        var store = new SdkProfileStore();

        await Assert.ThrowsAsync<ArgumentException>(() => store.LoadOrCreateAsync("  "));
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
