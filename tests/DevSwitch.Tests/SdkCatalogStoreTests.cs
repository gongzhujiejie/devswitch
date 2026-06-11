// 文件用途：验证 DevSwitch sdks.json 的公开仓储行为。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit
// NOTE: 合法授权学习使用，仅限本地环境。本测试只写入临时目录，不扫描真实 SDK。

using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class SdkCatalogStoreTests
{
    [Fact]
    public async Task LoadOrCreateAsyncCreatesEmptyCatalogWhenMissing()
    {
        // NOTE: 首次启动时如果没有 sdks.json，用户应得到一个空 SDK 目录，而不是异常或假数据。
        var dataRoot = CreateTemporaryDirectory();
        var store = new SdkCatalogStore();

        var catalog = await store.LoadOrCreateAsync(dataRoot);

        Assert.Equal(1, catalog.SchemaVersion);
        Assert.Null(catalog.Active.Java);
        Assert.Null(catalog.Active.Maven);
        Assert.Null(catalog.Active.Node);
        Assert.Null(catalog.Active.Go);
        Assert.Empty(catalog.Items);
        Assert.True(File.Exists(Path.Combine(dataRoot, "config", "sdks.json")));
    }

    [Fact]
    public async Task SaveAsyncThenLoadOrCreateAsyncRoundTripsSdkRecordsAndActiveSelection()
    {
        // NOTE: 仓储行为只验证公开数据能保存并读回，不依赖 UI 表格或 helper 切换实现。
        var dataRoot = CreateTemporaryDirectory();
        var store = new SdkCatalogStore();
        var createdAt = DateTimeOffset.Parse("2026-06-09T00:00:00Z");
        var record = new SdkRecord(
            Id: "sdk-java-17",
            Type: SdkType.Java,
            Name: "Temurin 17",
            Version: "17.0.10",
            Distribution: "temurin",
            Architecture: SdkArchitecture.X64,
            Source: SdkSourceKind.External,
            Path: @"D:\\Programs\\Java\\jdk-17",
            Status: SdkRecordStatus.Usable,
            CreatedAt: createdAt,
            LastVerifiedAt: null);

        var catalog = new SdkCatalog(
            SchemaVersion: 1,
            Active: new ActiveSdkSet(Java: record.Id, Maven: null, Node: null, Go: null),
            Items: new[] { record });

        await store.SaveAsync(dataRoot, catalog);
        var loaded = await store.LoadOrCreateAsync(dataRoot);

        var loadedRecord = Assert.Single(loaded.Items);
        Assert.Equal(record, loadedRecord);
        Assert.Equal(record.Id, loaded.Active.Java);
    }

    [Fact]
    public async Task SaveAsyncDoesNotLeaveTemporarySdkCatalogFileAfterSuccess()
    {
        // NOTE: 与 settings.json 一致，sdks.json 也需要临时文件替换策略，避免写一半破坏旧目录。
        var dataRoot = CreateTemporaryDirectory();
        var store = new SdkCatalogStore();
        var temporaryFile = Path.Combine(dataRoot, "config", "sdks.json.tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(temporaryFile)!);
        await File.WriteAllTextAsync(temporaryFile, "stale temporary content");

        await store.SaveAsync(dataRoot, SdkCatalog.CreateEmpty());

        Assert.False(File.Exists(temporaryFile));
        Assert.True(File.Exists(Path.Combine(dataRoot, "config", "sdks.json")));
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
