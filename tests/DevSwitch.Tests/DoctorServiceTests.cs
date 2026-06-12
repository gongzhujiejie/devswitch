// 文件用途：用 fake 抽象驱动 DoctorService 的 9 项检查，验证 Pass/Info/Warning/Error/Fatal 分级与汇总。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit
// NOTE: 合法授权学习使用，仅限本地环境。
//       本测试不读注册表、不跑真实命令、不联网，全部通过注入 fake 抽象实现。

using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class DoctorServiceTests
{
    [Fact]
    public async Task RunAsyncReturnsAllNineChecks()
    {
        var dataRoot = CreateWritableDataRoot();
        var service = CreateHealthyService(dataRoot);

        var report = await service.RunAsync();

        // 9 项检查应各产出一条结果。
        Assert.Equal(9, report.Results.Count);
    }

    [Fact]
    public async Task RunAsyncReportsPassWhenEverythingHealthy()
    {
        var dataRoot = CreateWritableDataRoot();
        var service = CreateHealthyService(dataRoot);

        var report = await service.RunAsync();

        Assert.Equal(DiagnosticSeverity.Pass, report.OverallSeverity);
        Assert.Equal(9, report.CountOf(DiagnosticSeverity.Pass));
    }

    [Fact]
    public async Task DataRootWritableCheckReportsErrorWhenDirectoryMissing()
    {
        // 指向不存在的目录，可写性检查应为 Error。
        var dataRoot = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"), "missing");
        var env = HealthyEnvironment(Path.Combine(dataRoot, "..", "real"));
        var service = new DoctorService(
            dataRoot,
            new SdkCatalogStore(),
            env,
            new FakeCurrentLinkInspector(healthy: true),
            new FakeCommandRunner(succeed: true),
            new FakeHelperPing(available: true));

        var report = await service.RunAsync();
        var result = Single(report, "data-root-writable");

        Assert.Equal(DiagnosticSeverity.Error, result.Severity);
    }

    [Fact]
    public async Task EnvironmentCheckReportsWarningWhenSomeVariablesMissing()
    {
        var dataRoot = CreateWritableDataRoot();
        // 只提供部分变量。
        var env = new FakeEnvironmentReader(dataRoot)
        {
            Variables =
            {
                ["DEVSWITCH_HOME"] = dataRoot,
                ["JAVA_HOME"] = Path.Combine(dataRoot, "current", "java"),
                // 缺 MAVEN_HOME、GOROOT
            },
        };
        env.SetManagedPathPresent(dataRoot);

        var service = CreateService(dataRoot, env);

        var report = await service.RunAsync();
        var result = Single(report, "hkcu-environment");

        Assert.Equal(DiagnosticSeverity.Warning, result.Severity);
        Assert.Contains("MAVEN_HOME", result.Detail);
    }

    [Fact]
    public async Task PathConflictCheckReportsWarningWithManualSuggestion()
    {
        var dataRoot = CreateWritableDataRoot();
        var env = HealthyEnvironment(dataRoot);
        // 在托管 shims 之前插入 Oracle javapath（会遮蔽 shims 转发的 java）。
        env.PathEntries =
        [
            @"C:\Program Files\Common Files\Oracle\Java\javapath",
            Path.Combine(dataRoot, "shims"),
        ];

        var service = CreateService(dataRoot, env);

        var report = await service.RunAsync();
        var result = Single(report, "path-conflict");

        Assert.Equal(DiagnosticSeverity.Warning, result.Severity);
        Assert.NotNull(result.Suggestion);
        Assert.Contains("手动", result.Suggestion!);
    }

    [Fact]
    public async Task PathConflictCheckReportsMachinePathWarningBeforeUserManaged()
    {
        var dataRoot = CreateWritableDataRoot();
        var env = HealthyEnvironment(dataRoot);
        // 系统 PATH 在用户 PATH 前；即使用户 PATH 已把 DevSwitch 片段置顶，系统旧 JDK 仍会先命中。
        env.MachinePathEntries = new List<string>
        {
            @"D:\Programs\java\jdk\Java_8_win\bin",
            @"C:\Windows\System32",
        };

        var service = CreateService(dataRoot, env);

        var report = await service.RunAsync();
        var result = Single(report, "path-conflict");

        Assert.Equal(DiagnosticSeverity.Warning, result.Severity);
        Assert.Contains("系统 PATH", result.Suggestion!);
        Assert.Contains("Java_8_win", result.Detail);
    }

    [Fact]
    public async Task ManagedPathCheckPassesWhenShimsDirectoryPresent()
    {
        // 场景 a：现代 shim 单目录方案——PATH 含唯一 dataRoot\shims 即视为通过。
        var dataRoot = CreateWritableDataRoot();
        var env = HealthyEnvironment(dataRoot);
        env.PathEntries =
        [
            Path.Combine(dataRoot, "shims"),
        ];

        var service = CreateService(dataRoot, env);

        var report = await service.RunAsync();
        var result = Single(report, "managed-path-segments");

        Assert.Equal(DiagnosticSeverity.Pass, result.Severity);
    }

    [Fact]
    public async Task ManagedPathCheckDoesNotErrorWhenLegacyPerTypeEntriesPresent()
    {
        // 场景 b：老式逐类型片段（current\<type>\bin，无 shims 目录）——不应判定为 Error。
        var dataRoot = CreateWritableDataRoot();
        var env = HealthyEnvironment(dataRoot);
        env.PathEntries =
        [
            Path.Combine(dataRoot, "current", "java", "bin"),
            Path.Combine(dataRoot, "current", "maven", "bin"),
            Path.Combine(dataRoot, "current", "node"),
        ];

        var service = CreateService(dataRoot, env);

        var report = await service.RunAsync();
        var result = Single(report, "managed-path-segments");

        Assert.NotEqual(DiagnosticSeverity.Error, result.Severity);
    }

    [Fact]
    public async Task ManagedPathCheckReportsErrorWhenNoDevSwitchEntryPresent()
    {
        // 场景 c：PATH 完全不含任何 DevSwitch 目录——保持真·缺失的 Error。
        var dataRoot = CreateWritableDataRoot();
        var env = HealthyEnvironment(dataRoot);
        env.PathEntries =
        [
            @"C:\Windows\System32",
            @"C:\Program Files\Git\cmd",
        ];

        var service = CreateService(dataRoot, env);

        var report = await service.RunAsync();
        var result = Single(report, "managed-path-segments");

        Assert.Equal(DiagnosticSeverity.Error, result.Severity);
    }

    [Fact]
    public async Task ManagedPathCheckIgnoresCaseAndTrailingSeparatorDifferences()
    {
        // 场景 d：大小写 / 结尾反斜杠 / 正斜杠差异不应影响识别。
        var dataRoot = CreateWritableDataRoot();
        var env = HealthyEnvironment(dataRoot);
        var shims = Path.Combine(dataRoot, "shims");
        env.PathEntries =
        [
            shims.ToUpperInvariant() + "\\",
        ];

        var service = CreateService(dataRoot, env);

        var report = await service.RunAsync();
        var result = Single(report, "managed-path-segments");

        Assert.Equal(DiagnosticSeverity.Pass, result.Severity);
    }

    [Fact]
    public async Task CurrentLinkCheckReportsErrorWhenLinkBroken()
    {
        var dataRoot = CreateWritableDataRoot();
        var env = HealthyEnvironment(dataRoot);
        var service = new DoctorService(
            dataRoot,
            new SdkCatalogStore(),
            env,
            new FakeCurrentLinkInspector(healthy: false) { ExistsButBroken = true },
            new FakeCommandRunner(succeed: true),
            new FakeHelperPing(available: true));

        var report = await service.RunAsync();
        var result = Single(report, "current-link-integrity");

        Assert.Equal(DiagnosticSeverity.Error, result.Severity);
    }

    [Fact]
    public async Task CommandVersionCheckReportsWarningWhenTimedOut()
    {
        var dataRoot = CreateWritableDataRoot();
        var env = HealthyEnvironment(dataRoot);
        var service = new DoctorService(
            dataRoot,
            new SdkCatalogStore(),
            env,
            new FakeCurrentLinkInspector(healthy: true),
            new FakeCommandRunner(succeed: true) { TimeOutAll = true },
            new FakeHelperPing(available: true));

        var report = await service.RunAsync();
        var result = Single(report, "command-version");

        Assert.Equal(DiagnosticSeverity.Warning, result.Severity);
    }

    [Fact]
    public async Task HelperCheckReportsFatalWhenUnavailable()
    {
        var dataRoot = CreateWritableDataRoot();
        var env = HealthyEnvironment(dataRoot);
        var service = new DoctorService(
            dataRoot,
            new SdkCatalogStore(),
            env,
            new FakeCurrentLinkInspector(healthy: true),
            new FakeCommandRunner(succeed: true),
            new FakeHelperPing(available: false));

        var report = await service.RunAsync();
        var result = Single(report, "helper-availability");

        Assert.Equal(DiagnosticSeverity.Fatal, result.Severity);
        // 整体等级应被 Fatal 抬高。
        Assert.Equal(DiagnosticSeverity.Fatal, report.OverallSeverity);
    }

    [Fact]
    public async Task SchemaVersionCheckReportsFatalWhenCatalogNewerThanSupported()
    {
        var dataRoot = CreateWritableDataRoot();
        // 写入 schemaVersion=2 的 sdks.json，但服务只支持 1。
        await new SdkCatalogStore().SaveAsync(dataRoot, new SdkCatalog(2, ActiveSdkSet.Empty, Array.Empty<SdkRecord>()));
        var env = HealthyEnvironment(dataRoot);
        var service = new DoctorService(
            dataRoot,
            new SdkCatalogStore(),
            env,
            new FakeCurrentLinkInspector(healthy: true),
            new FakeCommandRunner(succeed: true),
            new FakeHelperPing(available: true),
            supportedSchemaVersion: 1);

        var report = await service.RunAsync();
        var result = Single(report, "config-schema-version");

        Assert.Equal(DiagnosticSeverity.Fatal, result.Severity);
    }

    [Fact]
    public async Task CheckExceptionIsDegradedToErrorNotThrown()
    {
        var dataRoot = CreateWritableDataRoot();
        var env = HealthyEnvironment(dataRoot);
        // helper ping 抛异常，应被降级为 Error 而非冒泡。
        var service = new DoctorService(
            dataRoot,
            new SdkCatalogStore(),
            env,
            new FakeCurrentLinkInspector(healthy: true),
            new FakeCommandRunner(succeed: true),
            new ThrowingHelperPing());

        var report = await service.RunAsync();
        var result = Single(report, "helper-availability");

        Assert.Equal(DiagnosticSeverity.Error, result.Severity);
        Assert.Contains("检查执行异常", result.Detail);
    }

    [Fact]
    public async Task RunAsyncHonorsCancellation()
    {
        var dataRoot = CreateWritableDataRoot();
        var env = HealthyEnvironment(dataRoot);
        var service = CreateService(dataRoot, env);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync(cts.Token));
    }

    // ---- 测试基础设施 ----

    private static DoctorService CreateHealthyService(string dataRoot)
        => CreateService(dataRoot, HealthyEnvironment(dataRoot));

    private static DoctorService CreateService(string dataRoot, FakeEnvironmentReader env)
        => new(
            dataRoot,
            new SdkCatalogStore(),
            env,
            new FakeCurrentLinkInspector(healthy: true),
            new FakeCommandRunner(succeed: true),
            new FakeHelperPing(available: true));

    private static FakeEnvironmentReader HealthyEnvironment(string dataRoot)
    {
        var env = new FakeEnvironmentReader(dataRoot);
        var currentRoot = Path.Combine(dataRoot, "current");
        env.Variables["DEVSWITCH_HOME"] = dataRoot;
        env.Variables["JAVA_HOME"] = Path.Combine(currentRoot, "java");
        env.Variables["MAVEN_HOME"] = Path.Combine(currentRoot, "maven");
        env.Variables["GOROOT"] = Path.Combine(currentRoot, "go");
        env.SetManagedPathPresent(dataRoot);
        return env;
    }

    private static DiagnosticResult Single(DoctorReport report, string id)
        => report.Results.Single(r => r.Id == id);

    private static string CreateWritableDataRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeEnvironmentReader : IEnvironmentReader
    {
        public Dictionary<string, string?> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> PathEntries { get; set; } = new();

        public List<string> MachinePathEntries { get; set; } = new();

        public FakeEnvironmentReader(string dataRoot)
        {
            _ = dataRoot;
        }

        /// <summary>
        /// 设置 PATH 仅包含 DevSwitch 托管片段（shim 单目录方案：唯一 shims 目录即健康状态）。
        /// </summary>
        public void SetManagedPathPresent(string dataRoot)
        {
            PathEntries = new List<string>
            {
                Path.Combine(dataRoot, "shims"),
            };
        }

        public string? GetVariable(string name)
            => Variables.TryGetValue(name, out var value) ? value : null;

        public IReadOnlyList<string> GetPathEntries() => PathEntries;

        public IReadOnlyList<string> GetMachinePathEntries() => MachinePathEntries;
    }

    private sealed class FakeCurrentLinkInspector : ICurrentLinkInspector
    {
        private readonly bool healthy;

        public bool ExistsButBroken { get; init; }

        public FakeCurrentLinkInspector(bool healthy) => this.healthy = healthy;

        public Task<CurrentLinkInspection> InspectAsync(SdkType sdkType, string currentPath, CancellationToken cancellationToken = default)
        {
            if (healthy)
            {
                return Task.FromResult(new CurrentLinkInspection(Exists: true, TargetPath: currentPath + "-target", TargetExists: true));
            }

            if (ExistsButBroken)
            {
                // 链接存在但目标不存在 -> 不健康。
                return Task.FromResult(new CurrentLinkInspection(Exists: true, TargetPath: currentPath + "-target", TargetExists: false));
            }

            // 链接不存在。
            return Task.FromResult(new CurrentLinkInspection(Exists: false, TargetPath: null, TargetExists: false));
        }
    }

    private sealed class FakeCommandRunner : ICommandRunner
    {
        private readonly bool succeed;

        public bool TimeOutAll { get; init; }

        public FakeCommandRunner(bool succeed) => this.succeed = succeed;

        public Task<CommandResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken = default)
        {
            if (TimeOutAll)
            {
                return Task.FromResult(new CommandResult(Started: true, ExitCode: null, string.Empty, string.Empty, TimedOut: true));
            }

            if (fileName == "npm")
            {
                // npm prefix 返回 DevSwitch 范围内目录，保持健康。
                return Task.FromResult(new CommandResult(true, 0, "C:/data/DevSwitch/current/node", string.Empty, false));
            }

            return succeed
                ? Task.FromResult(new CommandResult(true, 0, $"{fileName} version 1.0", string.Empty, false))
                : Task.FromResult(new CommandResult(false, null, string.Empty, "not found", false));
        }
    }

    private sealed class FakeHelperPing : IHelperPing
    {
        private readonly bool available;

        public FakeHelperPing(bool available) => this.available = available;

        public Task<bool> PingAsync(CancellationToken cancellationToken = default) => Task.FromResult(available);
    }

    private sealed class ThrowingHelperPing : IHelperPing
    {
        public Task<bool> PingAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("ping failed");
    }
}
