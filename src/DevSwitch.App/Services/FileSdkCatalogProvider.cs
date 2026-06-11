// 文件用途：App 层真实 sdks.json 文件 provider，封装数据根和仓储细节。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：DevSwitch.Core

using DevSwitch.Core;

namespace DevSwitch.App.Services;

/// <summary>
/// 从 DevSwitch 数据根目录读取真实 SDK catalog。
/// </summary>
public sealed class FileSdkCatalogProvider : ISdkCatalogProvider
{
    private readonly string dataRoot;
    private readonly SdkCatalogStore store;

    /// <summary>
    /// 创建文件 catalog provider。
    /// </summary>
    /// <param name="dataRoot">DevSwitch 数据根目录。</param>
    /// <param name="store">可选仓储实例，测试或组合根可注入。</param>
    /// <exception cref="ArgumentException">数据根为空时抛出。</exception>
    public FileSdkCatalogProvider(string dataRoot, SdkCatalogStore? store = null)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("Data root is required.", nameof(dataRoot));
        }

        this.dataRoot = dataRoot;
        this.store = store ?? new SdkCatalogStore();
    }

    /// <inheritdoc />
    public async Task<SdkCatalog> LoadOrCreateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var catalog = await store.LoadOrCreateAsync(dataRoot);
        cancellationToken.ThrowIfCancellationRequested();

        // 加载自愈：以 active 指针为唯一真相，把历史脏数据（多条 status=active 残留）收敛，
        // 确保同类型最多一条「使用中」。若发生修正则回写，持久化清洗。
        var reconciled = SdkCatalogViewService.ReconcileActiveStatus(catalog);
        if (!ReferenceEquals(reconciled, catalog) && !CatalogStatusesEqual(catalog, reconciled))
        {
            try
            {
                await store.SaveAsync(dataRoot, reconciled);
            }
            catch
            {
                // 回写失败不影响展示；ToRow 仍只按 active 指针显示，不会出现多条「使用中」。
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return reconciled;
    }

    /// <summary>
    /// 比较两个 catalog 的记录状态是否完全一致，用于判断 reconcile 是否产生实际变化。
    /// </summary>
    private static bool CatalogStatusesEqual(SdkCatalog left, SdkCatalog right)
    {
        if (left.Items.Count != right.Items.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Items.Count; i++)
        {
            if (left.Items[i].Status != right.Items[i].Status)
            {
                return false;
            }
        }

        return true;
    }
}
