// 文件用途：将 sdks.json 公开目录模型投影为 GUI/CLI 可消费的 SDK 列表行。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.Linq

namespace DevSwitch.Core;

/// <summary>
/// GUI SDK 列表行 DTO。
/// </summary>
/// <param name="Id">SDK 记录稳定 ID，用于后续切换或详情操作。</param>
/// <param name="Type">SDK 类型。</param>
/// <param name="Category">面向 UI 的分类名称。</param>
/// <param name="Name">显示名称。</param>
/// <param name="Version">版本号。</param>
/// <param name="Source">来源文案。</param>
/// <param name="Path">SDK 根目录路径。</param>
/// <param name="Status">状态文案。</param>
/// <param name="Operation">主操作文案。</param>
/// <param name="CanSwitch">是否允许发起切换。</param>
public sealed record SdkCatalogRow(
    string Id,
    SdkType Type,
    string Category,
    string Name,
    string Version,
    string Source,
    string Path,
    string Status,
    string Operation,
    bool CanSwitch);

/// <summary>
/// 将真实 SDK catalog 加载并转换为 UI 列表行。
/// </summary>
public sealed class SdkCatalogViewService
{
    private readonly SdkCatalogStore store;

    /// <summary>
    /// 创建 SDK catalog 视图服务。
    /// </summary>
    /// <param name="store">可选仓储实例，测试可注入；为空时使用默认文件仓储。</param>
    public SdkCatalogViewService(SdkCatalogStore? store = null)
    {
        this.store = store ?? new SdkCatalogStore();
    }

    /// <summary>
    /// 从数据根加载 sdks.json 并按需过滤为 UI 行。
    /// </summary>
    /// <param name="dataRoot">DevSwitch 数据根目录。</param>
    /// <param name="type">可选 SDK 类型过滤；为空时返回全部。</param>
    /// <returns>按照 catalog 中记录顺序生成的列表行。</returns>
    public async Task<IReadOnlyList<SdkCatalogRow>> LoadRowsAsync(string dataRoot, SdkType? type = null)
    {
        var catalog = await store.LoadOrCreateAsync(dataRoot);
        return ToRows(catalog, type);
    }

    /// <summary>
    /// 将 catalog 内存模型转换为 UI 行，便于 ViewModel 和测试复用同一公开行为。
    /// </summary>
    /// <param name="catalog">真实 SDK 目录。</param>
    /// <param name="type">可选 SDK 类型过滤。</param>
    /// <returns>转换后的列表行。</returns>
    public static IReadOnlyList<SdkCatalogRow> ToRows(SdkCatalog catalog, SdkType? type = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        return catalog.Items
            .Where(record => type is null || record.Type == type.Value)
            .Select(record => ToRow(record, catalog.Active))
            .ToArray();
    }

    /// <summary>
    /// 将单条 SDK 记录转换为 UI 行。
    /// active 指针优先于记录自身状态，确保 sdks.json 的当前选择能被准确展示。
    /// </summary>
    /// <param name="record">SDK 记录。</param>
    /// <param name="active">当前 active 指针集合。</param>
    /// <returns>UI 可消费的 SDK 行。</returns>
    public static SdkCatalogRow ToRow(SdkRecord record, ActiveSdkSet active)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(active);

        var isActive = string.Equals(GetActiveRecordId(active, record.Type), record.Id, StringComparison.OrdinalIgnoreCase);
        // active 指针是“使用中”的唯一权威来源。当指针未命中本记录时，即便 record.Status==Active（sdks.json 脏数据，
        // 历史切换没清干净），也必须钳为 Usable 显示，确保同一类型最多一条“使用中”。
        // Unavailable/Unverified 不是“使用中”状态，保持原样照常显示。
        var displayStatus = isActive
            ? SdkRecordStatus.Active
            : (record.Status == SdkRecordStatus.Active ? SdkRecordStatus.Usable : record.Status);
        var (statusText, operation, canSwitch) = MapStatus(displayStatus);

        return new SdkCatalogRow(
            Id: record.Id,
            Type: record.Type,
            Category: GetCategoryName(record.Type),
            Name: record.Name,
            Version: record.Version,
            Source: GetSourceText(record.Source),
            Path: record.Path,
            Status: statusText,
            Operation: operation,
            CanSwitch: canSwitch);
    }

    /// <summary>
    /// 以 active 指针为唯一真相，自愈 catalog 中残留的脏 Active 状态，返回修正后的新 catalog（纯函数）。
    /// 规则：
    /// - active 指针命中的记录：status 设为 Active（即便原来是 Usable 也修正）。
    /// - 同类型其它记录若 status==Active：改为 Usable。
    /// - active 指针为 null 的类型：该类型所有 status==Active 残留改为 Usable。
    /// - 不改 Unavailable/Unverified 状态，不删记录，不动 active 指针本身。
    /// </summary>
    /// <param name="catalog">待修正的 SDK 目录。</param>
    /// <returns>状态修正后的新 catalog；record 与 catalog 均为 record 类型，用 with 表达式生成副本。</returns>
    public static SdkCatalog ReconcileActiveStatus(SdkCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var reconciledItems = catalog.Items
            .Select(record =>
            {
                // 该记录是否为其类型 active 指针命中的目标。
                var isActive = string.Equals(GetActiveRecordId(catalog.Active, record.Type), record.Id, StringComparison.OrdinalIgnoreCase);

                if (isActive)
                {
                    // 命中指针：确保为 Active（避免持久化时漏写 Active 的情况）。
                    return record.Status == SdkRecordStatus.Active ? record : record with { Status = SdkRecordStatus.Active };
                }

                // 未命中指针但残留 Active：钳为 Usable；其它状态保持原样。
                return record.Status == SdkRecordStatus.Active ? record with { Status = SdkRecordStatus.Usable } : record;
            })
            .ToArray();

        return catalog with { Items = reconciledItems };
    }

    /// <summary>
    /// 获取 SDK 类型对应的 active 记录 ID。
    /// </summary>
    private static string? GetActiveRecordId(ActiveSdkSet active, SdkType type)
    {
        return type switch
        {
            SdkType.Java => active.Java,
            SdkType.Maven => active.Maven,
            SdkType.Node => active.Node,
            SdkType.Go => active.Go,
            SdkType.Rust => active.Rust,
            _ => null,
        };
    }

    /// <summary>
    /// 将 SDK 类型转换为当前 UI 使用的分类文案。
    /// </summary>
    public static string GetCategoryName(SdkType type)
    {
        return type switch
        {
            SdkType.Java => "Java",
            SdkType.Maven => "Maven",
            SdkType.Node => "Node.js",
            SdkType.Go => "Go",
            SdkType.Rust => "Rust",
            _ => "SDK",
        };
    }

    /// <summary>
    /// 将 UI 分类文案转换为 SDK 类型。
    /// </summary>
    public static SdkType GetTypeFromCategory(string category)
    {
        return category switch
        {
            "Java" => SdkType.Java,
            "Maven" => SdkType.Maven,
            "Node.js" => SdkType.Node,
            "Go" => SdkType.Go,
            "Rust" => SdkType.Rust,
            _ => SdkType.Unknown,
        };
    }

    /// <summary>
    /// 将 SDK 来源枚举转换为中文 UI 文案。
    /// </summary>
    private static string GetSourceText(SdkSourceKind source)
    {
        return source switch
        {
            SdkSourceKind.Managed => "托管",
            SdkSourceKind.External => "外部",
            _ => "未知",
        };
    }

    /// <summary>
    /// 将记录状态映射为 UI 状态、操作和按钮可用性。
    /// </summary>
    private static (string StatusText, string Operation, bool CanSwitch) MapStatus(SdkRecordStatus status)
    {
        return status switch
        {
            SdkRecordStatus.Active => ("使用中", "当前", false),
            SdkRecordStatus.Usable => ("可用", "切换", true),
            SdkRecordStatus.Unverified => ("未验证", "验证", false),
            SdkRecordStatus.Unavailable => ("不可用", "查看原因", false),
            _ => ("未知", "查看", false),
        };
    }
}
