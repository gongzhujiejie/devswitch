// 文件用途：提供 SDK 切换业务服务，协调 sdks.json active 状态与 helper current link 事务。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.Diagnostics、System.Text.Json

using System.Diagnostics;
using System.Text.Json;

namespace DevSwitch.Core;

/// <summary>
/// SDK 切换结果。
/// </summary>
/// <param name="Success">是否完成切换并持久化 active。</param>
/// <param name="SdkType">本次切换的 SDK 类型。</param>
/// <param name="TargetRecordId">目标 SDK 记录 ID。</param>
/// <param name="PreviousRecordId">切换前 active 记录 ID。</param>
/// <param name="ErrorCode">失败时的稳定错误码。</param>
/// <param name="Message">面向 UI/日志的简短说明。</param>
/// <param name="TargetRecord">目标 SDK 记录。</param>
public sealed record SdkSwitchResult(
    bool Success,
    SdkType SdkType,
    string TargetRecordId,
    string? PreviousRecordId,
    string? ErrorCode,
    string Message,
    SdkRecord? TargetRecord);

/// <summary>
/// SDK 记录切换前验证结果。
/// </summary>
public sealed record SdkRecordValidationResult(bool Success, string? ErrorCode, string Message)
{
    /// <summary>
    /// 验证成功常量。
    /// </summary>
    public static SdkRecordValidationResult Ok { get; } = new(true, null, "SDK record is usable.");
}

/// <summary>
/// helper 进程客户端抽象，便于 Core 业务测试用 fake 替代真实进程。
/// </summary>
public interface IHelperClient
{
    /// <summary>
    /// 请求 helper 执行 SDK current link 切换事务。
    /// </summary>
    Task<HelperResponse> SwitchSdkAsync(SdkType sdkType, string currentPath, string targetPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 请求 helper 在单进程内完成 junction 切换 + 一次广播（批处理，减少进程数与卡顿）。
    /// </summary>
    Task<HelperResponse> SwitchSdkBatchAsync(SdkType sdkType, string currentPath, string targetPath, bool broadcast, CancellationToken cancellationToken = default);

    /// <summary>
    /// 请求 helper 检查 current link 最终状态。
    /// </summary>
    Task<HelperResponse> InspectLinkAsync(string currentPath, CancellationToken cancellationToken = default);
}

/// <summary>
/// 计算某 SDK 类型对应 current link 路径。
/// </summary>
public interface ISdkCurrentPathProvider
{
    /// <summary>
    /// 获取 dataRoot/current/typeSlug 路径。
    /// </summary>
    string GetCurrentPath(string dataRoot, SdkType sdkType);
}

/// <summary>
/// 验证 SDK 记录是否可作为切换目标。
/// </summary>
public interface ISdkRecordValidator
{
    /// <summary>
    /// 验证目标记录路径和类型。
    /// </summary>
    SdkRecordValidationResult ValidateSwitchTarget(SdkRecord record, SdkType requestedType);
}

/// <summary>
/// 默认 current path provider。
/// </summary>
public sealed class SdkCurrentPathProvider : ISdkCurrentPathProvider
{
    /// <inheritdoc />
    public string GetCurrentPath(string dataRoot, SdkType sdkType)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("Data root is required.", nameof(dataRoot));
        }

        return Path.Combine(dataRoot, "current", GetTypeSlug(sdkType));
    }

    /// <summary>
    /// 将 SDK 类型转换为 current 目录 slug。
    /// </summary>
    public static string GetTypeSlug(SdkType sdkType)
    {
        return sdkType switch
        {
            SdkType.Java => "java",
            SdkType.Maven => "maven",
            SdkType.Node => "node",
            SdkType.Go => "go",
            _ => "sdk",
        };
    }
}

/// <summary>
/// 默认 SDK 记录验证器，复用 SDK 根目录检测器做轻量结构验证。
/// </summary>
public sealed class SdkRecordValidator : ISdkRecordValidator
{
    /// <inheritdoc />
    public SdkRecordValidationResult ValidateSwitchTarget(SdkRecord record, SdkType requestedType)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.Type != requestedType)
        {
            return new SdkRecordValidationResult(false, "sdk-type-mismatch", "SDK record type does not match requested type.");
        }

        if (record.Status == SdkRecordStatus.Unavailable)
        {
            return new SdkRecordValidationResult(false, "sdk-record-unavailable", "SDK record is marked unavailable.");
        }

        try
        {
            var detection = SdkRootDetector.Detect(record.Path);
            if (detection.Status != SdkStatus.Usable || detection.Type != requestedType)
            {
                return new SdkRecordValidationResult(false, "sdk-target-invalid", "SDK target path is not a usable SDK root.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new SdkRecordValidationResult(false, "sdk-target-invalid", ex.Message);
        }

        return SdkRecordValidationResult.Ok;
    }
}

/// <summary>
/// 通过真实 helper 进程发送 JSON 请求的客户端。
/// </summary>
public sealed class ProcessHelperClient : IHelperClient
{
    private readonly string helperPath;
    private readonly JsonSerializerOptions serializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 创建 helper 进程客户端。
    /// </summary>
    /// <param name="helperPath">DevSwitch.Helper.exe 路径。</param>
    public ProcessHelperClient(string helperPath)
    {
        if (string.IsNullOrWhiteSpace(helperPath))
        {
            throw new ArgumentException("Helper path is required.", nameof(helperPath));
        }

        this.helperPath = helperPath;
    }

    /// <inheritdoc />
    public Task<HelperResponse> SwitchSdkAsync(SdkType sdkType, string currentPath, string targetPath, CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            sdkType = SdkCurrentPathProvider.GetTypeSlug(sdkType),
            currentPath,
            targetPath,
            linkPreference = "junction-first",
        };

        return InvokeAsync("switchSdk", payload, cancellationToken);
    }

    /// <inheritdoc />
    public Task<HelperResponse> InspectLinkAsync(string currentPath, CancellationToken cancellationToken = default)
    {
        return InvokeAsync("inspectLink", new { currentPath }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<HelperResponse> SwitchSdkBatchAsync(SdkType sdkType, string currentPath, string targetPath, bool broadcast, CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            sdkType = SdkCurrentPathProvider.GetTypeSlug(sdkType),
            currentPath,
            targetPath,
            linkPreference = "junction-first",
            broadcast,
        };

        return InvokeAsync("switchSdkBatch", payload, cancellationToken);
    }

    private async Task<HelperResponse> InvokeAsync(string operation, object payload, CancellationToken cancellationToken)
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
        await process.StandardInput.FlushAsync();
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
/// SDK 切换服务。
/// </summary>
public sealed class SdkSwitchService
{
    private readonly string dataRoot;
    private readonly SdkCatalogStore catalogStore;
    private readonly IHelperClient helperClient;
    private readonly ISdkCurrentPathProvider currentPathProvider;
    private readonly ISdkRecordValidator recordValidator;

    /// <summary>
    /// 创建 SDK 切换服务。
    /// </summary>
    public SdkSwitchService(
        string dataRoot,
        SdkCatalogStore catalogStore,
        IHelperClient helperClient,
        ISdkCurrentPathProvider? currentPathProvider = null,
        ISdkRecordValidator? recordValidator = null)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("Data root is required.", nameof(dataRoot));
        }

        this.dataRoot = dataRoot;
        this.catalogStore = catalogStore ?? throw new ArgumentNullException(nameof(catalogStore));
        this.helperClient = helperClient ?? throw new ArgumentNullException(nameof(helperClient));
        this.currentPathProvider = currentPathProvider ?? new SdkCurrentPathProvider();
        this.recordValidator = recordValidator ?? new SdkRecordValidator();
    }

    /// <summary>
    /// 切换指定 SDK 类型到目标记录。
    /// </summary>
    public async Task<SdkSwitchResult> SwitchAsync(SdkType sdkType, string targetRecordId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetRecordId))
        {
            return Failure(sdkType, targetRecordId, null, "sdk-record-not-found", "Target SDK record id is required.", null);
        }

        var catalog = await catalogStore.LoadOrCreateAsync(dataRoot);
        var target = catalog.Items.FirstOrDefault(item => string.Equals(item.Id, targetRecordId, StringComparison.OrdinalIgnoreCase));
        string? previousRecordId = GetActiveRecordId(catalog.Active, sdkType);

        if (target is null)
        {
            return Failure(sdkType, targetRecordId, previousRecordId, "sdk-record-not-found", "Target SDK record was not found.", null);
        }

        var validation = recordValidator.ValidateSwitchTarget(target, sdkType);
        if (!validation.Success)
        {
            return Failure(sdkType, targetRecordId, previousRecordId, validation.ErrorCode ?? "sdk-target-invalid", validation.Message, target);
        }

        string currentPath = currentPathProvider.GetCurrentPath(dataRoot, sdkType);
        var helperResponse = await helperClient.SwitchSdkAsync(sdkType, currentPath, target.Path, cancellationToken);
        if (!helperResponse.Success)
        {
            return Failure(sdkType, targetRecordId, previousRecordId, helperResponse.ErrorCode ?? "helper-switch-failed", helperResponse.Message, target);
        }

        var inspectResponse = await helperClient.InspectLinkAsync(currentPath, cancellationToken);
        if (!HelperResponseTargetsPath(inspectResponse, target.Path))
        {
            await TryRollbackAsync(catalog, sdkType, previousRecordId, currentPath, cancellationToken);
            return Failure(sdkType, targetRecordId, previousRecordId, "post-switch-validation-failed", "Current link did not point to target after switch.", target);
        }

        var updatedCatalog = catalog with
        {
            Active = SetActiveRecordId(catalog.Active, sdkType, targetRecordId),
            Items = catalog.Items.Select(item => UpdateRecordStatus(item, sdkType, targetRecordId)).ToArray(),
        };

        await catalogStore.SaveAsync(dataRoot, updatedCatalog);
        return new SdkSwitchResult(true, sdkType, targetRecordId, previousRecordId, null, "SDK switched.", target with { Status = SdkRecordStatus.Active });
    }

    /// <summary>
    /// 批处理切换：用 helper 的 switchSdkBatch 在单进程内完成 junction 切换 + 一次广播，
    /// 显著降低切换耗时与卡顿（不再分多个 helper 进程，也不重复广播）。
    /// 切换成功后照常收敛 active 状态并落盘 sdks.json。
    /// </summary>
    public async Task<SdkSwitchResult> SwitchBatchAsync(SdkType sdkType, string targetRecordId, bool broadcast = true, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetRecordId))
        {
            return Failure(sdkType, targetRecordId, null, "sdk-record-not-found", "Target SDK record id is required.", null);
        }

        var catalog = await catalogStore.LoadOrCreateAsync(dataRoot);
        var target = catalog.Items.FirstOrDefault(item => string.Equals(item.Id, targetRecordId, StringComparison.OrdinalIgnoreCase));
        string? previousRecordId = GetActiveRecordId(catalog.Active, sdkType);

        if (target is null)
        {
            return Failure(sdkType, targetRecordId, previousRecordId, "sdk-record-not-found", "Target SDK record was not found.", null);
        }

        var validation = recordValidator.ValidateSwitchTarget(target, sdkType);
        if (!validation.Success)
        {
            return Failure(sdkType, targetRecordId, previousRecordId, validation.ErrorCode ?? "sdk-target-invalid", validation.Message, target);
        }

        string currentPath = currentPathProvider.GetCurrentPath(dataRoot, sdkType);
        var helperResponse = await helperClient.SwitchSdkBatchAsync(sdkType, currentPath, target.Path, broadcast, cancellationToken);
        if (!helperResponse.Success)
        {
            return Failure(sdkType, targetRecordId, previousRecordId, helperResponse.ErrorCode ?? "helper-switch-failed", helperResponse.Message, target);
        }

        var updatedCatalog = catalog with
        {
            Active = SetActiveRecordId(catalog.Active, sdkType, targetRecordId),
            Items = catalog.Items.Select(item => UpdateRecordStatus(item, sdkType, targetRecordId)).ToArray(),
        };

        await catalogStore.SaveAsync(dataRoot, updatedCatalog);
        return new SdkSwitchResult(true, sdkType, targetRecordId, previousRecordId, null, "SDK switched.", target with { Status = SdkRecordStatus.Active });
    }

    private async Task TryRollbackAsync(SdkCatalog catalog, SdkType sdkType, string? previousRecordId, string currentPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(previousRecordId))
        {
            return;
        }

        var previous = catalog.Items.FirstOrDefault(item => string.Equals(item.Id, previousRecordId, StringComparison.OrdinalIgnoreCase));
        if (previous is null)
        {
            return;
        }

        var validation = recordValidator.ValidateSwitchTarget(previous, sdkType);
        if (!validation.Success)
        {
            return;
        }

        await helperClient.SwitchSdkAsync(sdkType, currentPath, previous.Path, cancellationToken);
    }

    private static SdkRecord UpdateRecordStatus(SdkRecord record, SdkType sdkType, string targetRecordId)
    {
        // 仅处理被切换的类型，其它类型记录原样保留。
        if (record.Type != sdkType)
        {
            return record;
        }

        // 目标记录设为 Active。
        if (string.Equals(record.Id, targetRecordId, StringComparison.OrdinalIgnoreCase))
        {
            return record with { Status = SdkRecordStatus.Active };
        }

        // 同类型其它记录：把所有残留 Active 收敛为 Usable，不再依赖 previousRecordId 单条清理，
        // 确保即使 sdks.json 有多条脏 Active，切换后同类型也只剩 target 一条 Active。
        if (record.Status == SdkRecordStatus.Active)
        {
            return record with { Status = SdkRecordStatus.Usable };
        }

        // 其它状态（Usable/Unavailable/Unverified）保持不变。
        return record;
    }

    private static bool HelperResponseTargetsPath(HelperResponse response, string targetPath)
    {
        if (!response.Success || response.Details is not { } details)
        {
            return false;
        }

        if (!details.TryGetProperty("targetPath", out var targetElement) || targetElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string? inspectedTarget = targetElement.GetString();
        if (string.IsNullOrWhiteSpace(inspectedTarget))
        {
            return false;
        }

        return PathsEqual(inspectedTarget, targetPath);
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(NormalizePath(left), NormalizePath(right), comparison);
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string? GetActiveRecordId(ActiveSdkSet active, SdkType sdkType)
    {
        return sdkType switch
        {
            SdkType.Java => active.Java,
            SdkType.Maven => active.Maven,
            SdkType.Node => active.Node,
            SdkType.Go => active.Go,
            _ => null,
        };
    }

    private static ActiveSdkSet SetActiveRecordId(ActiveSdkSet active, SdkType sdkType, string recordId)
    {
        return sdkType switch
        {
            SdkType.Java => active with { Java = recordId },
            SdkType.Maven => active with { Maven = recordId },
            SdkType.Node => active with { Node = recordId },
            SdkType.Go => active with { Go = recordId },
            _ => active,
        };
    }

    private static SdkSwitchResult Failure(SdkType sdkType, string targetRecordId, string? previousRecordId, string errorCode, string message, SdkRecord? target)
    {
        return new SdkSwitchResult(false, sdkType, targetRecordId, previousRecordId, errorCode, message, target);
    }
}
