// 文件用途：验证 EnvironmentService 高层编排逻辑（初始化/写变量/追加 PATH/重置/广播）。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit、System.Text.Json
// NOTE: 合法授权学习使用，仅限本地环境。本测试用 fake helper，不触碰真实注册表。

using System.Text.Json;
using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class EnvironmentServiceTests
{
    [Fact]
    public async Task InitializeWritesVariablesPrependsPathAndBroadcastsOnce()
    {
        var helper = new FakeEnvironmentHelperClient();
        var service = new EnvironmentService(helper);

        var result = await service.InitializeEnvironmentAsync(@"%LOCALAPPDATA%\DevSwitch");

        Assert.True(result.Success);
        // 调用顺序：先写变量，再置顶 PATH（修复遮蔽），最后广播一次。
        Assert.Equal(1, helper.WriteCalls.Count);
        // 行为修正：初始化用 prepend 置顶而非 append 追加，避免被用户残留项遮蔽。
        Assert.Equal(1, helper.PrependCalls.Count);
        Assert.Equal(0, helper.AppendCalls.Count);
        Assert.Equal(1, helper.BroadcastCalls);
        // 写入了默认 4 个变量。
        Assert.Equal(4, helper.WriteCalls[0].Count);
        Assert.Contains(helper.WriteCalls[0], v => v.Name == "DEVSWITCH_HOME");
        // 置顶了 5 个托管 PATH 片段（本质：初始化会把托管片段写入 Path）。
        Assert.Equal(5, helper.PrependCalls[0].Count);
    }

    [Fact]
    public async Task InitializeStopsAndReportsWhenPrependFails()
    {
        var helper = new FakeEnvironmentHelperClient
        {
            PrependResponse = FailResponse("registry-write-failed", "boom"),
        };
        var service = new EnvironmentService(helper);

        var result = await service.InitializeEnvironmentAsync(@"%LOCALAPPDATA%\DevSwitch");

        Assert.False(result.Success);
        Assert.Equal("registry-write-failed", result.ErrorCode);
        // 置顶失败后不应广播。
        Assert.Equal(0, helper.BroadcastCalls);
    }

    [Fact]
    public async Task PrependPathEntriesSurfacesPrependedFromDetails()
    {
        var helper = new FakeEnvironmentHelperClient
        {
            PrependResponse = OkResponse("{\"prepended\":[\"C:\\\\DevSwitch\\\\current\\\\java\\\\bin\"],\"count\":1,\"changed\":true}"),
        };
        var service = new EnvironmentService(helper);

        var result = await service.PrependPathEntriesAsync(new[] { @"C:\DevSwitch\current\java\bin" }, broadcast: false);

        Assert.True(result.Success);
        Assert.Single(result.AddedPathEntries);
        Assert.Equal(1, helper.PrependCalls.Count);
    }

    [Fact]
    public async Task InitializeDoesNotBroadcastWhenDisabled()
    {
        var helper = new FakeEnvironmentHelperClient();
        var service = new EnvironmentService(helper);

        await service.InitializeEnvironmentAsync(@"%LOCALAPPDATA%\DevSwitch", broadcast: false);

        Assert.Equal(0, helper.BroadcastCalls);
    }

    [Fact]
    public async Task InitializeStopsAndReportsWhenWriteFails()
    {
        var helper = new FakeEnvironmentHelperClient
        {
            WriteResponse = FailResponse("registry-write-failed", "boom"),
        };
        var service = new EnvironmentService(helper);

        var result = await service.InitializeEnvironmentAsync(@"%LOCALAPPDATA%\DevSwitch");

        Assert.False(result.Success);
        Assert.Equal("registry-write-failed", result.ErrorCode);
        // 写变量失败后不应继续追加 PATH 或广播。
        Assert.Equal(0, helper.AppendCalls.Count);
        Assert.Equal(0, helper.BroadcastCalls);
    }

    [Fact]
    public async Task InitializeRejectsEmptyHome()
    {
        var helper = new FakeEnvironmentHelperClient();
        var service = new EnvironmentService(helper);

        var result = await service.InitializeEnvironmentAsync("   ");

        Assert.False(result.Success);
        Assert.Equal("invalid-request", result.ErrorCode);
        Assert.Equal(0, helper.WriteCalls.Count);
    }

    [Fact]
    public async Task WriteVariablesSurfacesAddedNamesFromDetails()
    {
        var helper = new FakeEnvironmentHelperClient
        {
            WriteResponse = OkResponse("{\"written\":[\"JAVA_HOME\",\"GOROOT\"],\"count\":2}"),
        };
        var service = new EnvironmentService(helper);

        var result = await service.WriteVariablesAsync(
            new[] { new EnvironmentVariable("JAVA_HOME", "x"), new EnvironmentVariable("GOROOT", "y") },
            broadcast: false);

        Assert.True(result.Success);
        Assert.Equal(new[] { "JAVA_HOME", "GOROOT" }, result.WrittenVariables);
    }

    [Fact]
    public async Task AppendPathEntriesSurfacesAddedFromDetails()
    {
        var helper = new FakeEnvironmentHelperClient
        {
            AppendResponse = OkResponse("{\"added\":[\"%DEVSWITCH_HOME%\\\\current\\\\go\\\\bin\"],\"count\":1,\"changed\":true}"),
        };
        var service = new EnvironmentService(helper);

        var result = await service.AppendPathEntriesAsync(new[] { @"%DEVSWITCH_HOME%\current\go\bin" }, broadcast: false);

        Assert.True(result.Success);
        Assert.Single(result.AddedPathEntries);
    }

    [Fact]
    public async Task ResetRemovesManagedPathEntriesAndBroadcasts()
    {
        var helper = new FakeEnvironmentHelperClient
        {
            RemoveResponse = OkResponse("{\"removed\":[\"%DEVSWITCH_HOME%\\\\current\\\\java\\\\bin\"],\"count\":1,\"changed\":true}"),
        };
        var service = new EnvironmentService(helper);

        var result = await service.ResetEnvironmentAsync();

        Assert.True(result.Success);
        Assert.Equal(1, helper.RemoveCalls.Count);
        // 重置时移除的是托管片段集合。
        Assert.Equal(5, helper.RemoveCalls[0].Count);
        Assert.Single(result.RemovedPathEntries);
        Assert.Equal(1, helper.BroadcastCalls);
    }

    [Fact]
    public async Task PrependMachinePathSurfacesPrependedFromDetails()
    {
        var helper = new FakeEnvironmentHelperClient
        {
            PrependMachineResponse = OkResponse("{\"prepended\":[\"D:\\\\data\\\\current\\\\java\\\\bin\"],\"count\":1,\"changed\":true}"),
        };
        var service = new EnvironmentService(helper);

        var result = await service.PrependMachinePathEntriesAsync(new[] { @"D:\data\current\java\bin" }, broadcast: false);

        Assert.True(result.Success);
        Assert.Equal(1, helper.PrependMachineCalls.Count);
        Assert.Single(result.AddedPathEntries);
    }

    [Fact]
    public async Task PrependMachinePathSurfacesAccessDeniedWhenNotElevated()
    {
        var helper = new FakeEnvironmentHelperClient
        {
            PrependMachineResponse = FailResponse("registry-access-denied", "failed to open machine environment key"),
        };
        var service = new EnvironmentService(helper);

        var result = await service.PrependMachinePathEntriesAsync(new[] { @"D:\data\current\java\bin" }, broadcast: false);

        Assert.False(result.Success);
        Assert.Equal("registry-access-denied", result.ErrorCode);
    }

    [Fact]
    public async Task RebuildShimsForwardsDataRootAndShimSource()
    {
        var helper = new FakeEnvironmentHelperClient
        {
            RebuildShimsResponse = OkResponse("{\"created\":[\"java\",\"mvn\"],\"createdCount\":2,\"removed\":[],\"removedCount\":0}"),
        };
        var service = new EnvironmentService(helper);

        var result = await service.RebuildShimsAsync(@"D:\data", @"D:\app\DevSwitch.Shim.exe");

        Assert.True(result.Success);
        Assert.Single(helper.RebuildShimsCalls);
        Assert.Equal(@"D:\data", helper.RebuildShimsCalls[0].DataRoot);
        Assert.Equal(@"D:\app\DevSwitch.Shim.exe", helper.RebuildShimsCalls[0].ShimSourcePath);
        Assert.Equal(2, result.AddedPathEntries.Count);
    }

    [Fact]
    public async Task RebuildShimsRejectsEmptyArguments()
    {
        var service = new EnvironmentService(new FakeEnvironmentHelperClient());

        var result = await service.RebuildShimsAsync("  ", "x");

        Assert.False(result.Success);
        Assert.Equal("invalid-request", result.ErrorCode);
    }

    [Fact]
    public async Task BroadcastFailurePropagates()
    {
        var helper = new FakeEnvironmentHelperClient
        {
            BroadcastResponse = FailResponse("broadcast-failed", "hung"),
        };
        var service = new EnvironmentService(helper);

        var result = await service.BroadcastAsync();

        Assert.False(result.Success);
        Assert.Equal("broadcast-failed", result.ErrorCode);
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

    /// <summary>
    /// 记录调用并返回可配置响应的 fake 用户环境 helper 客户端。
    /// </summary>
    private sealed class FakeEnvironmentHelperClient : IEnvironmentHelperClient
    {
        public List<IReadOnlyList<EnvironmentVariable>> WriteCalls { get; } = new();

        public List<IReadOnlyList<string>> AppendCalls { get; } = new();

        public List<IReadOnlyList<string>> PrependCalls { get; } = new();

        public List<IReadOnlyList<string>> PrependMachineCalls { get; } = new();

        public List<IReadOnlyList<string>> RemoveCalls { get; } = new();

        public List<IReadOnlyList<string>> RemoveMachineCalls { get; } = new();

        public List<IReadOnlyList<string>> ReadCalls { get; } = new();

        public int BroadcastCalls { get; private set; }

        public HelperResponse? WriteResponse { get; set; }

        public HelperResponse? AppendResponse { get; set; }

        public HelperResponse? PrependResponse { get; set; }

        public HelperResponse? PrependMachineResponse { get; set; }

        public HelperResponse? RemoveResponse { get; set; }

        public HelperResponse? RemoveMachineResponse { get; set; }

        public HelperResponse? ReadResponse { get; set; }

        public HelperResponse? BroadcastResponse { get; set; }

        public Task<HelperResponse> WriteUserEnvironmentAsync(IReadOnlyList<EnvironmentVariable> variables, CancellationToken cancellationToken = default)
        {
            WriteCalls.Add(variables);
            return Task.FromResult(WriteResponse ?? DefaultOk("written"));
        }

        public Task<HelperResponse> AppendManagedPathEntriesAsync(IReadOnlyList<string> entries, CancellationToken cancellationToken = default)
        {
            AppendCalls.Add(entries);
            return Task.FromResult(AppendResponse ?? DefaultOk("added"));
        }

        public Task<HelperResponse> PrependManagedPathEntriesAsync(IReadOnlyList<string> entries, CancellationToken cancellationToken = default)
        {
            PrependCalls.Add(entries);
            return Task.FromResult(PrependResponse ?? DefaultOk("prepended"));
        }

        public Task<HelperResponse> RemoveManagedPathEntriesAsync(IReadOnlyList<string> entries, CancellationToken cancellationToken = default)
        {
            RemoveCalls.Add(entries);
            return Task.FromResult(RemoveResponse ?? DefaultOk("removed"));
        }

        public Task<HelperResponse> PrependMachinePathEntriesAsync(IReadOnlyList<string> entries, CancellationToken cancellationToken = default)
        {
            PrependMachineCalls.Add(entries);
            return Task.FromResult(PrependMachineResponse ?? DefaultOk("prepended"));
        }

        public Task<HelperResponse> RemoveMachinePathEntriesAsync(IReadOnlyList<string> entries, CancellationToken cancellationToken = default)
        {
            RemoveMachineCalls.Add(entries);
            return Task.FromResult(RemoveMachineResponse ?? DefaultOk("removed"));
        }

        public List<(string DataRoot, string ShimSourcePath)> RebuildShimsCalls { get; } = new();

        public HelperResponse? RebuildShimsResponse { get; set; }

        public Task<HelperResponse> RebuildShimsAsync(string dataRoot, string shimSourcePath, CancellationToken cancellationToken = default)
        {
            RebuildShimsCalls.Add((dataRoot, shimSourcePath));
            return Task.FromResult(RebuildShimsResponse ?? DefaultOk("created"));
        }

        public Task<HelperResponse> ReadUserEnvironmentAsync(IReadOnlyList<string> names, CancellationToken cancellationToken = default)
        {
            ReadCalls.Add(names);
            return Task.FromResult(ReadResponse ?? DefaultOk("values"));
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
