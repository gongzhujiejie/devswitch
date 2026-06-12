// 文件用途：编排本地 SDK 导入后的自动验证与 catalog 回写。
// 创建/修改日期：2026-06-12
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：DevSwitch.Core 内部导入、验证与 catalog 存储服务
// NOTE: 合法授权学习使用，仅限本地环境。UI 层只调用本服务，不承载验证与落盘细节。

namespace DevSwitch.Core;

/// <summary>
/// 本地 SDK 导入 + 自动验证 + catalog 回写编排服务。
/// </summary>
public sealed class SdkImportRegistrationService
{
    private readonly string dataRoot;
    private readonly SdkCatalogStore store;
    private readonly LocalSdkImportService localImportService;
    private readonly SdkImportVerificationService verificationService;

    /// <summary>
    /// 创建本地 SDK 导入登记编排服务。
    /// </summary>
    public SdkImportRegistrationService(
        string dataRoot,
        SdkCatalogStore? store,
        SdkImportVerificationService verificationService)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("Data root is required.", nameof(dataRoot));
        }

        this.dataRoot = dataRoot;
        this.store = store ?? new SdkCatalogStore();
        this.localImportService = new LocalSdkImportService(dataRoot, this.store);
        this.verificationService = verificationService ?? throw new ArgumentNullException(nameof(verificationService));
    }

    /// <summary>
    /// 导入本地 SDK，并在导入成功后立即运行一次自动命令验证，把真实版本/status 回写 catalog。
    /// </summary>
    public async Task<LocalSdkImportResult> ImportAndVerifyAsync(
        string selectedPath,
        string? customName = null,
        CancellationToken cancellationToken = default)
    {
        var import = await localImportService.ImportLocalAsync(selectedPath, customName).ConfigureAwait(false);
        if (!import.Success || import.Record is null)
        {
            return import;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var verified = await verificationService.VerifyAsync(import.Record, cancellationToken).ConfigureAwait(false);
        var finalRecord = ShouldRefreshGeneratedName(customName, import.Record, verified.Record)
            ? verified.Record with { Name = BuildGeneratedName(verified.Record.Type, verified.Record.Version) }
            : verified.Record;

        await ReplaceRecordAsync(finalRecord, cancellationToken).ConfigureAwait(false);

        return import with
        {
            Record = finalRecord,
            Message = finalRecord.Status == SdkRecordStatus.Usable
                ? "SDK 已导入并验证。"
                : "SDK 已导入，但自动验证未通过。",
        };
    }

    private async Task ReplaceRecordAsync(SdkRecord record, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var catalog = await store.LoadOrCreateAsync(dataRoot).ConfigureAwait(false);
        var updated = catalog with
        {
            Items = catalog.Items.Select(item => string.Equals(item.Id, record.Id, StringComparison.OrdinalIgnoreCase) ? record : item).ToArray(),
        };
        await store.SaveAsync(dataRoot, updated).ConfigureAwait(false);
    }

    private static bool ShouldRefreshGeneratedName(string? customName, SdkRecord before, SdkRecord after)
        => string.IsNullOrWhiteSpace(customName)
            && !string.Equals(after.Version, SdkVersionResolver.UnknownVersion, StringComparison.Ordinal)
            && !string.Equals(before.Version, after.Version, StringComparison.Ordinal);

    private static string BuildGeneratedName(SdkType type, string version)
    {
        var baseName = type switch
        {
            SdkType.Java => "Java",
            SdkType.Maven => "Maven",
            SdkType.Node => "Node.js",
            SdkType.Go => "Go",
            SdkType.Rust => "Rust",
            _ => "SDK",
        };
        return string.Equals(version, SdkVersionResolver.UnknownVersion, StringComparison.Ordinal)
            ? baseName
            : $"{baseName} {version}";
    }
}
