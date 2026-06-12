// 文件用途：实现 DevSwitch「删除 SDK 记录」能力。
//           外部 SDK 只移除登记不删实体；托管 SDK 在用户确认后可删除托管实体目录。
//           核心安全裁决（是否允许删实体 / 路径是否在托管根内）抽成纯函数，最优先单测。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.IO（仅真实 remover 使用）
// NOTE: 合法授权学习使用，仅限本地环境。删实体走可注入抽象，单测不真实删盘。

namespace DevSwitch.Core;

/// <summary>
/// 托管 SDK 实体目录删除抽象，便于单测用 fake 替代真实磁盘删除。
/// </summary>
/// <remarks>
/// 命名刻意不与 DoctorModels / SdkVerificationModels 中既有接口重名。
/// 真实实现 <see cref="SafeManagedFileRemover"/> 必须先校验目标位于 dataRoot\sdks 内才删除。
/// </remarks>
public interface IManagedFileRemover
{
    /// <summary>
    /// 递归删除托管 SDK 实体目录。
    /// </summary>
    /// <param name="directoryPath">已通过越界校验的托管 SDK 根目录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task RemoveDirectoryAsync(string directoryPath, CancellationToken cancellationToken = default);
}

/// <summary>
/// 「是否允许删除实体」的纯裁决结果（无副作用，便于单测）。
/// </summary>
/// <param name="AllowDelete">是否允许删除实体目录。</param>
/// <param name="PendingConfirmation">是否因为用户未确认而保留实体（仅托管 SDK 可能为 true）。</param>
/// <param name="Reason">稳定原因码（kebab-case），用于 UI/日志。</param>
public sealed record EntityDeletionDecision(bool AllowDelete, bool PendingConfirmation, string Reason);

/// <summary>
/// SDK 删除结果。
/// </summary>
/// <param name="Success">操作是否成功完成。</param>
/// <param name="RecordRemoved">是否已从 catalog.items 移除该记录。</param>
/// <param name="EntityDeleted">是否真实删除了托管实体目录。</param>
/// <param name="EntityPreservedPendingConfirmation">是否因未确认而保留了托管实体。</param>
/// <param name="ActivePointerCleared">删除的是否为某类型 active 记录并已清空指针。</param>
/// <param name="ErrorCode">失败时的稳定错误码；成功时为空。</param>
/// <param name="Message">面向 UI/日志的简短说明。</param>
public sealed record SdkDeletionResult(
    bool Success,
    bool RecordRemoved,
    bool EntityDeleted,
    bool EntityPreservedPendingConfirmation,
    bool ActivePointerCleared,
    string? ErrorCode,
    string Message)
{
    /// <summary>
    /// 构造失败结果。
    /// </summary>
    public static SdkDeletionResult Fail(string errorCode, string message)
    {
        return new SdkDeletionResult(false, false, false, false, false, errorCode, message);
    }
}

/// <summary>
/// SDK 删除业务服务：协调 catalog 记录移除、active 指针清理与托管实体删除。
/// </summary>
/// <remarks>
/// 安全红线：
/// - 外部 SDK 永不删实体（仅移除登记）。
/// - 托管 SDK 仅在用户明确确认且实体路径位于 dataRoot\sdks 内时才删除。
/// - 删 active 记录时同步清空对应类型 active 指针。
/// </remarks>
public sealed class SdkDeletionService
{
    /// <summary>
    /// 托管 SDK 实体所在的子目录名（dataRoot\sdks）。
    /// </summary>
    public const string ManagedSdksFolderName = "sdks";

    private readonly SdkCatalogStore catalogStore;
    private readonly IManagedFileRemover fileRemover;

    /// <summary>
    /// 创建 SDK 删除服务。
    /// </summary>
    /// <param name="catalogStore">SDK 目录读写。</param>
    /// <param name="fileRemover">托管实体删除抽象。</param>
    public SdkDeletionService(SdkCatalogStore catalogStore, IManagedFileRemover fileRemover)
    {
        this.catalogStore = catalogStore ?? throw new ArgumentNullException(nameof(catalogStore));
        this.fileRemover = fileRemover ?? throw new ArgumentNullException(nameof(fileRemover));
    }

    /// <summary>
    /// 删除指定 SDK 记录。
    /// </summary>
    /// <param name="dataRoot">DevSwitch 数据根目录。</param>
    /// <param name="recordId">待删除记录 ID。</param>
    /// <param name="confirmDeleteManagedFiles">是否已获得用户确认删除托管实体文件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<SdkDeletionResult> DeleteAsync(
        string dataRoot,
        string recordId,
        bool confirmDeleteManagedFiles,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            return SdkDeletionResult.Fail("invalid-request", "Data root is required.");
        }

        if (string.IsNullOrWhiteSpace(recordId))
        {
            return SdkDeletionResult.Fail("invalid-request", "Record id is required.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var catalog = await catalogStore.LoadOrCreateAsync(dataRoot);
        var record = catalog.Items.FirstOrDefault(item => string.Equals(item.Id, recordId, StringComparison.OrdinalIgnoreCase));
        if (record is null)
        {
            return SdkDeletionResult.Fail("sdk-record-not-found", "Target SDK record was not found.");
        }

        // 先做纯裁决：是否允许删实体、是否因未确认而保留。
        var decision = EvaluateEntityDeletion(record, dataRoot, confirmDeleteManagedFiles);

        // 实体删除发生在记录移除之前：若删除失败则不动 catalog，保持一致性。
        bool entityDeleted = false;
        if (decision.AllowDelete)
        {
            try
            {
                await fileRemover.RemoveDirectoryAsync(record.Path, cancellationToken);
                entityDeleted = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 实体删除失败：不移除登记，向上报告，避免出现「记录没了但目录还在」的悬挂状态。
                return SdkDeletionResult.Fail("sdk-entity-delete-failed", ex.Message);
            }
        }

        // 移除登记，并在删的是 active 记录时同步清空对应类型指针。
        bool activeCleared = IsActiveRecord(catalog.Active, record.Type, record.Id);
        var updatedCatalog = catalog with
        {
            Active = activeCleared ? ClearActiveRecordId(catalog.Active, record.Type) : catalog.Active,
            Items = catalog.Items.Where(item => !string.Equals(item.Id, record.Id, StringComparison.OrdinalIgnoreCase)).ToArray(),
        };

        await catalogStore.SaveAsync(dataRoot, updatedCatalog);

        return new SdkDeletionResult(
            Success: true,
            RecordRemoved: true,
            EntityDeleted: entityDeleted,
            EntityPreservedPendingConfirmation: decision.PendingConfirmation,
            ActivePointerCleared: activeCleared,
            ErrorCode: null,
            Message: BuildMessage(record.Source, entityDeleted, decision.PendingConfirmation));
    }

    /// <summary>
    /// 纯裁决：根据来源、确认标志、路径越界判断是否允许删除托管实体（无 I/O，最优先单测）。
    /// </summary>
    /// <remarks>
    /// 规则：
    /// - 外部 SDK：永不删实体。
    /// - 托管 SDK + 未确认：保留实体并标注需确认。
    /// - 托管 SDK + 已确认 + 路径在 dataRoot\sdks 内：允许删除。
    /// - 托管 SDK + 已确认 + 路径越界：拒绝删除（安全红线）。
    /// </remarks>
    public static EntityDeletionDecision EvaluateEntityDeletion(SdkRecord record, string dataRoot, bool confirmDeleteManagedFiles)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.Source == SdkSourceKind.External)
        {
            // 外部 SDK 实体保留在用户原位，绝不删除。
            return new EntityDeletionDecision(false, PendingConfirmation: false, Reason: "external-sdk-entity-preserved");
        }

        if (!confirmDeleteManagedFiles)
        {
            // 托管 SDK 未确认：只移记录，保留实体待用户确认。
            return new EntityDeletionDecision(false, PendingConfirmation: true, Reason: "managed-entity-requires-confirmation");
        }

        if (!IsWithinManagedRoot(dataRoot, record.Path))
        {
            // 已确认但路径不在托管根内：触发安全红线，拒绝删除。
            return new EntityDeletionDecision(false, PendingConfirmation: false, Reason: "managed-path-outside-data-root");
        }

        return new EntityDeletionDecision(true, PendingConfirmation: false, Reason: "managed-entity-delete-allowed");
    }

    /// <summary>
    /// 纯函数：判断目标路径是否严格位于 dataRoot\sdks 之内（含规范化，防止 .. 越界）。
    /// </summary>
    /// <remarks>
    /// 比较前对两侧做 <see cref="Path.GetFullPath(string)"/> 规范化并补尾分隔符，
    /// 确保 `...\sdks-evil` 之类的前缀伪装不被误判为子目录。
    /// </remarks>
    public static bool IsWithinManagedRoot(string dataRoot, string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(dataRoot) || string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        string managedRoot;
        string fullCandidate;
        try
        {
            managedRoot = Path.GetFullPath(Path.Combine(dataRoot, ManagedSdksFolderName));
            fullCandidate = Path.GetFullPath(candidatePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        // 候选路径恰好等于托管根本身不允许删除（不能删整个 sdks 目录）。
        if (string.Equals(WithTrailingSeparator(managedRoot), WithTrailingSeparator(fullCandidate), comparison))
        {
            return false;
        }

        return WithTrailingSeparator(fullCandidate).StartsWith(WithTrailingSeparator(managedRoot), comparison);
    }

    private static string WithTrailingSeparator(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed + Path.DirectorySeparatorChar;
    }

    private static bool IsActiveRecord(ActiveSdkSet active, SdkType type, string recordId)
    {
        var current = type switch
        {
            SdkType.Java => active.Java,
            SdkType.Maven => active.Maven,
            SdkType.Node => active.Node,
            SdkType.Go => active.Go,
            SdkType.Rust => active.Rust,
            _ => null,
        };

        return !string.IsNullOrWhiteSpace(current) && string.Equals(current, recordId, StringComparison.OrdinalIgnoreCase);
    }

    private static ActiveSdkSet ClearActiveRecordId(ActiveSdkSet active, SdkType type)
    {
        return type switch
        {
            SdkType.Java => active with { Java = null },
            SdkType.Maven => active with { Maven = null },
            SdkType.Node => active with { Node = null },
            SdkType.Go => active with { Go = null },
            SdkType.Rust => active with { Rust = null },
            _ => active,
        };
    }

    private static string BuildMessage(SdkSourceKind source, bool entityDeleted, bool pendingConfirmation)
    {
        if (source == SdkSourceKind.External)
        {
            return "External SDK record removed; entity preserved in place.";
        }

        if (entityDeleted)
        {
            return "Managed SDK record removed and entity deleted.";
        }

        return pendingConfirmation
            ? "Managed SDK record removed; entity preserved pending confirmation."
            : "Managed SDK record removed; entity preserved.";
    }
}

/// <summary>
/// 真实托管实体删除器：删除前强制校验目标位于 dataRoot\sdks 内，安全递归删除。
/// </summary>
/// <remarks>
/// 这是双重保险：即便调用方裁决出错，本类也会再次拒绝越界路径，绝不删 sdks 之外目录。
/// </remarks>
public sealed class SafeManagedFileRemover : IManagedFileRemover
{
    private readonly string dataRoot;

    /// <summary>
    /// 创建真实托管实体删除器。
    /// </summary>
    /// <param name="dataRoot">DevSwitch 数据根目录，用于越界校验。</param>
    public SafeManagedFileRemover(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("Data root is required.", nameof(dataRoot));
        }

        this.dataRoot = dataRoot;
    }

    /// <inheritdoc />
    public Task RemoveDirectoryAsync(string directoryPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 安全红线再校验一次：拒绝任何不在 dataRoot\sdks 内的路径。
        if (!SdkDeletionService.IsWithinManagedRoot(dataRoot, directoryPath))
        {
            throw new UnauthorizedAccessException($"Refusing to delete path outside managed sdks root: {directoryPath}");
        }

        // Directory.Delete 是同步 API；删除前确认目录存在，不存在视为已删除（幂等）。
        if (Directory.Exists(directoryPath))
        {
            Directory.Delete(directoryPath, recursive: true);
        }

        return Task.CompletedTask;
    }
}
