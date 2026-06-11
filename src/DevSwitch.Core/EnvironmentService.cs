// 文件用途：DevSwitch 用户环境变量写入的 Core 高层服务与 helper 客户端抽象。
//           组合 helper 的 writeUserEnvironment / appendManagedPathEntries /
//           removeManagedPathEntries / readUserEnvironment / broadcastEnvironmentChanged。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.Diagnostics、System.Text.Json
// NOTE: 合法授权学习使用，仅限本地环境。本服务只写 HKCU（当前用户），不触碰 HKLM、不要求管理员权限。

using System.Diagnostics;
using System.Text.Json;

namespace DevSwitch.Core;

/// <summary>
/// 用户环境变量 helper 操作客户端抽象，便于 Core 业务用 fake 替代真实进程做单测。
/// </summary>
/// <remarks>
/// 与 <see cref="IHelperClient"/> 独立，互不影响：本接口只负责 HKCU\Environment 相关操作。
/// </remarks>
public interface IEnvironmentHelperClient
{
    /// <summary>
    /// 写入若干用户环境变量（REG_EXPAND_SZ）。
    /// </summary>
    Task<HelperResponse> WriteUserEnvironmentAsync(IReadOnlyList<EnvironmentVariable> variables, CancellationToken cancellationToken = default);

    /// <summary>
    /// 追加托管 PATH 片段（去重、保序、只加不删）。
    /// </summary>
    Task<HelperResponse> AppendManagedPathEntriesAsync(IReadOnlyList<string> entries, CancellationToken cancellationToken = default);

    /// <summary>
    /// 置顶托管 PATH 片段：移除 Path 中等于这些片段的旧条目，把它们插到 Path 最前，其余用户条目保序在后。
    /// </summary>
    /// <remarks>
    /// 用于修复用户已有其它 SDK 残留项遮蔽托管 java\bin 的问题：托管片段置顶后优先级最高。
    /// </remarks>
    Task<HelperResponse> PrependManagedPathEntriesAsync(IReadOnlyList<string> entries, CancellationToken cancellationToken = default);

    /// <summary>
    /// 移除托管 PATH 片段（仅完全匹配的托管条目）。
    /// </summary>
    Task<HelperResponse> RemoveManagedPathEntriesAsync(IReadOnlyList<string> entries, CancellationToken cancellationToken = default);

    /// <summary>
    /// 置顶托管 PATH 片段到 HKLM 系统 Path（需要管理员权限）。
    /// </summary>
    /// <remarks>
    /// Windows 新进程的有效 PATH 顺序是 系统 Path 在前、用户 Path 在后，因此只有写到系统 Path 最前，
    /// 才能压过系统级旧 JDK/Node/Go 条目（如 D:\Programs\java\jdk\...\bin）。无管理员权限时返回 registry-access-denied。
    /// </remarks>
    Task<HelperResponse> PrependMachinePathEntriesAsync(IReadOnlyList<string> entries, CancellationToken cancellationToken = default);

    /// <summary>
    /// 从 HKLM 系统 Path 移除托管 PATH 片段（需要管理员权限）。
    /// </summary>
    Task<HelperResponse> RemoveMachinePathEntriesAsync(IReadOnlyList<string> entries, CancellationToken cancellationToken = default);

    /// <summary>
    /// 根据 current 下真实可执行重建 shims 目录（生成/更新/清理 shim 转发器）。
    /// </summary>
    /// <param name="dataRoot">数据根目录绝对路径。</param>
    /// <param name="shimSourcePath">DevSwitch.Shim.exe 绝对路径。</param>
    Task<HelperResponse> RebuildShimsAsync(string dataRoot, string shimSourcePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取若干用户环境变量的原始未展开值。
    /// </summary>
    Task<HelperResponse> ReadUserEnvironmentAsync(IReadOnlyList<string> names, CancellationToken cancellationToken = default);

    /// <summary>
    /// 广播 WM_SETTINGCHANGE，让新进程感知用户环境变化。
    /// </summary>
    Task<HelperResponse> BroadcastEnvironmentChangedAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 用户环境操作结果。
/// </summary>
/// <param name="Success">是否成功。</param>
/// <param name="ErrorCode">失败时的稳定错误码（kebab-case）。</param>
/// <param name="Message">面向 UI/日志的简短说明。</param>
/// <param name="WrittenVariables">本次写入的变量名（writeUserEnvironment）。</param>
/// <param name="AddedPathEntries">本次新增的托管 PATH 片段（appendManagedPathEntries）。</param>
/// <param name="RemovedPathEntries">本次移除的托管 PATH 片段（removeManagedPathEntries）。</param>
public sealed record EnvironmentOperationResult(
    bool Success,
    string? ErrorCode,
    string Message,
    IReadOnlyList<string> WrittenVariables,
    IReadOnlyList<string> AddedPathEntries,
    IReadOnlyList<string> RemovedPathEntries)
{
    /// <summary>
    /// 构造成功结果。
    /// </summary>
    public static EnvironmentOperationResult Ok(
        string message,
        IReadOnlyList<string>? written = null,
        IReadOnlyList<string>? added = null,
        IReadOnlyList<string>? removed = null)
    {
        return new EnvironmentOperationResult(
            true,
            null,
            message,
            written ?? Array.Empty<string>(),
            added ?? Array.Empty<string>(),
            removed ?? Array.Empty<string>());
    }

    /// <summary>
    /// 构造失败结果。
    /// </summary>
    public static EnvironmentOperationResult Fail(string errorCode, string message)
    {
        return new EnvironmentOperationResult(
            false,
            errorCode,
            message,
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>());
    }
}

/// <summary>
/// 通过真实 helper 进程发送用户环境变量请求的客户端（一次请求一进程）。
/// </summary>
public sealed class ProcessEnvironmentHelperClient : IEnvironmentHelperClient
{
    private readonly string helperPath;
    private readonly JsonSerializerOptions serializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 创建用户环境 helper 进程客户端。
    /// </summary>
    /// <param name="helperPath">DevSwitch.Helper.exe 路径。</param>
    public ProcessEnvironmentHelperClient(string helperPath)
    {
        if (string.IsNullOrWhiteSpace(helperPath))
        {
            throw new ArgumentException("Helper path is required.", nameof(helperPath));
        }

        this.helperPath = helperPath;
    }

    /// <inheritdoc />
    public Task<HelperResponse> WriteUserEnvironmentAsync(IReadOnlyList<EnvironmentVariable> variables, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(variables);
        return InvokeAsync("writeUserEnvironment", new WriteUserEnvironmentPayload(variables), cancellationToken);
    }

    /// <inheritdoc />
    public Task<HelperResponse> AppendManagedPathEntriesAsync(IReadOnlyList<string> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return InvokeAsync("appendManagedPathEntries", new ManagedPathEntriesPayload(entries), cancellationToken);
    }

    /// <inheritdoc />
    public Task<HelperResponse> PrependManagedPathEntriesAsync(IReadOnlyList<string> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        // 复用 ManagedPathEntriesPayload（{"entries":[...]}），仅 operation 不同。
        return InvokeAsync("prependManagedPathEntries", new ManagedPathEntriesPayload(entries), cancellationToken);
    }

    /// <inheritdoc />
    public Task<HelperResponse> RemoveManagedPathEntriesAsync(IReadOnlyList<string> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return InvokeAsync("removeManagedPathEntries", new ManagedPathEntriesPayload(entries), cancellationToken);
    }

    /// <inheritdoc />
    public Task<HelperResponse> PrependMachinePathEntriesAsync(IReadOnlyList<string> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        // 复用 ManagedPathEntriesPayload（{"entries":[...]}），写 HKLM 系统 Path，需要管理员权限。
        return InvokeAsync("prependMachinePathEntries", new ManagedPathEntriesPayload(entries), cancellationToken);
    }

    /// <inheritdoc />
    public Task<HelperResponse> RemoveMachinePathEntriesAsync(IReadOnlyList<string> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return InvokeAsync("removeMachinePathEntries", new ManagedPathEntriesPayload(entries), cancellationToken);
    }

    /// <inheritdoc />
    public Task<HelperResponse> RebuildShimsAsync(string dataRoot, string shimSourcePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("Data root is required.", nameof(dataRoot));
        }
        if (string.IsNullOrWhiteSpace(shimSourcePath))
        {
            throw new ArgumentException("Shim source path is required.", nameof(shimSourcePath));
        }

        return InvokeAsync("rebuildShims", new RebuildShimsPayload(dataRoot, shimSourcePath), cancellationToken);
    }

    /// <inheritdoc />
    public Task<HelperResponse> ReadUserEnvironmentAsync(IReadOnlyList<string> names, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(names);
        return InvokeAsync("readUserEnvironment", new ReadUserEnvironmentPayload(names), cancellationToken);
    }

    /// <inheritdoc />
    public Task<HelperResponse> BroadcastEnvironmentChangedAsync(CancellationToken cancellationToken = default)
    {
        // broadcast 不需要 payload。
        return InvokeAsync("broadcastEnvironmentChanged", null, cancellationToken);
    }

    private async Task<HelperResponse> InvokeAsync(string operation, object? payload, CancellationToken cancellationToken)
    {
        // NOTE: helper 是一次请求一个进程的安全边界，避免长期驻留进程积累状态。
        var request = new HelperRequest(Guid.NewGuid().ToString("N"), operation, payload);
        var startInfo = new ProcessStartInfo(helperPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start DevSwitch helper.");
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request, serializerOptions));
        await process.StandardInput.FlushAsync(cancellationToken);
        process.StandardInput.Close();

        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        string error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var response = JsonSerializer.Deserialize<HelperResponse>(output, serializerOptions)
            ?? throw new InvalidDataException($"Helper returned invalid JSON. stderr: {error}");

        return response;
    }
}

/// <summary>
/// 用户环境变量写入高层服务：组合 helper 操作完成初始化、重置、写变量、追加 PATH、广播。
/// </summary>
/// <remarks>
/// 安全原则（design.md 第 8 节 / CONTEXT.md 用户级切换、托管 PATH 片段）：
/// - 只写 HKCU（当前用户），不碰 HKLM，不要求管理员权限。
/// - 托管 PATH 片段只追加、去重、保序，绝不删除/重排用户已有条目。
/// - 写入变量使用 REG_EXPAND_SZ，保留 %VAR% 占位符。
/// </remarks>
public sealed class EnvironmentService
{
    private readonly IEnvironmentHelperClient helperClient;

    /// <summary>
    /// 创建用户环境变量服务。
    /// </summary>
    /// <param name="helperClient">用户环境 helper 客户端。</param>
    public EnvironmentService(IEnvironmentHelperClient helperClient)
    {
        this.helperClient = helperClient ?? throw new ArgumentNullException(nameof(helperClient));
    }

    /// <summary>
    /// 初始化用户环境：写默认变量集 + 置顶托管 PATH 片段 + 广播。
    /// </summary>
    /// <param name="devSwitchHomeValue">DEVSWITCH_HOME 的值（数据根目录，可含 %LOCALAPPDATA% 占位符）。</param>
    /// <param name="options">兼容变量选项。</param>
    /// <param name="broadcast">是否在写入后广播 WM_SETTINGCHANGE，默认 true。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<EnvironmentOperationResult> InitializeEnvironmentAsync(
        string devSwitchHomeValue,
        EnvironmentCompatibilityOptions? options = null,
        bool broadcast = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(devSwitchHomeValue))
        {
            return EnvironmentOperationResult.Fail("invalid-request", "DEVSWITCH_HOME value is required.");
        }

        // 1) 计算并写入默认变量集。
        var variables = EnvironmentLayout.BuildDefaultVariables(devSwitchHomeValue, options);
        var writeResult = await WriteVariablesAsync(variables, broadcast: false, cancellationToken);
        if (!writeResult.Success)
        {
            return writeResult;
        }

        // 2) 置顶托管 PATH 片段（helper 端移除旧的同名条目并插到 Path 最前）。
        //    使用绝对路径版本：嵌套占位符 %DEVSWITCH_HOME% 无法被 Windows 一层展开，故传 devSwitchHomeValue。
        //    置顶（而非追加）是修复关键：用户已有其它 SDK 残留项排在前面会遮蔽托管 java\bin，导致解析旧版本。
        var managed = EnvironmentLayout.BuildManagedPathEntries(devSwitchHomeValue);
        var prependResult = await PrependPathEntriesAsync(managed, broadcast: false, cancellationToken);
        if (!prependResult.Success)
        {
            return prependResult;
        }

        // 3) 统一在末尾广播一次，避免多次广播。
        if (broadcast)
        {
            var broadcastResult = await BroadcastAsync(cancellationToken);
            if (!broadcastResult.Success)
            {
                return broadcastResult;
            }
        }

        return EnvironmentOperationResult.Ok(
            "Environment initialized.",
            written: writeResult.WrittenVariables,
            added: prependResult.AddedPathEntries);
    }

    /// <summary>
    /// 重置用户环境：移除托管 PATH 片段并广播。
    /// </summary>
    /// <remarks>
    /// 默认只移除托管 PATH 片段；变量值（JAVA_HOME 等）保留指向 current 入口，
    /// 不在此处删除，避免误删用户可能依赖的变量。变量层面的清理由上层显式决定。
    /// </remarks>
    /// <param name="broadcast">是否在移除后广播。</param>
    /// <param name="dataRoot">数据根目录；提供时同时移除"绝对路径"托管片段（新版写入格式），为空仅移除占位符片段（旧格式）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<EnvironmentOperationResult> ResetEnvironmentAsync(
        bool broadcast = true,
        string? dataRoot = null,
        CancellationToken cancellationToken = default)
    {
        // 同时移除占位符片段（旧格式）与绝对路径片段（新格式），兼容历史已初始化的用户。
        var managed = new List<string>(EnvironmentLayout.BuildManagedPathEntries());
        if (!string.IsNullOrWhiteSpace(dataRoot))
        {
            managed.AddRange(EnvironmentLayout.BuildManagedPathEntries(dataRoot));
        }

        var response = await helperClient.RemoveManagedPathEntriesAsync(managed, cancellationToken).ConfigureAwait(false);
        if (!response.Success)
        {
            return EnvironmentOperationResult.Fail(response.ErrorCode ?? "remove-path-failed", response.Message);
        }

        var removed = ExtractStringArray(response, "removed");

        if (broadcast)
        {
            var broadcastResult = await BroadcastAsync(cancellationToken).ConfigureAwait(false);
            if (!broadcastResult.Success)
            {
                return broadcastResult;
            }
        }

        return EnvironmentOperationResult.Ok("Environment reset.", removed: removed);
    }

    /// <summary>
    /// 写入指定变量集（REG_EXPAND_SZ）。
    /// </summary>
    public async Task<EnvironmentOperationResult> WriteVariablesAsync(
        IReadOnlyList<EnvironmentVariable> variables,
        bool broadcast = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(variables);
        if (variables.Count == 0)
        {
            return EnvironmentOperationResult.Fail("invalid-request", "At least one variable is required.");
        }

        var response = await helperClient.WriteUserEnvironmentAsync(variables, cancellationToken);
        if (!response.Success)
        {
            return EnvironmentOperationResult.Fail(response.ErrorCode ?? "registry-write-failed", response.Message);
        }

        var written = ExtractStringArray(response, "written");

        if (broadcast)
        {
            var broadcastResult = await BroadcastAsync(cancellationToken);
            if (!broadcastResult.Success)
            {
                return broadcastResult;
            }
        }

        return EnvironmentOperationResult.Ok("Variables written.", written: written);
    }

    /// <summary>
    /// 追加托管 PATH 片段（去重、保序、只加不删）。
    /// </summary>
    public async Task<EnvironmentOperationResult> AppendPathEntriesAsync(
        IReadOnlyList<string> entries,
        bool broadcast = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return EnvironmentOperationResult.Fail("invalid-request", "At least one path entry is required.");
        }

        var response = await helperClient.AppendManagedPathEntriesAsync(entries, cancellationToken);
        if (!response.Success)
        {
            return EnvironmentOperationResult.Fail(response.ErrorCode ?? "registry-write-failed", response.Message);
        }

        var added = ExtractStringArray(response, "added");

        if (broadcast)
        {
            var broadcastResult = await BroadcastAsync(cancellationToken);
            if (!broadcastResult.Success)
            {
                return broadcastResult;
            }
        }

        return EnvironmentOperationResult.Ok("Path entries appended.", added: added);
    }

    /// <summary>
    /// 置顶托管 PATH 片段：移除 Path 中等于这些片段的旧条目，把它们插到 Path 最前，其余用户条目保序在后。
    /// </summary>
    /// <remarks>
    /// 修复遮蔽问题：用户已有其它 SDK 工具残留项排在前面会遮蔽追加到末尾的托管 java\bin，
    /// 置顶后托管片段优先级最高，确保解析到 DevSwitch 管理的版本。
    /// helper 端 details 用 "prepended" 暴露实际置顶的片段。
    /// </remarks>
    public async Task<EnvironmentOperationResult> PrependPathEntriesAsync(
        IReadOnlyList<string> entries,
        bool broadcast = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return EnvironmentOperationResult.Fail("invalid-request", "At least one path entry is required.");
        }

        var response = await helperClient.PrependManagedPathEntriesAsync(entries, cancellationToken);
        if (!response.Success)
        {
            return EnvironmentOperationResult.Fail(response.ErrorCode ?? "registry-write-failed", response.Message);
        }

        // helper 端 details 用 "prepended" 字段返回本次置顶的片段（契约约定）。
        var prepended = ExtractStringArray(response, "prepended");

        if (broadcast)
        {
            var broadcastResult = await BroadcastAsync(cancellationToken);
            if (!broadcastResult.Success)
            {
                return broadcastResult;
            }
        }

        // 复用 AddedPathEntries 承载置顶片段，供上层统一读取本次写入的托管片段。
        return EnvironmentOperationResult.Ok("Path entries prepended.", added: prepended);
    }

    /// <summary>
    /// 置顶托管 PATH 片段到 HKLM 系统 Path（需要管理员权限）。
    /// </summary>
    /// <remarks>
    /// 这是“真正让新终端 java -version 命中 DevSwitch”的关键：仅写用户 Path 会被系统 Path 里的旧 JDK 压住，
    /// 因为 Windows 有效 PATH 是 系统 Path 在前。无管理员权限时 helper 返回 registry-access-denied，
    /// 由上层提示用户以管理员身份重新运行。
    /// </remarks>
    public async Task<EnvironmentOperationResult> PrependMachinePathEntriesAsync(
        IReadOnlyList<string> entries,
        bool broadcast = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return EnvironmentOperationResult.Fail("invalid-request", "At least one path entry is required.");
        }

        var response = await helperClient.PrependMachinePathEntriesAsync(entries, cancellationToken);
        if (!response.Success)
        {
            return EnvironmentOperationResult.Fail(response.ErrorCode ?? "registry-write-failed", response.Message);
        }

        var prepended = ExtractStringArray(response, "prepended");

        if (broadcast)
        {
            var broadcastResult = await BroadcastAsync(cancellationToken);
            if (!broadcastResult.Success)
            {
                return broadcastResult;
            }
        }

        return EnvironmentOperationResult.Ok("Machine path entries prepended.", added: prepended);
    }

    /// <summary>
    /// 重建 shims 目录（根据 current 下真实可执行生成/更新/清理 shim 转发器）。
    /// </summary>
    /// <param name="dataRoot">数据根目录绝对路径。</param>
    /// <param name="shimSourcePath">DevSwitch.Shim.exe 绝对路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<EnvironmentOperationResult> RebuildShimsAsync(
        string dataRoot,
        string shimSourcePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            return EnvironmentOperationResult.Fail("invalid-request", "Data root is required.");
        }
        if (string.IsNullOrWhiteSpace(shimSourcePath))
        {
            return EnvironmentOperationResult.Fail("invalid-request", "Shim source path is required.");
        }

        var response = await helperClient.RebuildShimsAsync(dataRoot, shimSourcePath, cancellationToken);
        if (!response.Success)
        {
            return EnvironmentOperationResult.Fail(response.ErrorCode ?? "rebuild-shims-failed", response.Message);
        }

        var created = ExtractStringArray(response, "created");
        return EnvironmentOperationResult.Ok("Shims rebuilt.", added: created);
    }

    /// <summary>
    /// 广播 WM_SETTINGCHANGE。
    /// </summary>
    public async Task<EnvironmentOperationResult> BroadcastAsync(CancellationToken cancellationToken = default)
    {
        var response = await helperClient.BroadcastEnvironmentChangedAsync(cancellationToken);
        return response.Success
            ? EnvironmentOperationResult.Ok("Environment change broadcasted.")
            : EnvironmentOperationResult.Fail(response.ErrorCode ?? "broadcast-failed", response.Message);
    }

    /// <summary>
    /// 从 helper 响应 details 中提取字符串数组属性，失败时返回空集合。
    /// </summary>
    private static IReadOnlyList<string> ExtractStringArray(HelperResponse response, string propertyName)
    {
        if (response.Details is not { } details || details.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<string>();
        }

        if (!details.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>(array.GetArrayLength());
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                var value = element.GetString();
                if (value is not null)
                {
                    result.Add(value);
                }
            }
        }

        return result;
    }
}
