// 文件用途：日志查看视图的代码隐藏，负责加载、展示、刷新、打开目录与清理过期日志。
//   - 通过 LogReaderService 异步读取并脱敏日志，再回 UI 线程刷新 ListView。
//   - 不写入 / 不修改任何已有服务；清理复用 LogRetentionService.PruneAsync。
// 创建日期：2026-06-11
// 语言版本要求：WinUI 3 / .NET 8 / C# 12
// 依赖库：Microsoft.UI.Xaml、DevSwitch.Core、System.Diagnostics
// NOTE: 合法授权学习使用，仅限本地环境。展示前每行已脱敏，UI 不暴露原始敏感信息。

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DevSwitch.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DevSwitch.App.Views;

/// <summary>
/// 日志查看视图（只读）。主窗口创建后调用 <see cref="Initialize"/> 注入数据根目录。
/// </summary>
public sealed partial class LogsView : UserControl
{
    // 一次最多展示的日志行数：与 LogReaderService 默认上限一致，避免超大文件卡 UI。
    private const int MaxLines = 500;

    // 通道下拉首项「全部」的固定文案；选中它表示不按通道过滤。
    private const string AllChannelsLabel = "全部";

    // 日志读取服务（纯读取 + 脱敏）。
    private readonly LogReaderService logReader = new();

    // 日志保留清理服务（复用既有实现，不修改）。
    private readonly LogRetentionService logRetention = new();

    // 绑定到 ListView 的可观察集合；UI 线程上增删，触发自动刷新。
    private readonly ObservableCollection<LogRow> logRows = new();

    // 当前 UI 线程的调度队列，用于把后台读取结果切回 UI 线程更新。
    private readonly DispatcherQueue dispatcherQueue;

    // 注入的数据根目录；Initialize 之前为 null，事件回调会做空判断保护。
    private string? dataRoot;

    // 防止初始化期间填充下拉触发的 SelectionChanged 反向再次加载。
    private bool isPopulatingChannels;

    // 防止快速重复进入日志页或连续点击刷新时叠加多个后台读取任务。
    private bool isLoading;

    /// <summary>
    /// 构造视图：加载 XAML 并绑定列表数据源。
    /// </summary>
    public LogsView()
    {
        this.InitializeComponent();

        // 捕获当前线程（UI 线程）的调度队列，供后台任务回调使用。
        this.dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        // 绑定可观察集合；后续仅需增删元素即可驱动 UI。
        this.LogListView.ItemsSource = this.logRows;
    }

    /// <summary>
    /// 初始化视图：保存数据根目录、填充通道下拉并首次异步加载日志。
    /// </summary>
    /// <param name="dataRoot">DevSwitch 数据根目录。</param>
    public void Initialize(string dataRoot)
    {
        this.dataRoot = dataRoot;
        Refresh();
    }

    /// <summary>
    /// 重新枚举日志通道并异步读取最近日志；可由主窗口重复进入日志页时安全调用。
    /// </summary>
    public void Refresh()
    {
        // 填充通道下拉（含「全部」），再触发加载；内部有防重入，避免快速连点叠加任务。
        this.PopulateChannels();
        _ = this.LoadLogsAsync();
    }

    /// <summary>
    /// 扫描通道并填充下拉框：首项固定为「全部」，其余按字母序排列，默认选中「全部」。
    /// </summary>
    private void PopulateChannels()
    {
        if (string.IsNullOrWhiteSpace(this.dataRoot))
        {
            return;
        }

        // 置位防抖标记：填充期间的 SelectionChanged 不触发重复加载。
        this.isPopulatingChannels = true;
        try
        {
            this.ChannelComboBox.Items.Clear();
            this.ChannelComboBox.Items.Add(AllChannelsLabel);

            // 读取去重排序后的通道列表，逐项加入下拉；IO 异常在本层兜底，不能让日志页首访失败。
            foreach (var channel in this.logReader.ListChannels(this.dataRoot))
            {
                this.ChannelComboBox.Items.Add(channel);
            }

            // 默认选中首项「全部」。
            this.ChannelComboBox.SelectedIndex = 0;
        }
        catch
        {
            this.ChannelComboBox.Items.Clear();
            this.ChannelComboBox.Items.Add(AllChannelsLabel);
            this.ChannelComboBox.SelectedIndex = 0;
        }
        finally
        {
            this.isPopulatingChannels = false;
        }
    }

    /// <summary>
    /// 异步读取当前选中通道的最近日志并刷新列表（含加载圈与空状态控制）。
    /// </summary>
    private async Task LoadLogsAsync()
    {
        if (string.IsNullOrWhiteSpace(this.dataRoot) || this.isLoading)
        {
            return;
        }

        this.isLoading = true;

        // 计算过滤通道：选中「全部」或未选则为 null（读取全部通道）。
        var selected = this.ChannelComboBox.SelectedItem as string;
        var channelFilter = (selected is null || selected == AllChannelsLabel) ? null : selected;

        // 进入加载态：显示加载圈，避免大文件读取时界面显得卡死。
        this.SetLoading(true);

        try
        {
            // 后台线程读取 + 脱敏，避免阻塞 UI 线程。
            IReadOnlyList<LogEntry> entries = await Task.Run(
                () => this.logReader.ReadRecentAsync(this.dataRoot!, channelFilter, MaxLines, CancellationToken.None))
                .ConfigureAwait(false);

            // 切回 UI 线程更新可观察集合（ObservableCollection 仅允许 UI 线程修改）。
            if (!this.dispatcherQueue.TryEnqueue(() =>
            {
                this.logRows.Clear();
                foreach (var entry in entries)
                {
                    this.logRows.Add(LogRow.FromEntry(entry));
                }

                // 根据是否有数据切换空状态与列表的可见性。
                this.UpdateEmptyState(entries.Count == 0);
                this.SetLoading(false);
                this.isLoading = false;
            }))
            {
                this.isLoading = false;
            }
        }
        catch (Exception)
        {
            // 读取失败（如 IO 异常）时回到可用状态并显示空提示，避免界面卡在加载态。
            if (!this.dispatcherQueue.TryEnqueue(() =>
            {
                this.logRows.Clear();
                this.UpdateEmptyState(true);
                this.SetLoading(false);
                this.isLoading = false;
            }))
            {
                this.isLoading = false;
            }
        }
    }

    /// <summary>
    /// 切换加载态：控制加载圈的激活与可见性。
    /// </summary>
    /// <param name="isLoading">是否处于加载中。</param>
    private void SetLoading(bool isLoading)
    {
        this.LoadingRing.IsActive = isLoading;
        this.LoadingRing.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 切换空状态：无日志时显示居中提示并隐藏列表，反之亦然。
    /// </summary>
    /// <param name="isEmpty">当前是否无日志。</param>
    private void UpdateEmptyState(bool isEmpty)
    {
        this.EmptyStatePanel.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        this.LogListView.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// 通道下拉选择变化：重新加载对应通道日志（填充期间不触发）。
    /// </summary>
    private void OnChannelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (this.isPopulatingChannels)
        {
            return;
        }

        _ = this.LoadLogsAsync();
    }

    /// <summary>
    /// 「刷新」按钮：重新枚举通道并重新读取日志。
    /// </summary>
    private void OnRefreshLogsClick(object sender, RoutedEventArgs e)
    {
        // 刷新时一并刷新通道列表（可能产生了新通道的日志文件）。
        Refresh();
    }

    /// <summary>
    /// 「打开日志目录」按钮：用资源管理器打开 logs 目录（不存在则先创建）。
    /// </summary>
    private void OnOpenLogsFolderClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(this.dataRoot))
        {
            return;
        }

        var logsDir = Path.Combine(this.dataRoot, "logs");

        try
        {
            // 目录不存在先创建，确保 Process.Start 能成功打开。
            Directory.CreateDirectory(logsDir);

            // UseShellExecute=true：交由 Shell 用默认程序（资源管理器）打开目录。
            Process.Start(new ProcessStartInfo(logsDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // 打开失败仅提示，不影响视图其它功能。
            _ = this.ShowMessageAsync("打开日志目录失败", ex.Message);
        }
    }

    /// <summary>
    /// 「清理过期」按钮：调用保留服务清理超期日志，完成后刷新并提示结果。
    /// </summary>
    private async void OnPruneLogsClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(this.dataRoot))
        {
            return;
        }

        // 清理期间显示加载圈，避免误以为无响应。
        this.SetLoading(true);

        try
        {
            // 复用既有保留服务，按默认 14 天清理（不修改该服务）。
            var result = await this.logRetention
                .PruneAsync(this.dataRoot, LogRetentionPolicy.DefaultRetentionDays, CancellationToken.None)
                .ConfigureAwait(false);

            var deletedCount = result.DeletedFiles.Count;

            // 切回 UI 线程：重新加载并提示清理结果。
            this.dispatcherQueue.TryEnqueue(async () =>
            {
                this.PopulateChannels();
                await this.LoadLogsAsync().ConfigureAwait(true);
                await this.ShowMessageAsync(
                    "清理完成",
                    deletedCount > 0 ? $"已清理 {deletedCount} 个过期日志文件。" : "没有需要清理的过期日志。")
                    .ConfigureAwait(true);
            });
        }
        catch (Exception ex)
        {
            this.dispatcherQueue.TryEnqueue(async () =>
            {
                this.SetLoading(false);
                await this.ShowMessageAsync("清理失败", ex.Message).ConfigureAwait(true);
            });
        }
    }

    /// <summary>
    /// 弹出简单的提示对话框（用当前视图的 XamlRoot 作为承载）。
    /// </summary>
    /// <param name="title">标题。</param>
    /// <param name="message">正文。</param>
    private async Task ShowMessageAsync(string title, string message)
    {
        if (this.XamlRoot is null)
        {
            // NOTE: 首次导航或窗口关闭竞态下可能尚无 XamlRoot；不能因提示失败二次触发闪退。
            return;
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "好",
            // ContentDialog 必须设置 XamlRoot 才能在桌面应用中正确显示。
            XamlRoot = this.XamlRoot,
        };

        await dialog.ShowAsync();
    }

    /// <summary>
    /// 绑定到 ListView 每一行的视图模型（把 LogEntry 转为可直接展示的字段）。
    /// </summary>
    private sealed class LogRow
    {
        /// <summary>格式化后的时间戳文本；无时间戳显示占位。</summary>
        public string TimestampText { get; init; } = string.Empty;

        /// <summary>来源通道。</summary>
        public string Channel { get; init; } = string.Empty;

        /// <summary>已脱敏的消息正文。</summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>
        /// 从 <see cref="LogEntry"/> 构造行视图模型。
        /// </summary>
        /// <param name="entry">日志条目。</param>
        /// <returns>用于 UI 绑定的行模型。</returns>
        public static LogRow FromEntry(LogEntry entry)
        {
            return new LogRow
            {
                // 有时间戳则按「yyyy-MM-dd HH:mm:ss」展示，无则占位 "--"。
                TimestampText = entry.Timestamp is { } ts
                    ? ts.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                    : "--",
                Channel = entry.Channel,
                Message = entry.Message,
            };
        }
    }
}
