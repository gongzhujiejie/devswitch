// 文件用途：验证真实 sdks.json 到 SDK 列表行的公开投影行为。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit
// NOTE: 合法授权学习使用，仅限本地环境。本测试只读写临时目录下的 sdks.json。

using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class SdkCatalogViewServiceTests
{
    [Fact]
    public async Task LoadRowsAsyncReturnsRowsFromPersistedCatalog()
    {
        // NOTE: 该测试通过公开仓储写入真实 catalog，再通过视图服务读取，验证 GUI 不再依赖假数据。
        var dataRoot = CreateTemporaryDirectory();
        var record = CreateRecord("java-21", SdkType.Java, "Temurin 21", "21.0.2", SdkSourceKind.External, SdkRecordStatus.Usable, @"D:\SDK\jdk-21");
        await new SdkCatalogStore().SaveAsync(dataRoot, new SdkCatalog(1, ActiveSdkSet.Empty, new[] { record }));

        var rows = await new SdkCatalogViewService().LoadRowsAsync(dataRoot);

        var row = Assert.Single(rows);
        Assert.Equal("java-21", row.Id);
        Assert.Equal(SdkType.Java, row.Type);
        Assert.Equal("Java", row.Category);
        Assert.Equal("Temurin 21", row.Name);
        Assert.Equal("21.0.2", row.Version);
        Assert.Equal("外部", row.Source);
        Assert.Equal(@"D:\SDK\jdk-21", row.Path);
        Assert.Equal("可用", row.Status);
        Assert.Equal("切换", row.Operation);
        Assert.True(row.CanSwitch);
    }

    [Fact]
    public async Task LoadRowsAsyncFiltersBySdkType()
    {
        // NOTE: 分类切换应基于已加载 catalog 的类型字段过滤，不能混入其它 SDK 类型。
        var dataRoot = CreateTemporaryDirectory();
        var java = CreateRecord("java-21", SdkType.Java, "Java", "21", SdkSourceKind.External, SdkRecordStatus.Usable, @"D:\SDK\jdk-21");
        var node = CreateRecord("node-22", SdkType.Node, "Node.js", "22", SdkSourceKind.Managed, SdkRecordStatus.Usable, @"D:\SDK\node-22");
        await new SdkCatalogStore().SaveAsync(dataRoot, new SdkCatalog(1, ActiveSdkSet.Empty, new[] { java, node }));

        var rows = await new SdkCatalogViewService().LoadRowsAsync(dataRoot, SdkType.Node);

        var row = Assert.Single(rows);
        Assert.Equal("node-22", row.Id);
        Assert.Equal("Node.js", row.Category);
        Assert.Equal("托管", row.Source);
    }

    [Fact]
    public void ToRowMarksActiveRecordAsCurrentEvenWhenRecordStatusIsUsable()
    {
        // NOTE: active 指针是当前使用状态的权威来源，记录自身 status 仍为 Usable 时也要展示“使用中”。
        var record = CreateRecord("java-active", SdkType.Java, "Java", "17", SdkSourceKind.External, SdkRecordStatus.Usable, @"D:\SDK\jdk-17");
        var active = new ActiveSdkSet(Java: record.Id, Maven: null, Node: null, Go: null);

        var row = SdkCatalogViewService.ToRow(record, active);

        Assert.Equal("使用中", row.Status);
        Assert.Equal("当前", row.Operation);
        Assert.False(row.CanSwitch);
    }

    [Fact]
    public void ToRowsClampsDirtyActiveRecordsToUsableWhenActivePointerIsNull()
    {
        // NOTE: 脏数据场景——active.java=null 但两条 java record 的 status 都被历史切换残留成 Active。
        // 期望：active 指针未命中任何记录时，所有 record.Status==Active 都钳为“可用”，绝不展示“使用中”。
        var first = CreateRecord("java-1", SdkType.Java, "JDK1", "17", SdkSourceKind.External, SdkRecordStatus.Active, @"D:\SDK\jdk-17");
        var second = CreateRecord("java-2", SdkType.Java, "JDK2", "21", SdkSourceKind.External, SdkRecordStatus.Active, @"D:\SDK\jdk-21");
        var catalog = new SdkCatalog(1, ActiveSdkSet.Empty, new[] { first, second });

        var rows = SdkCatalogViewService.ToRows(catalog);

        Assert.All(rows, row => Assert.Equal("可用", row.Status));
        Assert.DoesNotContain(rows, row => row.Status == "使用中");
    }

    [Fact]
    public void ToRowsShowsOnlyActivePointerRecordAsCurrentAmongDirtyRecords()
    {
        // NOTE: active.java=id2，三条 java 记录 status 含脏 Active；只有 active 指针命中的 id2 应为“使用中”，其余钳为“可用”。
        var id1 = CreateRecord("java-1", SdkType.Java, "JDK1", "17", SdkSourceKind.External, SdkRecordStatus.Active, @"D:\SDK\jdk-17");
        var id2 = CreateRecord("java-2", SdkType.Java, "JDK2", "21", SdkSourceKind.External, SdkRecordStatus.Usable, @"D:\SDK\jdk-21");
        var id3 = CreateRecord("java-3", SdkType.Java, "JDK3", "8", SdkSourceKind.External, SdkRecordStatus.Active, @"D:\SDK\jdk-8");
        var catalog = new SdkCatalog(1, new ActiveSdkSet(Java: "java-2", Maven: null, Node: null, Go: null), new[] { id1, id2, id3 });

        var rows = SdkCatalogViewService.ToRows(catalog);

        Assert.Equal("使用中", rows.Single(row => row.Id == "java-2").Status);
        Assert.Equal("可用", rows.Single(row => row.Id == "java-1").Status);
        Assert.Equal("可用", rows.Single(row => row.Id == "java-3").Status);
        Assert.Single(rows, row => row.Status == "使用中");
    }

    [Fact]
    public void ToRowClampsDirtyActiveRecordToUsableWhenNotPointedByActive()
    {
        // NOTE: 单条投影——记录自身 status=Active 但未被 active 指针命中（脏数据），应钳为“可用”。
        var record = CreateRecord("java-dirty", SdkType.Java, "Java", "17", SdkSourceKind.External, SdkRecordStatus.Active, @"D:\SDK\jdk-17");

        var row = SdkCatalogViewService.ToRow(record, ActiveSdkSet.Empty);

        Assert.Equal("可用", row.Status);
        Assert.Equal("切换", row.Operation);
        Assert.True(row.CanSwitch);
    }

    [Fact]
    public void ReconcileActiveStatusKeepsOnlyPointedRecordActive()
    {
        // NOTE: active=java-2，但 java-1 status 残留 Active；以 active 指针为唯一真相修正后只有 java-2 是 Active。
        var id1 = CreateRecord("java-1", SdkType.Java, "JDK1", "17", SdkSourceKind.External, SdkRecordStatus.Active, @"D:\SDK\jdk-17");
        var id2 = CreateRecord("java-2", SdkType.Java, "JDK2", "21", SdkSourceKind.External, SdkRecordStatus.Usable, @"D:\SDK\jdk-21");
        var catalog = new SdkCatalog(1, new ActiveSdkSet(Java: "java-2", Maven: null, Node: null, Go: null), new[] { id1, id2 });

        var reconciled = SdkCatalogViewService.ReconcileActiveStatus(catalog);

        Assert.Equal(SdkRecordStatus.Usable, reconciled.Items.Single(item => item.Id == "java-1").Status);
        Assert.Equal(SdkRecordStatus.Active, reconciled.Items.Single(item => item.Id == "java-2").Status);
        Assert.Equal("java-2", reconciled.Active.Java);
    }

    [Fact]
    public void ReconcileActiveStatusClearsAllActiveWhenPointerIsNull()
    {
        // NOTE: active.java=null 时，该类型所有 status==Active 残留都应改为 Usable。
        var id1 = CreateRecord("java-1", SdkType.Java, "JDK1", "17", SdkSourceKind.External, SdkRecordStatus.Active, @"D:\SDK\jdk-17");
        var id2 = CreateRecord("java-2", SdkType.Java, "JDK2", "21", SdkSourceKind.External, SdkRecordStatus.Active, @"D:\SDK\jdk-21");
        var catalog = new SdkCatalog(1, ActiveSdkSet.Empty, new[] { id1, id2 });

        var reconciled = SdkCatalogViewService.ReconcileActiveStatus(catalog);

        Assert.All(reconciled.Items, item => Assert.Equal(SdkRecordStatus.Usable, item.Status));
        Assert.Null(reconciled.Active.Java);
    }

    [Fact]
    public void ReconcileActiveStatusPreservesNonActiveStatuses()
    {
        // NOTE: 自愈只针对 Active 残留，不得改动 Unavailable/Unverified，也不删记录、不动 active 指针。
        var unavailable = CreateRecord("go-missing", SdkType.Go, "Go", "unknown", SdkSourceKind.External, SdkRecordStatus.Unavailable, @"D:\missing\go");
        var unverified = CreateRecord("node-new", SdkType.Node, "Node", "22", SdkSourceKind.Managed, SdkRecordStatus.Unverified, @"D:\SDK\node-22");
        var catalog = new SdkCatalog(1, ActiveSdkSet.Empty, new[] { unavailable, unverified });

        var reconciled = SdkCatalogViewService.ReconcileActiveStatus(catalog);

        Assert.Equal(SdkRecordStatus.Unavailable, reconciled.Items.Single(item => item.Id == "go-missing").Status);
        Assert.Equal(SdkRecordStatus.Unverified, reconciled.Items.Single(item => item.Id == "node-new").Status);
        Assert.Equal(2, reconciled.Items.Count);
    }

    [Fact]
    public void ReconcileActiveStatusActivatesPointedRecordEvenWhenStatusWasUsable()
    {
        // NOTE: active 指针命中的记录即便 status 仍为 Usable（持久化时漏写）也要修正为 Active。
        var id1 = CreateRecord("java-1", SdkType.Java, "JDK1", "21", SdkSourceKind.External, SdkRecordStatus.Usable, @"D:\SDK\jdk-21");
        var catalog = new SdkCatalog(1, new ActiveSdkSet(Java: "java-1", Maven: null, Node: null, Go: null), new[] { id1 });

        var reconciled = SdkCatalogViewService.ReconcileActiveStatus(catalog);

        Assert.Equal(SdkRecordStatus.Active, reconciled.Items.Single(item => item.Id == "java-1").Status);
    }

    [Fact]
    public void ToRowMapsUnavailableRecordToReasonOperation()
    {
        // NOTE: 不可用 SDK 不能切换，只能提示用户查看原因或后续诊断。
        var record = CreateRecord("go-missing", SdkType.Go, "Go", "unknown", SdkSourceKind.External, SdkRecordStatus.Unavailable, @"D:\missing\go");

        var row = SdkCatalogViewService.ToRow(record, ActiveSdkSet.Empty);

        Assert.Equal("Go", row.Category);
        Assert.Equal("不可用", row.Status);
        Assert.Equal("查看原因", row.Operation);
        Assert.False(row.CanSwitch);
    }

    [Fact]
    public async Task ImportLocalAsyncThenViewServiceReturnsImportedRow()
    {
        // NOTE: 端到端锁定“添加本地 SDK”按钮后续依赖的行为：导入成功后刷新列表即可看到新增外部 SDK。
        var dataRoot = CreateTemporaryDirectory();
        var jdkRoot = CreateTemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(jdkRoot, "bin"));
        await File.WriteAllTextAsync(Path.Combine(jdkRoot, "release"), "JAVA_VERSION=\"21\"");
        await File.WriteAllTextAsync(Path.Combine(jdkRoot, "bin", "java.exe"), string.Empty);
        await File.WriteAllTextAsync(Path.Combine(jdkRoot, "bin", "javac.exe"), string.Empty);

        var importResult = await new LocalSdkImportService(dataRoot).ImportLocalAsync(jdkRoot, customName: "本地 JDK 21");
        var rows = await new SdkCatalogViewService().LoadRowsAsync(dataRoot, SdkType.Java);

        Assert.True(importResult.Success);
        var row = Assert.Single(rows);
        Assert.Equal(importResult.Record!.Id, row.Id);
        Assert.Equal("本地 JDK 21", row.Name);
        Assert.Equal("外部", row.Source);
        Assert.Equal("可用", row.Status);
        Assert.Equal("切换", row.Operation);
    }

    private static SdkRecord CreateRecord(
        string id,
        SdkType type,
        string name,
        string version,
        SdkSourceKind source,
        SdkRecordStatus status,
        string path)
    {
        return new SdkRecord(
            Id: id,
            Type: type,
            Name: name,
            Version: version,
            Distribution: "test",
            Architecture: SdkArchitecture.X64,
            Source: source,
            Path: path,
            Status: status,
            CreatedAt: DateTimeOffset.Parse("2026-06-09T00:00:00Z"),
            LastVerifiedAt: null);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
