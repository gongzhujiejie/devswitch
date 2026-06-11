// 文件用途：DevSwitch「配置档案」视图的代码隐藏，负责加载档案列表、刷新 UI 与新建/应用/重命名/删除交互。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8（WinUI 3）
// 依赖库：Microsoft.UI.Xaml、DevSwitch.Core
// NOTE: 合法授权学习使用，仅限本地环境。本视图自包含，不依赖 MainWindow 的切换服务，避免耦合。

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using DevSwitch.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DevSwitch.App.Views;

/// <summary>
/// 配置档案页面。由主窗口 <c>new ProfilesView()</c> 创建后调用 <see cref="Initialize"/> 注入数据根并加载列表。
/// </summary>
public sealed partial class ProfilesView : UserControl
{
    // NOTE: Store 无状态，可整个视图复用一个实例，避免每次操作重复构造。
    private readonly SdkProfileStore _profileStore = new();

    // NOTE: ItemsControl 的数据源；用 ObservableCollection 便于增删后 UI 自动反映。
    private readonly ObservableCollection<ProfileListItem> _items = new();

    // NOTE: Initialize 注入的数据根目录；未初始化时为 null，所有数据操作前先校验。
    private string? _dataRoot;

    /// <summary>
    /// 构造函数：仅初始化 XAML 组件并绑定列表数据源，不触发任何 IO。
    /// </summary>
    public ProfilesView()
    {
        InitializeComponent();
        ProfilesItemsControl.ItemsSource = _items;
    }

    /// <summary>
    /// 注入 DevSwitch 数据根目录并异步加载档案列表刷新 UI。
    /// 由主窗口在创建视图后调用一次。
    /// </summary>
    /// <param name="dataRoot">DevSwitch 数据根目录。</param>
    public void Initialize(string dataRoot)
    {
        _dataRoot = dataRoot;
        // NOTE: 构造/初始化方法不能 await，这里 fire-and-forget 异步刷新；异常在内部捕获并提示，避免吞掉。
        _ = RefreshAsync();
    }

    /// <summary>
    /// 从磁盘加载档案集合并重建列表项，同时切换空状态显隐。
    /// </summary>
    private async Task RefreshAsync()
    {
        if (string.IsNullOrWhiteSpace(_dataRoot))
        {
            return;
        }

        try
        {
            var catalog = await _profileStore.LoadOrCreateAsync(_dataRoot);

            // NOTE: 全量重建列表，逻辑简单且档案数量通常很少，性能足够且不会出现增量同步偏差。
            _items.Clear();
            foreach (var profile in catalog.Profiles)
            {
                _items.Add(ProfileListItem.FromProfile(profile));
            }

            UpdateEmptyState();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("加载失败", $"读取配置档案时出错：{ex.Message}");
        }
    }

    /// <summary>
    /// 根据当前列表是否为空，切换列表与空状态面板的可见性。
    /// </summary>
    private void UpdateEmptyState()
    {
        var isEmpty = _items.Count == 0;
        EmptyStatePanel.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        ProfilesScrollViewer.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// 「新建档案」：弹出输入框获取名称，非空则 AddAsync 并刷新。
    /// </summary>
    private async void CreateProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_dataRoot))
        {
            return;
        }

        var name = await PromptForNameAsync("新建配置档案", "请输入档案名称", string.Empty);
        if (string.IsNullOrWhiteSpace(name))
        {
            // NOTE: 用户取消或留空，直接放弃，不创建空名档案。
            return;
        }

        try
        {
            // NOTE: 首版新建空 entries 档案，后续接入 SDK 选择 UI 后再补充条目。
            await _profileStore.AddAsync(_dataRoot, name.Trim(), Array.Empty<SdkProfileEntry>());
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("新建失败", ex.Message);
        }
    }

    /// <summary>
    /// 「应用」：当前仅提示，真正的切换接线由后续接入，避免与主窗口切换服务耦合。
    /// </summary>
    private async void ApplyProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetProfileId(sender, out var profileId))
        {
            var item = _items.FirstOrDefault(p => p.Id == profileId);
            var name = item?.Name ?? profileId;
            await ShowMessageAsync("应用配置档案", $"将应用档案「{name}」（切换接线由后续接入）。");
        }
    }

    /// <summary>
    /// 「重命名」：弹出输入框获取新名称，非空则 RenameAsync 并刷新。
    /// </summary>
    private async void RenameProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_dataRoot) || !TryGetProfileId(sender, out var profileId))
        {
            return;
        }

        var current = _items.FirstOrDefault(p => p.Id == profileId);
        var newName = await PromptForNameAsync("重命名档案", "请输入新的档案名称", current?.Name ?? string.Empty);
        if (string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        try
        {
            var renamed = await _profileStore.RenameAsync(_dataRoot, profileId, newName.Trim());
            if (renamed is null)
            {
                // NOTE: id 未命中（可能已被并发删除），提示后刷新到最新状态。
                await ShowMessageAsync("重命名失败", "未找到该配置档案，可能已被删除。");
            }

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("重命名失败", ex.Message);
        }
    }

    /// <summary>
    /// 「删除」：二次确认后 RemoveAsync 并刷新。
    /// </summary>
    private async void DeleteProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_dataRoot) || !TryGetProfileId(sender, out var profileId))
        {
            return;
        }

        var item = _items.FirstOrDefault(p => p.Id == profileId);
        var name = item?.Name ?? profileId;

        // NOTE: 删除不可逆，必须二次确认；ContentDialog 返回 Primary 才执行。
        var confirmDialog = new ContentDialog
        {
            Title = "删除配置档案",
            Content = $"确定要删除档案「{name}」吗？此操作无法撤销。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };

        var result = await confirmDialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await _profileStore.RemoveAsync(_dataRoot, profileId);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("删除失败", ex.Message);
        }
    }

    /// <summary>
    /// 从触发事件的按钮 Tag 中取出 ProfileId。
    /// </summary>
    /// <param name="sender">事件发送者，预期为携带 Tag=ProfileId 的按钮。</param>
    /// <param name="profileId">取出的档案 id。</param>
    /// <returns>成功取到非空 id 返回 true。</returns>
    private static bool TryGetProfileId(object sender, out string profileId)
    {
        if (sender is FrameworkElement element && element.Tag is string id && !string.IsNullOrWhiteSpace(id))
        {
            profileId = id;
            return true;
        }

        profileId = string.Empty;
        return false;
    }

    /// <summary>
    /// 弹出带单行输入框的对话框，返回用户输入文本；取消返回 null。
    /// </summary>
    /// <param name="title">对话框标题。</param>
    /// <param name="placeholder">输入框占位提示。</param>
    /// <param name="initialText">输入框初始文本（重命名时回填当前名称）。</param>
    /// <returns>用户确认时返回输入文本，取消时返回 null。</returns>
    private async Task<string?> PromptForNameAsync(string title, string placeholder, string initialText)
    {
        var input = new TextBox
        {
            PlaceholderText = placeholder,
            Text = initialText,
            // NOTE: 限制长度，避免异常超长名称影响列表展示与文件可读性。
            MaxLength = 60,
        };

        var dialog = new ContentDialog
        {
            Title = title,
            Content = input,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? input.Text : null;
    }

    /// <summary>
    /// 弹出仅含确定按钮的提示对话框。
    /// </summary>
    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };

        await dialog.ShowAsync();
    }

    /// <summary>
    /// 列表项视图模型：把 <see cref="SdkProfile"/> 投影成 UI 绑定所需的扁平字段。
    /// </summary>
    private sealed class ProfileListItem
    {
        /// <summary>档案稳定 id，供操作按钮 Tag 定位。</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>档案名称。</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>SDK 条目摘要文本，用于卡片副标题展示。</summary>
        public string Summary { get; init; } = string.Empty;

        /// <summary>
        /// 由领域模型构造列表项，并生成可读的 SDK 条目摘要。
        /// </summary>
        /// <param name="profile">源配置档案。</param>
        /// <returns>用于绑定的列表项。</returns>
        public static ProfileListItem FromProfile(SdkProfile profile)
        {
            return new ProfileListItem
            {
                Id = profile.Id,
                Name = profile.Name,
                Summary = BuildSummary(profile.Entries),
            };
        }

        /// <summary>
        /// 把 SDK 选择项集合拼成形如「Java、Maven、Node」的摘要；为空时给出占位提示。
        /// </summary>
        private static string BuildSummary(IReadOnlyList<SdkProfileEntry> entries)
        {
            if (entries is null || entries.Count == 0)
            {
                return "暂无 SDK 条目";
            }

            // NOTE: 仅展示类型名做概览，避免暴露内部 RecordId；详细内容留待详情/编辑界面。
            var types = entries.Select(entry => entry.Type.ToString());
            return "包含 SDK：" + string.Join("、", types);
        }
    }
}
