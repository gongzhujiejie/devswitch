// 文件用途：提供本地 SDK 根目录导入服务，将可用 SDK 登记到 sdks.json。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.IO

namespace DevSwitch.Core;

/// <summary>
/// 本地 SDK 导入结果。
/// </summary>
/// <param name="Success">是否成功登记 SDK 记录。</param>
/// <param name="Record">成功时写入 sdks.json 的 SDK 记录。</param>
/// <param name="Detection">SDK 根目录识别结果。</param>
/// <param name="Message">面向 UI 的简短结果说明。</param>
public sealed record LocalSdkImportResult(
    bool Success,
    SdkRecord? Record,
    SdkDetectionResult Detection,
    string Message);

/// <summary>
/// 本地 SDK 导入服务。
/// </summary>
public sealed class LocalSdkImportService
{
    private readonly string dataRoot;
    private readonly SdkCatalogStore store;

    /// <summary>
    /// 创建本地 SDK 导入服务。
    /// </summary>
    /// <param name="dataRoot">DevSwitch 数据根目录。</param>
    /// <param name="store">可选仓储实例，测试可注入；为空时使用默认文件仓储。</param>
    /// <exception cref="ArgumentException">dataRoot 为空时抛出。</exception>
    public LocalSdkImportService(string dataRoot, SdkCatalogStore? store = null)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("Data root is required.", nameof(dataRoot));
        }

        this.dataRoot = dataRoot;
        this.store = store ?? new SdkCatalogStore();
    }

    /// <summary>
    /// 导入用户选择的本地 SDK 根目录。
    /// </summary>
    /// <param name="selectedPath">用户在文件夹选择器中选择的路径。</param>
    /// <param name="customName">可选自定义展示名称。</param>
    /// <returns>导入结果；不支持目录不会写入 sdks.json。</returns>
    public async Task<LocalSdkImportResult> ImportLocalAsync(string selectedPath, string? customName = null)
    {
        var detection = SdkRootDetector.Detect(selectedPath);
        if (detection.Status != SdkStatus.Usable || detection.Type == SdkType.Unknown)
        {
            // NOTE: 不可识别目录是普通用户输入错误，不应污染 SDK 目录，也不应抛异常打断 UI 流程。
            return new LocalSdkImportResult(
                Success: false,
                Record: null,
                Detection: detection,
                Message: "所选目录不是受支持的 SDK 根目录。");
        }

        var catalog = await store.LoadOrCreateAsync(dataRoot);
        var record = CreateExternalRecord(detection, customName);
        var updatedCatalog = catalog with
        {
            Items = catalog.Items.Concat(new[] { record }).ToArray()
        };

        await store.SaveAsync(dataRoot, updatedCatalog);

        return new LocalSdkImportResult(
            Success: true,
            Record: record,
            Detection: detection,
            Message: "SDK 已导入。");
    }

    private static SdkRecord CreateExternalRecord(SdkDetectionResult detection, string? customName)
    {
        // NOTE: 不运行外部命令识别版本，避免 UI 卡顿和执行未知用户文件。
        // 版本号通过 SdkVersionResolver 纯文件/目录名解析（release、VERSION、目录名）填充。
        var version = SdkVersionResolver.ResolveVersion(detection.Type, detection.RootPath);

        return new SdkRecord(
            Id: CreateRecordId(detection.Type),
            Type: detection.Type,
            Name: ResolveDisplayName(detection.Type, customName, version),
            Version: version,
            Distribution: GetDefaultDistribution(detection.Type),
            Architecture: SdkArchitecture.Unknown,
            Source: SdkSourceKind.External,
            Path: detection.RootPath,
            Status: SdkRecordStatus.Usable,
            CreatedAt: DateTimeOffset.UtcNow,
            LastVerifiedAt: null);
    }

    private static string ResolveDisplayName(SdkType type, string? customName, string version)
    {
        // 用户自定义名称优先，保持其在 UI 中的完全控制权。
        if (!string.IsNullOrWhiteSpace(customName))
        {
            return customName;
        }

        var baseName = GetDefaultName(type);

        // 解析到真实版本时附加版本号，便于在 UI 区分多个同类型 SDK（如 "Java 17.0.11"）。
        return string.Equals(version, SdkVersionResolver.UnknownVersion, StringComparison.Ordinal)
            ? baseName
            : $"{baseName} {version}";
    }

    private static string CreateRecordId(SdkType type)
    {
        return $"{GetTypeSlug(type)}-{Guid.NewGuid():N}";
    }

    private static string GetDefaultName(SdkType type)
    {
        return type switch
        {
            SdkType.Java => "Java",
            SdkType.Maven => "Maven",
            SdkType.Node => "Node.js",
            SdkType.Go => "Go",
            _ => "SDK"
        };
    }

    private static string GetDefaultDistribution(SdkType type)
    {
        return type switch
        {
            SdkType.Java => "local-java",
            SdkType.Maven => "apache-maven",
            SdkType.Node => "nodejs",
            SdkType.Go => "go",
            _ => "unknown"
        };
    }

    private static string GetTypeSlug(SdkType type)
    {
        return type switch
        {
            SdkType.Java => "java",
            SdkType.Maven => "maven",
            SdkType.Node => "node",
            SdkType.Go => "go",
            _ => "sdk"
        };
    }
}
