// 文件用途：验证 downloads.json 仓储的初始化、往返与原子写入行为。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit、DevSwitch.Core
// NOTE: 合法授权学习使用，仅限本地环境。只写入临时目录，不触碰真实数据根目录。

using DevSwitch.Core;
using DevSwitch.Downloader;
using Xunit;

namespace DevSwitch.Tests;

public sealed class DownloaderCatalogStoreTests
{
    [Fact]
    public async Task LoadOrCreateAsyncCreatesEmptyCatalogWhenMissing()
    {
        // 首次启动没有 downloads.json 时返回空目录并落盘。
        var dataRoot = CreateTemporaryDirectory();
        var store = new DownloadCatalogStore();

        var catalog = await store.LoadOrCreateAsync(dataRoot);

        Assert.Equal(1, catalog.SchemaVersion);
        Assert.Empty(catalog.Tasks);
        Assert.True(File.Exists(Path.Combine(dataRoot, "config", "downloads.json")));
    }

    [Fact]
    public async Task SaveThenLoadRoundTripsTaskWithChunksAndStatus()
    {
        // 仓储往返：任务的状态、字节进度、分块都应被完整保存并读回。
        var dataRoot = CreateTemporaryDirectory();
        var store = new DownloadCatalogStore();

        var task = new DownloadTask(
            Id: "download-java-17",
            SdkType: SdkType.Java,
            Version: "17.0.10",
            Distribution: "temurin",
            Arch: SdkArchitecture.X64,
            Url: "https://example.invalid/temurin-17.zip",
            ExpectedSha256: "abc123",
            Status: DownloadStatus.Paused,
            BytesTotal: 100,
            BytesCompleted: 50,
            Chunks: new[]
            {
                new DownloadChunk(0, 0, 49, 50),
                new DownloadChunk(1, 50, 99, 0),
            });

        var catalog = new DownloadCatalog(SchemaVersion: 1, Tasks: new[] { task });

        await store.SaveAsync(dataRoot, catalog);
        var loaded = await store.LoadOrCreateAsync(dataRoot);

        var loadedTask = Assert.Single(loaded.Tasks);
        // NOTE: record 默认相等性对集合字段按引用比较，反序列化得到 List 与原数组不会整体相等，
        //       因此逐字段断言关键标量并单独核对分块内容。
        Assert.Equal(task.Id, loadedTask.Id);
        Assert.Equal(task.SdkType, loadedTask.SdkType);
        Assert.Equal(task.ExpectedSha256, loadedTask.ExpectedSha256);
        Assert.Equal(task.BytesTotal, loadedTask.BytesTotal);
        Assert.Equal(task.BytesCompleted, loadedTask.BytesCompleted);
        Assert.Equal(DownloadStatus.Paused, loadedTask.Status);
        Assert.Equal(2, loadedTask.Chunks.Count);
        Assert.Equal(task.Chunks[0], loadedTask.Chunks[0]);
        Assert.Equal(task.Chunks[1], loadedTask.Chunks[1]);
        Assert.Equal(50, loadedTask.Chunks[0].BytesCompleted);
    }

    [Fact]
    public async Task SaveDoesNotLeaveTemporaryFileAfterSuccess()
    {
        // 原子写入：成功后不应残留 .tmp 文件，且即使有陈旧 .tmp 也会被覆盖。
        var dataRoot = CreateTemporaryDirectory();
        var store = new DownloadCatalogStore();
        var temporaryFile = Path.Combine(dataRoot, "config", "downloads.json.tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(temporaryFile)!);
        await File.WriteAllTextAsync(temporaryFile, "stale temporary content");

        await store.SaveAsync(dataRoot, DownloadCatalog.CreateEmpty());

        Assert.False(File.Exists(temporaryFile));
        Assert.True(File.Exists(Path.Combine(dataRoot, "config", "downloads.json")));
    }

    [Fact]
    public async Task LoadRejectsNewerSchemaVersion()
    {
        // schemaVersion 高于当前支持版本时应拒绝，避免误读未来格式。
        var dataRoot = CreateTemporaryDirectory();
        var configDir = Path.Combine(dataRoot, "config");
        Directory.CreateDirectory(configDir);
        await File.WriteAllTextAsync(
            Path.Combine(configDir, "downloads.json"),
            "{\"schemaVersion\": 999, \"tasks\": []}");

        var store = new DownloadCatalogStore();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadOrCreateAsync(dataRoot));
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
