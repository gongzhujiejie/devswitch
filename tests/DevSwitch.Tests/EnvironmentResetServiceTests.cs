// 文件用途：验证 EnvironmentResetService 的工具环境重置编排与安全红线。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit、System.Text.Json
// NOTE: 合法授权学习使用，仅限本地环境。本测试用 fake helper/link，不改注册表、不删真实链接。

using System.Text.Json;
using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class EnvironmentResetServiceTests
{
    [Fact]
    public async Task ResetRemovesManagedPathButKeepsUserEntriesAndClearsVariables()
    {
        var dataRoot = CreateTemporaryDirectory();
        await new SdkCatalogStore().SaveAsync(dataRoot, SdkCatalog.CreateEmpty());
        var envHelper = new FakeEnvironmentHelperClient
        {
            // helper 端只回报移除的托管片段，用户其它 PATH 项不在 removed 里。
            RemoveResponse = OkResponse("{\"removed\":[\"%DEVSWITCH_HOME%\\\\current\\\\java\\\\bin\"],\"count\":1}"),
        };
        var link = new FakeLinkClient();
        var service = CreateService(dataRoot, envHelper, link);

        var result = await service.ResetAsync(dataRoot);

        Assert.True(result.Success);
        // 移除了托管 PATH 片段（fake 仅回报 1 条）。
        Assert.Single(result.RemovedPathEntries);
        Assert.Equal(1, envHelper.RemoveCalls.Count);
        // 移除时传入托管片段集合（占位符旧格式 5 条 + 绝对路径新格式 5 条 = 10 条），绝不传用户其它项。
        // 兼容历史用占位符初始化的用户与新版用绝对路径初始化的用户。
        Assert.Equal(10, envHelper.RemoveCalls[0].Count);
        // 清除了核心托管变量。
        Assert.Contains("DEVSWITCH_HOME", result.RemovedVariables);
        Assert.Contains("JAVA_HOME", result.RemovedVariables);
        Assert.Contains("MAVEN_HOME", result.RemovedVariables);
        Assert.Contains("GOROOT", result.RemovedVariables);
        // 写入空值即清除：变量写调用发生且全部为空值。
        Assert.Single(envHelper.WriteCalls);
        Assert.All(envHelper.WriteCalls[0], v => Assert.Equal(string.Empty, v.Value));
    }

    [Fact]
    public async Task ResetRemovesAllFiveCurrentLinksWithoutTouchingTargets()
    {
        var dataRoot = CreateTemporaryDirectory();
        await new SdkCatalogStore().SaveAsync(dataRoot, SdkCatalog.CreateEmpty());
        var envHelper = new FakeEnvironmentHelperClient();
        var link = new FakeLinkClient();
        var service = CreateService(dataRoot, envHelper, link);

        var result = await service.ResetAsync(dataRoot);

        Assert.True(result.Success);
        // java/maven/node/go/rust 五个 current 入口都尝试移除。
        Assert.Equal(5, link.RemoveCalls.Count);
        Assert.Equal(5, result.RemovedCurrentLinks.Count);
    }

    [Fact]
    public async Task ResetClearsActiveButNeverRemovesSdkRecordsOrEntities()
    {
        var dataRoot = CreateTemporaryDirectory();
        var external = CreateRecord("ext-java", SdkType.Java, SdkSourceKind.External, Path.Combine(dataRoot, "external", "jdk"));
        var managed = CreateRecord("mgd-go", SdkType.Go, SdkSourceKind.Managed, Path.Combine(dataRoot, "sdks", "go"));
        await new SdkCatalogStore().SaveAsync(
            dataRoot,
            new SdkCatalog(1, new ActiveSdkSet(external.Id, null, null, managed.Id), new[] { external, managed }));
        var service = CreateService(dataRoot, new FakeEnvironmentHelperClient(), new FakeLinkClient());

        var result = await service.ResetAsync(dataRoot);
        var catalog = await new SdkCatalogStore().LoadOrCreateAsync(dataRoot);

        Assert.True(result.ActiveCleared);
        Assert.Null(catalog.Active.Java);
        Assert.Null(catalog.Active.Go);
        // SDK 记录与实体登记都保留，重置不删 SDK。
        Assert.Equal(2, catalog.Items.Count);
    }

    [Fact]
    public async Task ResetSkipsActiveSaveWhenAlreadyEmpty()
    {
        var dataRoot = CreateTemporaryDirectory();
        await new SdkCatalogStore().SaveAsync(dataRoot, SdkCatalog.CreateEmpty());
        var service = CreateService(dataRoot, new FakeEnvironmentHelperClient(), new FakeLinkClient());

        var result = await service.ResetAsync(dataRoot);

        Assert.True(result.Success);
        Assert.False(result.ActiveCleared);
    }

    [Fact]
    public async Task ResetWithCompatibilityOptionsClearsJdkAndM2Home()
    {
        var dataRoot = CreateTemporaryDirectory();
        await new SdkCatalogStore().SaveAsync(dataRoot, SdkCatalog.CreateEmpty());
        var service = CreateService(dataRoot, new FakeEnvironmentHelperClient(), new FakeLinkClient());

        var result = await service.ResetAsync(dataRoot, new EnvironmentResetOptions(ClearJdkHome: true, ClearM2Home: true));

        Assert.Contains("JDK_HOME", result.RemovedVariables);
        Assert.Contains("M2_HOME", result.RemovedVariables);
    }

    [Fact]
    public async Task ResetStopsAndReportsWhenPathRemovalFails()
    {
        var dataRoot = CreateTemporaryDirectory();
        var envHelper = new FakeEnvironmentHelperClient
        {
            RemoveResponse = FailResponse("remove-path-failed", "boom"),
        };
        var link = new FakeLinkClient();
        var service = CreateService(dataRoot, envHelper, link);

        var result = await service.ResetAsync(dataRoot);

        Assert.False(result.Success);
        Assert.Equal("remove-path-failed", result.ErrorCode);
        // PATH 移除失败后不应继续删链接。
        Assert.Equal(0, link.RemoveCalls.Count);
    }

    [Fact]
    public void BuildManagedVariableNamesIsPureAndRespectsOptions()
    {
        var defaults = EnvironmentResetService.BuildManagedVariableNames(EnvironmentResetOptions.Default);
        Assert.Equal(new[] { "DEVSWITCH_HOME", "JAVA_HOME", "MAVEN_HOME", "GOROOT" }, defaults);

        var withCompat = EnvironmentResetService.BuildManagedVariableNames(new EnvironmentResetOptions(ClearJdkHome: true, ClearM2Home: true));
        Assert.Contains("JDK_HOME", withCompat);
        Assert.Contains("M2_HOME", withCompat);
        Assert.Equal(6, withCompat.Count);
    }

    private static EnvironmentResetService CreateService(string dataRoot, FakeEnvironmentHelperClient envHelper, FakeLinkClient link)
    {
        return new EnvironmentResetService(
            new EnvironmentService(envHelper),
            envHelper,
            link,
            new SdkCatalogStore());
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

    private static HelperResponse OkResponse(string detailsJson)
    {
        using var doc = JsonDocument.Parse(detailsJson);
        return new HelperResponse("fake", true, null, "ok", doc.RootElement.Clone());
    }

    private static HelperResponse FailResponse(string errorCode, string message)
    {
        return new HelperResponse("fake", false, errorCode, message, null);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// 记录 current link 移除调用的 fake 客户端，默认回报链接已移除。
    /// </summary>
    private sealed class FakeLinkClient : ISdkDeletionLinkClient
    {
        public List<string> RemoveCalls { get; } = new();

        public HelperResponse? Response { get; init; }

        public Task<HelperResponse> RemoveCurrentLinkAsync(string currentPath, CancellationToken cancellationToken = default)
        {
            RemoveCalls.Add(currentPath);
            if (Response is not null)
            {
                return Task.FromResult(Response);
            }

            using var doc = JsonDocument.Parse("{\"removed\":true}");
            return Task.FromResult(new HelperResponse("fake", true, null, "removed", doc.RootElement.Clone()));
        }
    }

    /// <summary>
    /// 复用 EnvironmentServiceTests 风格的 fake 环境 helper（独立内嵌，避免跨文件耦合）。
    /// </summary>
    private sealed class FakeEnvironmentHelperClient : IEnvironmentHelperClient
    {
        public List<IReadOnlyList<EnvironmentVariable>> WriteCalls { get; } = new();

        public List<IReadOnlyList<string>> AppendCalls { get; } = new();

        public List<IReadOnlyList<string>> PrependCalls { get; } = new();

        public List<IReadOnlyList<string>> RemoveCalls { get; } = new();

        public int BroadcastCalls { get; private set; }

        public HelperResponse? WriteResponse { get; set; }

        public HelperResponse? RemoveResponse { get; set; }

        public HelperResponse? BroadcastResponse { get; set; }

        public Task<HelperResponse> WriteUserEnvironmentAsync(IReadOnlyList<EnvironmentVariable> variables, CancellationToken cancellationToken = default)
        {
            WriteCalls.Add(variables);
            return Task.FromResult(WriteResponse ?? DefaultOk("written"));
        }

        public Task<HelperResponse> AppendManagedPathEntriesAsync(IReadOnlyList<string> entries, CancellationToken cancellationToken = default)
        {
            AppendCalls.Add(entries);
            return Task.FromResult(DefaultOk("added"));
        }

        public Task<HelperResponse> PrependManagedPathEntriesAsync(IReadOnlyList<string> entries, CancellationToken cancellationToken = default)
        {
            // 接口契约新增成员；reset 链路不置顶 PATH，仅为满足接口实现而记录到 AppendCalls 之外的独立列表。
            PrependCalls.Add(entries);
            return Task.FromResult(DefaultOk("prepended"));
        }

        public Task<HelperResponse> RemoveManagedPathEntriesAsync(IReadOnlyList<string> entries, CancellationToken cancellationToken = default)
        {
            RemoveCalls.Add(entries);
            return Task.FromResult(RemoveResponse ?? DefaultOk("removed"));
        }

        public List<IReadOnlyList<string>> PrependMachineCalls { get; } = new();

        public List<IReadOnlyList<string>> RemoveMachineCalls { get; } = new();

        public Task<HelperResponse> PrependMachinePathEntriesAsync(IReadOnlyList<string> entries, CancellationToken cancellationToken = default)
        {
            PrependMachineCalls.Add(entries);
            return Task.FromResult(DefaultOk("prepended"));
        }

        public Task<HelperResponse> RemoveMachinePathEntriesAsync(IReadOnlyList<string> entries, CancellationToken cancellationToken = default)
        {
            RemoveMachineCalls.Add(entries);
            return Task.FromResult(RemoveResponse ?? DefaultOk("removed"));
        }

        public Task<HelperResponse> RebuildShimsAsync(string dataRoot, string shimSourcePath, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(DefaultOk("created"));
        }

        public Task<HelperResponse> ReadUserEnvironmentAsync(IReadOnlyList<string> names, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(DefaultOk("values"));
        }

        public Task<HelperResponse> BroadcastEnvironmentChangedAsync(CancellationToken cancellationToken = default)
        {
            BroadcastCalls++;
            return Task.FromResult(BroadcastResponse ?? new HelperResponse("fake", true, null, "ok", null));
        }

        private static HelperResponse DefaultOk(string arrayProperty)
        {
            using var doc = JsonDocument.Parse($"{{\"{arrayProperty}\":[],\"count\":0}}");
            return new HelperResponse("fake", true, null, "ok", doc.RootElement.Clone());
        }
    }
}
