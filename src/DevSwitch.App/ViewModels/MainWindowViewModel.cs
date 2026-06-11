// 文件用途：主窗口 ViewModel，负责把真实 sdks.json catalog 投影到 WinUI SDK 管理页。
// 创建/修改日期：2026-06-10
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：DevSwitch.Core、Microsoft.UI.Xaml
// NOTE: 合法授权学习使用，仅限本地环境。
//       该 ViewModel 只负责 UI 状态和真实 catalog 投影；导入、切换等副作用由窗口事件桥接到 Core 服务。

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
public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly ISdkCatalogProvider catalogProvider;
    private string selectedCategory = "Java";
    private bool isLoading;
    private string? loadErrorText;
    private bool hasLoadedCatalog;

    // 状态筛选下拉选中的离散值；默认 All 等价于不过滤，保持原"全部"行为。
    private SdkStatusFilter selectedStatusFilter = SdkStatusFilter.All;

    // 可见行缓存：避免每次绑定读取/空状态判断都重新 LINQ 过滤并分配新数组。
    // 仅在 SelectedCategory / SelectedStatusFilter 变化、Versions 重新加载或单行 Status 变更时由 RebuildVisibleVersions 重算。
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
    /// 当前生效的状态筛选值。默认 All（"全部"）。
    /// </summary>
    /// <remarks>
    /// 由 MainWindow 状态 ComboBox 的 SelectionChanged 桥接写入。每次变更都重算可见行缓存并通知 UI。
    /// </remarks>
    public SdkStatusFilter SelectedStatusFilter
    {
        get => selectedStatusFilter;
        set
        {
            if (selectedStatusFilter == value)
            {
                return;
            }

            selectedStatusFilter = value;
            RebuildVisibleVersions();
            NotifyVisibleStateChanged();
            OnPropertyChanged(nameof(SelectedStatusFilter));
        }
    }

    /// <summary>
    /// 从真实 sdks.json 映射出的全量 SDK 行。
    /// </summary>
    public ObservableCollection<SdkVersionRow> Versions { get; }

    /// <summary>
    /// 当前分类与过滤器下需要展示的 SDK 行。
    /// </summary>
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
                return "config/sdks.json 暂无 SDK 记录，可通过\u201c添加本地 SDK\u201d导入本机已有工具链。";
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
    public Visibility EmptyStateVisibility => visibleVersionsCache.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 首次加载真实 SDK catalog。
    /// </summary>
    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        return RefreshAsync(cancellationToken);
    }

    /// <summary>
    /// 重新读取真实 SDK catalog 并刷新 UI 行。
    /// </summary>
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

            ReplaceVersions(rows);

            hasLoadedCatalog = true;
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
            ReplaceVersions(Array.Empty<SdkVersionRow>());
            hasLoadedCatalog = true;
            LoadErrorText = ex.Message;
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
    /// 把命令验证结果回写到对应 SDK 行：
    /// - 验证成功 → 维持原"使用中/可用"语义（活跃保持 Active 文案，否则置为 Usable）。
    /// - 验证失败 / 命令未启动 / 超时 / 解析失败 → 置为「不可用」并禁用切换按钮。
    /// 回写后立即触发 INPC，让徽章 UI 重绘；同时重算可见行，使当前过滤器即时生效。
    /// </summary>
    /// <param name="rowId">SDK 记录稳定 ID（与 row.Id 对应）。</param>
    /// <param name="success">命令验证是否成功（成功=Verified；其余皆视为失败）。</param>
    /// <returns>true 表示找到了对应行并完成回写；false 表示未找到。</returns>
    public bool ApplyCommandVerificationResult(string rowId, bool success)
    {
        if (string.IsNullOrEmpty(rowId))
        {
            return false;
        }

        var target = Versions.FirstOrDefault(row => string.Equals(row.Id, rowId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return false;
        }

        // 失败：统一钳为"不可用"。同一 SDK 类型只允许一条"使用中"，由 catalog active 指针在下次刷新时自愈。
        if (!success)
        {
            target.Status = "不可用";
            target.Operation = "查看原因";
            target.CanSwitch = false;
            return true;
        }

        // 成功：原本就是"使用中"则保持；否则一律落到"可用"。
        if (!string.Equals(target.Status, "使用中", StringComparison.Ordinal))
        {
            target.Status = "可用";
            target.Operation = "切换";
            target.CanSwitch = true;
        }

        return true;
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
    /// 用新行集合一次性替换 <see cref="Versions"/> 内容；同时管理行 PropertyChanged 订阅。
    /// </summary>
    /// <remarks>
    /// 旧行需要先解订阅，避免委托回调进入已被 GC 的 ViewModel；新行订阅 Status 变更，
    /// 触发可见集合即时重算（例如"可用→不可用"切换后，当前是"可用"过滤时该行立即消失）。
    /// </remarks>
    private void ReplaceVersions(IReadOnlyList<SdkVersionRow> rows)
    {
        foreach (var row in Versions)
        {
            row.PropertyChanged -= OnVersionRowPropertyChanged;
        }

        Versions.Clear();
        foreach (var row in rows)
        {
            row.PropertyChanged += OnVersionRowPropertyChanged;
            Versions.Add(row);
        }
    }

    /// <summary>
    /// 单行 Status 变更联动：让过滤器 + 列表绑定即时反映新状态。
    /// </summary>
    private void OnVersionRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SdkVersionRow.Status))
        {
            return;
        }

        RebuildVisibleVersions();
        NotifyVisibleStateChanged();
    }

    /// <summary>
    /// 依据当前分类和状态筛选重算可见行缓存。
    /// </summary>
    private void RebuildVisibleVersions()
    {
        visibleVersionsCache = Versions
            .Where(version => version.Category == selectedCategory)
            .Where(version => SdkStatusFilterMatcher.Matches(version.Status, selectedStatusFilter))
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
