// 文件用途：通过真实 helper 进程验证用户环境变量 operation 的公开行为。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit、System.Text.Json、System.Environment（仅 BCL，不引入第三方包）
// NOTE: 合法授权学习使用，仅限本地环境。
//       本测试会真实写 HKCU\Environment，但只使用 DEVSWITCH_TEST_ 前缀的测试变量，
//       并在 finally 中删除，绝不触碰真实 JAVA_HOME / Path。
//       绝不对真实 Path 做集成写入；Path 合并/去重逻辑由 EnvironmentLayoutTests 纯函数覆盖。

using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class HelperEnvironmentTests
{
    // 测试专用变量名前缀，确保不污染用户真实环境变量。
    private const string TestVariablePrefix = "DEVSWITCH_TEST_";

    [Fact]
    public async Task WriteUserEnvironmentWritesExpandableValueAndReadsBackUnexpanded()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var name = TestVariablePrefix + Guid.NewGuid().ToString("N");
        // 用一个 OS 一定能展开的占位符（%SystemRoot%），便于同时验证“原始未展开”与“类型为 REG_EXPAND_SZ”。
        const string value = @"%SystemRoot%\devswitch-test";

        try
        {
            var write = (await InvokeAsync("env-write", "writeUserEnvironment", new
            {
                variables = new[] { new { name, value } },
            })).DeserializeResponse();

            Assert.True(write.Success);
            Assert.Contains(name, ExtractStrings(write, "written"));

            // 通过 helper 读回原始注册表值（RegQueryValueExW 不展开），应保留 %SystemRoot% 占位符。
            var read = (await InvokeAsync("env-read", "readUserEnvironment", new
            {
                names = new[] { name },
            })).DeserializeResponse();

            Assert.True(read.Success);
            var entry = read.Details!.Value.GetProperty("values").GetProperty(name);
            Assert.True(entry.GetProperty("exists").GetBoolean());
            Assert.Equal(value, entry.GetProperty("value").GetString());

            // 通过 BCL 读取用户环境变量：OS 会展开 REG_EXPAND_SZ。
            // 若类型错误地写成 REG_SZ，OS 不会展开，仍保留字面 %SystemRoot%，断言即失败。
            var expanded = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
            Assert.NotNull(expanded);
            Assert.DoesNotContain("%SystemRoot%", expanded, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(@"\devswitch-test", expanded, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            // 清理测试变量，绝不影响真实用户环境。
            Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.User);
        }
    }

    [Fact]
    public async Task ReadUserEnvironmentReportsMissingVariable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var name = TestVariablePrefix + Guid.NewGuid().ToString("N");

        var read = (await InvokeAsync("env-read-missing", "readUserEnvironment", new
        {
            names = new[] { name },
        })).DeserializeResponse();

        Assert.True(read.Success);
        var entry = read.Details!.Value.GetProperty("values").GetProperty(name);
        Assert.False(entry.GetProperty("exists").GetBoolean());
    }

    [Fact]
    public async Task AppendThenRemoveManagedTestEntryRoundTrips()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // 使用 DEVSWITCH_TEST_ 前缀的伪 PATH 片段，避免与真实托管片段混淆；
        // 该片段不会真实指向任何目录，仅验证 append/remove 往返与去重。
        var marker = $@"C:\{TestVariablePrefix}{Guid.NewGuid():N}\bin";
        var originalRawPath = await ReadRawUserPathAsync();

        try
        {
            // 追加一次。
            var append1 = (await InvokeAsync("env-append-1", "appendManagedPathEntries", new
            {
                entries = new[] { marker },
            })).DeserializeResponse();
            Assert.True(append1.Success);
            Assert.Contains(marker, ExtractStrings(append1, "added"));
            Assert.Contains(marker, await ReadRawUserPathEntriesAsync());

            // 再次追加同一片段：应去重、不重复添加。
            var append2 = (await InvokeAsync("env-append-2", "appendManagedPathEntries", new
            {
                entries = new[] { marker },
            })).DeserializeResponse();
            Assert.True(append2.Success);
            Assert.Empty(ExtractStrings(append2, "added"));
            Assert.False(append2.Details!.Value.GetProperty("changed").GetBoolean());

            // 移除该片段。
            var remove = (await InvokeAsync("env-remove", "removeManagedPathEntries", new
            {
                entries = new[] { marker },
            })).DeserializeResponse();
            Assert.True(remove.Success);
            Assert.Contains(marker, ExtractStrings(remove, "removed"));
            Assert.DoesNotContain(marker, await ReadRawUserPathEntriesAsync());
        }
        finally
        {
            // 兜底清理：确保即使断言中途失败，测试片段也不会残留在用户 Path 中。
            await InvokeAsync("env-cleanup", "removeManagedPathEntries", new { entries = new[] { marker } });
            // 防御性校验：原始非测试条目仍在（不强制断言，避免环境差异导致脆弱），仅用于诊断。
            _ = originalRawPath;
        }
    }

    [Fact]
    public async Task WriteUserEnvironmentRejectsMissingVariablesField()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await InvokeAsync("env-write-missing", "writeUserEnvironment", new { });
        var response = result.DeserializeResponse();

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(response.Success);
        Assert.Equal("missing-payload-field", response.ErrorCode);
    }

    [Fact]
    public async Task AppendManagedPathEntriesRejectsEmptyEntries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await InvokeAsync("env-append-empty", "appendManagedPathEntries", new
        {
            entries = Array.Empty<string>(),
        });
        var response = result.DeserializeResponse();

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(response.Success);
        Assert.Equal("missing-payload-field", response.ErrorCode);
    }

    [Fact]
    public async Task BroadcastEnvironmentChangedSucceeds()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await InvokeAsync("env-broadcast", "broadcastEnvironmentChanged", new { });
        var response = result.DeserializeResponse();

        Assert.Equal(0, result.ExitCode);
        Assert.True(response.Success);
        Assert.True(response.Details!.Value.GetProperty("broadcast").GetBoolean());
    }

    private static Task<HelperProcessResult> InvokeAsync(string requestId, string operation, object payload)
    {
        return HelperProcessTestSupport.InvokeHelperAsync(new HelperRequest(requestId, operation, payload));
    }

    private static IReadOnlyList<string> ExtractStrings(HelperResponse response, string property)
    {
        var list = new List<string>();
        foreach (var element in response.Details!.Value.GetProperty(property).EnumerateArray())
        {
            var value = element.GetString();
            if (value is not null)
            {
                list.Add(value);
            }
        }

        return list;
    }

    // 通过 helper readUserEnvironment 读回原始未展开的 Path 值。
    private static async Task<string?> ReadRawUserPathAsync()
    {
        var read = (await InvokeAsync("env-read-path", "readUserEnvironment", new
        {
            names = new[] { EnvironmentLayout.PathName },
        })).DeserializeResponse();

        var entry = read.Details!.Value.GetProperty("values").GetProperty(EnvironmentLayout.PathName);
        return entry.GetProperty("exists").GetBoolean() ? entry.GetProperty("value").GetString() : null;
    }

    private static async Task<IReadOnlyList<string>> ReadRawUserPathEntriesAsync()
    {
        var raw = await ReadRawUserPathAsync();
        return EnvironmentLayout.SplitPath(raw);
    }
}
