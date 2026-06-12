// 文件用途：实现 DevSwitch「重置工具环境」能力。
//           恢复 DevSwitch 自身管理的：托管 PATH 片段、托管环境变量、current 入口链接、
//           以及 catalog.active 当前使用状态；默认不删除任何 SDK 实体（外部/托管都不删）。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.Text.Json
// NOTE: 合法授权学习使用，仅限本地环境。所有副作用走可注入抽象，单测不改注册表、不删链接。

using System.Text.Json;

namespace DevSwitch.Core;

/// <summary>
/// current 入口链接移除客户端抽象（封装 helper 的 removeCurrentLink 操作）。
/// </summary>
/// <remarks>
/// 命名刻意区别于 DoctorModels / SdkVerificationModels 的 ICurrentLinkInspector，
/// 仅负责删除 junction/symlink 链接本身，绝不触碰链接目标指向的真实 SDK 目录。
/// </remarks>
public interface ISdkDeletionLinkClient
{
    /// <summary>
    /// 移除指定 current 入口链接（只删 junction/symlink，不删目标）。
    /// </summary>
    /// <param name="currentPath">current 入口路径，例如 dataRoot\current\java。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<HelperResponse> RemoveCurrentLinkAsync(string currentPath, CancellationToken cancellationToken = default);
}

/// <summary>
/// 通过真实 helper 进程执行 removeCurrentLink 的客户端（一次请求一进程）。
/// </summary>
public sealed class ProcessSdkDeletionLinkClient : ISdkDeletionLinkClient
{
    private readonly string helperPath;
    private readonly JsonSerializerOptions serializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 创建 link 客户端。
    /// </summary>
    /// <param name="helperPath">DevSwitch.Helper.exe 路径。</param>
    public ProcessSdkDeletionLinkClient(string helperPath)
    {
        if (string.IsNullOrWhiteSpace(helperPath))
        {
            throw new ArgumentException("Helper path is required.", nameof(helperPath));
        }

        this.helperPath = helperPath;
    }

    /// <inheritdoc />
    public async Task<HelperResponse> RemoveCurrentLinkAsync(string currentPath, CancellationToken cancellationToken = default)
    {
        // NOTE: helper 一次请求一进程，避免长期驻留进程积累状态。
        var payload = new HelperLinkPayload(SdkType: null, CurrentPath: currentPath, TargetPath: null, LinkPreference: null);
        var request = new HelperRequest(Guid.NewGuid().ToString("N"), "removeCurrentLink", payload);

        var startInfo = new System.Diagnostics.ProcessStartInfo(helperPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start DevSwitch helper.");
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request, serializerOptions));
        await process.StandardInput.FlushAsync(cancellationToken);
        process.StandardInput.Close();

        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        string error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return JsonSerializer.Deserialize<HelperResponse>(output, serializerOptions)
            ?? throw new InvalidDataException($"Helper returned invalid JSON. stderr: {error}");
    }
}

/// <summary>
/// 重置选项：控制要移除哪些托管变量。
/// </summary>
/// <param name="ClearJdkHome">是否清除兼容变量 JDK_HOME（初始化时可选写入，故重置可选清除）。</param>
/// <param name="ClearM2Home">是否清除兼容变量 M2_HOME。</param>
/// <param name="Broadcast">完成后是否广播 WM_SETTINGCHANGE。</param>
public sealed record EnvironmentResetOptions(
    bool ClearJdkHome = false,
    bool ClearM2Home = false,
    bool Broadcast = true)
{
    /// <summary>
    /// 默认重置选项（仅清核心变量、广播）。
    /// </summary>
    public static EnvironmentResetOptions Default { get; } = new();
}

/// <summary>
/// 工具环境重置结果。
/// </summary>
/// <param name="Success">是否成功完成重置。</param>
/// <param name="RemovedVariables">本次清除的托管环境变量名。</param>
/// <param name="RemovedPathEntries">本次移除的托管 PATH 片段。</param>
/// <param name="RemovedCurrentLinks">本次移除的 current 入口路径。</param>
/// <param name="ActiveCleared">是否清空了 catalog.active 当前使用状态。</param>
/// <param name="ErrorCode">失败时的稳定错误码；成功时为空。</param>
/// <param name="Message">面向 UI/日志的简短说明。</param>
public sealed record EnvironmentResetResult(
    bool Success,
    IReadOnlyList<string> RemovedVariables,
    IReadOnlyList<string> RemovedPathEntries,
    IReadOnlyList<string> RemovedCurrentLinks,
    bool ActiveCleared,
    string? ErrorCode,
    string Message)
{
    /// <summary>
    /// 构造失败结果。
    /// </summary>
    public static EnvironmentResetResult Fail(string errorCode, string message)
    {
        return new EnvironmentResetResult(
            false,
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            false,
            errorCode,
            message);
    }
}

/// <summary>
/// 工具环境重置服务：清理 DevSwitch 自身管理的环境状态，但绝不删 SDK 实体。
/// </summary>
/// <remarks>
/// 安全红线（requirements.md 第 13 节 / CONTEXT.md）：
/// - 只移除托管 PATH 片段，保留用户其它 PATH 项（依赖 EnvironmentLayout 托管片段集合 + helper 完全匹配移除）。
/// - 只清除 DevSwitch 管理的变量：DEVSWITCH_HOME/JAVA_HOME/MAVEN_HOME/GOROOT 及可选 JDK_HOME/M2_HOME。
/// - 只删 current junction/symlink，不碰链接目标。
/// - 绝不删除任何 SDK 实体（外部与托管都不删）；不动 settings.xml / npm 全局包 / GOPATH。
/// </remarks>
public sealed class EnvironmentResetService
{
    private readonly EnvironmentService environmentService;
    private readonly IEnvironmentHelperClient environmentHelperClient;
    private readonly ISdkDeletionLinkClient linkClient;
    private readonly SdkCatalogStore catalogStore;
    private readonly ISdkCurrentPathProvider currentPathProvider;

    /// <summary>
    /// 创建工具环境重置服务。
    /// </summary>
    /// <param name="environmentService">环境变量/PATH 高层服务（复用其托管 PATH 移除/广播）。</param>
    /// <param name="environmentHelperClient">环境 helper 客户端（用于清除托管变量）。</param>
    /// <param name="linkClient">current 链接移除客户端。</param>
    /// <param name="catalogStore">SDK 目录读写（清空 active）。</param>
    /// <param name="currentPathProvider">current 路径计算器，默认 <see cref="SdkCurrentPathProvider"/>。</param>
    public EnvironmentResetService(
        EnvironmentService environmentService,
        IEnvironmentHelperClient environmentHelperClient,
        ISdkDeletionLinkClient linkClient,
        SdkCatalogStore catalogStore,
        ISdkCurrentPathProvider? currentPathProvider = null)
    {
        this.environmentService = environmentService ?? throw new ArgumentNullException(nameof(environmentService));
        this.environmentHelperClient = environmentHelperClient ?? throw new ArgumentNullException(nameof(environmentHelperClient));
        this.linkClient = linkClient ?? throw new ArgumentNullException(nameof(linkClient));
        this.catalogStore = catalogStore ?? throw new ArgumentNullException(nameof(catalogStore));
        this.currentPathProvider = currentPathProvider ?? new SdkCurrentPathProvider();
    }

    /// <summary>
    /// 计算需要清除的托管环境变量名集合（纯函数，最优先单测）。
    /// </summary>
    /// <remarks>
    /// 仅包含 DevSwitch 写入的变量；用户自定义变量不在此列。
    /// JDK_HOME / M2_HOME 是初始化时的可选兼容变量，按 options 决定是否清除。
    /// </remarks>
    public static IReadOnlyList<string> BuildManagedVariableNames(EnvironmentResetOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var names = new List<string>
        {
            EnvironmentLayout.DevSwitchHomeName,
            "JAVA_HOME",
            "MAVEN_HOME",
            "GOROOT",
        };

        if (options.ClearJdkHome)
        {
            names.Add("JDK_HOME");
        }

        if (options.ClearM2Home)
        {
            names.Add("M2_HOME");
        }

        return names;
    }

    /// <summary>
    /// 计算各 SDK 类型对应的 current 入口路径集合（纯逻辑，便于断言）。
    /// </summary>
    public IReadOnlyList<(SdkType Type, string CurrentPath)> BuildCurrentLinkPaths(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("Data root is required.", nameof(dataRoot));
        }

        var types = new[] { SdkType.Java, SdkType.Maven, SdkType.Node, SdkType.Go, SdkType.Rust };
        return types.Select(type => (type, currentPathProvider.GetCurrentPath(dataRoot, type))).ToArray();
    }

    /// <summary>
    /// 执行工具环境重置。
    /// </summary>
    /// <param name="dataRoot">DevSwitch 数据根目录。</param>
    /// <param name="options">重置选项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<EnvironmentResetResult> ResetAsync(
        string dataRoot,
        EnvironmentResetOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            return EnvironmentResetResult.Fail("invalid-request", "Data root is required.");
        }

        options ??= EnvironmentResetOptions.Default;
        cancellationToken.ThrowIfCancellationRequested();

        // 1) 移除托管 PATH 片段（只移托管，保留用户其它项；此处先不广播，末尾统一广播）。
        //    传入 dataRoot 以同时移除绝对路径片段（新格式）与占位符片段（旧格式）。
        var pathResult = await environmentService.ResetEnvironmentAsync(broadcast: false, dataRoot, cancellationToken);
        if (!pathResult.Success)
        {
            return EnvironmentResetResult.Fail(pathResult.ErrorCode ?? "remove-path-failed", pathResult.Message);
        }

        // 1b) 同时从 HKLM 系统 PATH 移除托管片段（切换时可能已写入系统 PATH 最前）。
        //     shim 方案下系统 PATH 只放 shims 一条；同时兼容旧的 current\<type>\bin 片段，一并清理。
        //     best-effort：无管理员权限时跳过，不让整个重置失败——用户级清理已完成。
        var machineManaged = new List<string>(EnvironmentLayout.BuildManagedPathEntries(dataRoot))
        {
            EnvironmentLayout.BuildShimsPathEntry(dataRoot),
        };
        var machineRemove = await environmentHelperClient.RemoveMachinePathEntriesAsync(machineManaged, cancellationToken);
        if (!machineRemove.Success
            && !string.Equals(machineRemove.ErrorCode, "registry-access-denied", StringComparison.Ordinal))
        {
            return EnvironmentResetResult.Fail(machineRemove.ErrorCode ?? "remove-machine-path-failed", machineRemove.Message);
        }

        // 2) 清除托管环境变量（写空值即视为清除；helper 端对空值做删除/置空处理）。
        var managedVariableNames = BuildManagedVariableNames(options);
        var clearVariables = managedVariableNames.Select(name => new EnvironmentVariable(name, string.Empty)).ToArray();
        var writeResponse = await environmentHelperClient.WriteUserEnvironmentAsync(clearVariables, cancellationToken);
        if (!writeResponse.Success)
        {
            return EnvironmentResetResult.Fail(writeResponse.ErrorCode ?? "registry-write-failed", writeResponse.Message);
        }

        // 3) 移除 current 入口链接（仅 junction/symlink，不碰目标 SDK 目录）。
        var removedLinks = new List<string>();
        foreach (var (_, currentPath) in BuildCurrentLinkPaths(dataRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var linkResponse = await linkClient.RemoveCurrentLinkAsync(currentPath, cancellationToken);
            if (!linkResponse.Success)
            {
                return EnvironmentResetResult.Fail(linkResponse.ErrorCode ?? "remove-link-failed", linkResponse.Message);
            }

            // 仅当链接确实存在并被移除时计入结果，避免把「本就不存在」当成移除。
            if (LinkWasRemoved(linkResponse))
            {
                removedLinks.Add(currentPath);
            }
        }

        // 4) 清空 catalog.active 当前使用状态（不动 items，不删任何 SDK 实体）。
        var catalog = await catalogStore.LoadOrCreateAsync(dataRoot);
        bool activeCleared = !IsActiveEmpty(catalog.Active);
        if (activeCleared)
        {
            await catalogStore.SaveAsync(dataRoot, catalog with { Active = ActiveSdkSet.Empty });
        }

        // 5) 统一广播一次环境变更。
        if (options.Broadcast)
        {
            var broadcastResult = await environmentService.BroadcastAsync(cancellationToken);
            if (!broadcastResult.Success)
            {
                return EnvironmentResetResult.Fail(broadcastResult.ErrorCode ?? "broadcast-failed", broadcastResult.Message);
            }
        }

        return new EnvironmentResetResult(
            Success: true,
            RemovedVariables: managedVariableNames,
            RemovedPathEntries: pathResult.RemovedPathEntries,
            RemovedCurrentLinks: removedLinks,
            ActiveCleared: activeCleared,
            ErrorCode: null,
            Message: "Tool environment reset; no SDK entity removed.");
    }

    /// <summary>
    /// 判断 helper removeCurrentLink 响应是否表示链接被实际移除（details.removed==true 或缺省视为已处理）。
    /// </summary>
    private static bool LinkWasRemoved(HelperResponse response)
    {
        if (response.Details is not { } details || details.ValueKind != JsonValueKind.Object)
        {
            // 没有结构化细节时，成功响应保守地视为已移除。
            return true;
        }

        if (details.TryGetProperty("removed", out var removed) && removed.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return removed.GetBoolean();
        }

        // existed=false 表示链接本就不存在，不计入移除集合。
        if (details.TryGetProperty("existed", out var existed) && existed.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return existed.GetBoolean();
        }

        return true;
    }

    private static bool IsActiveEmpty(ActiveSdkSet active)
    {
        return string.IsNullOrWhiteSpace(active.Java)
            && string.IsNullOrWhiteSpace(active.Maven)
            && string.IsNullOrWhiteSpace(active.Node)
            && string.IsNullOrWhiteSpace(active.Go)
            && string.IsNullOrWhiteSpace(active.Rust);
    }
}
