// 文件用途：主窗口 ViewModel，负责把真实 sdks.json catalog 投影到 WinUI SDK 管理页。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：DevSwitch.Core、Microsoft.UI.Xaml

using System.Collections.ObjectModel;
using System.ComponentModel;
using DevSwitch.App.Models;
using DevSwitch.App.Services;
using DevSwitch.Core;
using Microsoft.UI.Xaml;

namespace DevSwitch.App.ViewModels;

/// <summary>
/// 主窗口 ViewModel。
/// </summary>
/// <remarks>
/// 该 ViewModel 只负责 UI 状态和真实 catalog 投影；导入、切换等副作用由窗口事件桥接到 Core 服务。
/// </remarks>
public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly ISdkCatalogProvider catalogProvider;
    private string selectedCategory = "Java";
    private bool isLoading;
    private string? loadErrorText;
    private bool hasLoadedCatalog;

    // 可见行缓存：避免每次绑定读取/空状态判断都重新 LINQ 过滤并分配新数组。
    // 仅在 SelectedCategory 变化或 Versions 重新加载时由 RebuildVisibleVersions 重算一次。
    private IReadOnlyList<SdkVersionRow> visibleVersionsCache = Array.Empty<SdkVersionRow>();

    /// <summary>
    /// 创建主窗口 ViewModel。
    /// </summary>
    /// <param name="catalogProvider">真实 SDK catalog provider。</param>
    public MainWindowViewModel(ISdkCatalogProvider catalogProvider)
    {
        this.catalogProvider = catalogProvider ?? throw new ArgumentNullException(nameof(catalogProvider));
        Categories = new ObservableCollection<string>
        {
            "Java",
            "Maven",
            "Node.js",
            "Go",
        };

        Versions = new ObservableCollection<SdkVersionRow>();
    }

    /// <summary>
    /// 属性变化事件。
    /// 使用普通 C# MVVM 机制，避免为当前轻量 UI 引入额外 MVVM 依赖。
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 左侧 SDK 分类列表。
    /// </summary>
    public ObservableCollection<string> Categories { get; }

    /// <summary>
    /// 当前选中的 SDK 分类。
    /// </summary>
    public string SelectedCategory
    {
        get => selectedCategory;
        set
        {
            if (selectedCategory == value)
            {
                return;
            }

            selectedCategory = value;
            // 分类切换时重算可见行缓存一次，再统一发出相关绑定属性的变更通知。
            RebuildVisibleVersions();
            NotifyVisibleStateChanged();
            OnPropertyChanged(nameof(SelectedCategory));
        }
    }

    /// <summary>
    /// 从真实 sdks.json 映射出的全量 SDK 行。
    /// </summary>
    public ObservableCollection<SdkVersionRow> Versions { get; }

    /// <summary>
    /// 当前分类下需要展示的 SDK 行。
    /// </summary>
    /// <remarks>
    /// 直接返回缓存结果，不在 getter 内做 LINQ 过滤；缓存由 <see cref="RebuildVisibleVersions"/>
    /// 在分类切换或 catalog 重新加载时统一刷新，避免绑定层多次访问触发重复计算与数组分配。
    /// </remarks>
    public IReadOnlyList<SdkVersionRow> VisibleVersions => visibleVersionsCache;

    /// <summary>
    /// 是否正在异步读取 catalog。
    /// </summary>
    public bool IsLoading
    {
        get => isLoading;
        private set
        {
            if (isLoading == value)
            {
                return;
            }

            isLoading = value;
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(CatalogBadgeText));
            OnPropertyChanged(nameof(CatalogNoticeText));
        }
    }

    /// <summary>
    /// 最近一次读取失败的错误说明；为空表示无错误。
    /// </summary>
    public string? LoadErrorText
    {
        get => loadErrorText;
        private set
        {
            if (loadErrorText == value)
            {
                return;
            }

            loadErrorText = value;
            OnPropertyChanged(nameof(LoadErrorText));
            OnPropertyChanged(nameof(CatalogBadgeText));
            OnPropertyChanged(nameof(CatalogNoticeText));
        }
    }

    /// <summary>
    /// SDK 页面右上角 catalog 状态徽标。
    /// </summary>
    public string CatalogBadgeText
    {
        get
        {
            if (IsLoading)
            {
                return "加载中";
            }

            if (!string.IsNullOrWhiteSpace(LoadErrorText))
            {
                return "读取失败";
            }

            return hasLoadedCatalog ? "真实配置" : "未加载";
        }
    }

    /// <summary>
    /// SDK 页面提示条文本。
    /// </summary>
    public string CatalogNoticeText
    {
        get
        {
            if (IsLoading)
            {
                return "正在读取 config/sdks.json，请稍候。";
            }

            if (!string.IsNullOrWhiteSpace(LoadErrorText))
            {
                return $"无法读取 config/sdks.json：{LoadErrorText}";
            }

            if (Versions.Count == 0)
            {
                return "config/sdks.json 暂无 SDK 记录，可通过“添加本地 SDK”导入本机已有工具链。";
            }

            return "已从 config/sdks.json 读取 SDK 目录。切换 PATH/JAVA_HOME/NODE_HOME 将在后续步骤接入。";
        }
    }

    /// <summary>
    /// 空状态提示文案。
    /// </summary>
    public string EmptyStateText => IsLoading
        ? "正在读取 SDK 配置"
        : string.IsNullOrWhiteSpace(LoadErrorText)
            ? $"当前还没有 {SelectedCategory} 版本"
            : "SDK 配置读取失败，请检查文件格式或权限。";

    /// <summary>
    /// 空状态区域的可见性。
    /// </summary>
    /// <remarks>
    /// 基于已缓存的可见行结果判断，不再次触发过滤计算。
    /// </remarks>
    public Visibility EmptyStateVisibility => visibleVersionsCache.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 首次加载真实 SDK catalog。
    /// </summary>
    /// <param name="cancellationToken">窗口生命周期取消令牌。</param>
    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        return RefreshAsync(cancellationToken);
    }

    /// <summary>
    /// 重新读取真实 SDK catalog 并刷新 UI 行。
    /// </summary>
    /// <param name="cancellationToken">窗口生命周期取消令牌。</param>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        LoadErrorText = null;

        try
        {
            var catalog = await catalogProvider.LoadOrCreateAsync(cancellationToken);
            var rows = SdkCatalogViewService.ToRows(catalog).Select(ToSdkVersionRow).ToArray();

            // 先在本地构建好完整行集合，再用单次替换刷新 Versions，
            // 避免逐项 Add 在大列表时产生 CollectionChanged 通知风暴。
            ReplaceVersions(rows);

            hasLoadedCatalog = true;
            // catalog 重新加载后重算一次可见行缓存，再统一通知绑定层。
            RebuildVisibleVersions();
            NotifyVisibleStateChanged();
            OnPropertyChanged(nameof(CatalogBadgeText));
            OnPropertyChanged(nameof(CatalogNoticeText));
        }
        catch (OperationCanceledException)
        {
            // NOTE: 窗口关闭时取消加载是正常生命周期事件，不展示错误给用户。
        }
        catch (Exception ex)
        {
            Versions.Clear();
            hasLoadedCatalog = true;
            LoadErrorText = ex.Message;
            // 读取失败时清空可见行缓存，确保空状态判断与列表绑定一致。
            RebuildVisibleVersions();
            NotifyVisibleStateChanged();
        }
        finally
        {
            IsLoading = false;
            NotifyVisibleStateChanged();
            OnPropertyChanged(nameof(CatalogNoticeText));
        }
    }

    /// <summary>
    /// 选择 SDK 分类。
    /// </summary>
    /// <param name="category">用户点击的分类名称。</param>
    public void SelectCategory(string category)
    {
        if (Categories.Contains(category))
        {
            SelectedCategory = category;
        }
    }

    /// <summary>
    /// 将 Core 行 DTO 转成当前 XAML 绑定的行模型。
    /// </summary>
    private static SdkVersionRow ToSdkVersionRow(SdkCatalogRow row)
    {
        return new SdkVersionRow
        {
            Id = row.Id,
            Type = row.Type,
            Category = row.Category,
            Name = row.Name,
            Version = row.Version,
            Source = row.Source,
            Path = row.Path,
            Status = row.Status,
            Operation = row.Operation,
            CanSwitch = row.CanSwitch,
        };
    }

    /// <summary>
    /// 用新行集合一次性替换 <see cref="Versions"/> 内容。
    /// </summary>
    /// <param name="rows">本地已构建完成的全量行集合。</param>
    /// <remarks>
    /// ObservableCollection 没有原生批量接口，逐项 Add 会按行触发 CollectionChanged。
    /// 这里先 Clear（单次 Reset 通知），再顺序填充；由于该集合不直接绑定到任何控件
    /// （ListView 绑定的是 VisibleVersions 缓存），填充阶段不会引发 UI 逐行重建，
    /// 从而把刷新期间的通知开销压到最小。
    /// </remarks>
    private void ReplaceVersions(IReadOnlyList<SdkVersionRow> rows)
    {
        Versions.Clear();
        foreach (var row in rows)
        {
            Versions.Add(row);
        }
    }

    /// <summary>
    /// 依据当前分类重算可见行缓存。
    /// </summary>
    /// <remarks>
    /// 集中在此处做一次 LINQ 过滤 + 数组分配，调用方随后只读取缓存，
    /// 避免绑定层与空状态判断重复触发过滤计算。
    /// </remarks>
    private void RebuildVisibleVersions()
    {
        visibleVersionsCache = Versions
            .Where(version => version.Category == selectedCategory)
            .ToArray();
    }

    /// <summary>
    /// 通知与列表可见状态相关的绑定属性。
    /// </summary>
    private void NotifyVisibleStateChanged()
    {
        OnPropertyChanged(nameof(VisibleVersions));
        OnPropertyChanged(nameof(EmptyStateText));
        OnPropertyChanged(nameof(EmptyStateVisibility));
        OnPropertyChanged(nameof(CatalogNoticeText));
    }

    /// <summary>
    /// 通知绑定层某个属性已变化。
    /// </summary>
    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
