using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using DevSwitch.App.ViewModels;
using DevSwitch.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace DevSwitch.App;

/// <summary>
/// DevSwitch 主窗口。
/// 当前承载 tubatools 风格自绘标题栏与 Gallery 风格手写侧栏，绑定假数据 ViewModel，不执行真实 SDK 切换。
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly MainWindowViewModel viewModel;
    private readonly string dataRoot;
    private readonly DevSwitch.App.Services.AppServices appServices;
    private readonly SdkImportRegistrationService sdkImportRegistrationService;
    private readonly CancellationTokenSource windowLifetime = new();
    private bool isSidebarExpanded = true;

    // 防止同一行/同一操作重复触发的繁忙标记。
    private bool isRowOperationBusy;
    private bool isDoctorRunning;

    // SDK 子菜单（Java/Maven/Node/Go）展开状态：用户点 SDK 管理父项可手动展开/收起。默认展开。
    private bool isSdkSubmenuExpanded = true;

    // SDK 总览页卡片数据源（按分类汇总）。
    private readonly System.Collections.ObjectModel.ObservableCollection<DevSwitch.Core.SdkCategorySummary> sdkOverviewSummaries = new();

    private Button? currentNavigationButton;
    private Border? currentNavigationIndicator;
    private FrameworkElement? currentContent;

    private Button[] navigationButtons = System.Array.Empty<Button>();
    private Border[] navigationIndicators = System.Array.Empty<Border>();
    private TextBlock[] navigationTextBlocks = System.Array.Empty<TextBlock>();

    private readonly SolidColorBrush navTransparentBrush = new(Colors.Transparent);
    private Brush? navHoverBrush;
    private Brush? navPressedBrush;
    private Brush? navSelectedHoverBrush;
    private Brush? navHoverBorderBrush;
    private Brush? navSelectedBorderBrush;
    private Brush? selectedNavigationBrush;
    private Brush? navIconBrush;
    private Brush? navSelectedTextBrush;

    /// <summary>
    /// 创建主窗口、设置自绘标题栏、设置初始尺寸，并绑定真实 SDK catalog ViewModel。
    /// </summary>
    /// <param name="viewModel">主窗口 ViewModel，由 App 组合根注入。</param>
    /// <param name="dataRoot">DevSwitch 数据根目录，用于本地 SDK 导入服务。</param>
    /// <param name="appServices">App 组合根，集中提供切换/验证/删除/诊断/重置/更新/下载等后端服务。</param>
    public MainWindow(MainWindowViewModel viewModel, string dataRoot, DevSwitch.App.Services.AppServices appServices)
    {
        // 重要：先赋值依赖字段，再调用 InitializeComponent()。
        // XAML 中若有 IsSelected="True" 等设置会在 InitializeComponent 期间立刻触发 SelectionChanged
        // 之类的事件 handler，handler 通常会读 this.viewModel；若此时字段还没赋值，会抛 NullReferenceException
        // 把整个 InitializeComponent 包成 XamlParseException，主窗口构造直接失败、应用闪退。
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        this.dataRoot = dataRoot ?? throw new ArgumentNullException(nameof(dataRoot));
        this.appServices = appServices ?? throw new ArgumentNullException(nameof(appServices));

        InitializeComponent();
        InitializeSdkListViewLayout();

        Title = "DevSwitch";
        InitializeCustomTitleBar();
        sdkImportRegistrationService = this.appServices.CreateImportRegistrationService();
        RootGrid.DataContext = viewModel;

        InitializeNavigationCaches();
        InitializeNavigationVisuals();
        InitializeAccentSwatches();
        // 绑定 SDK 总览卡片数据源。
        SdkOverviewCards.ItemsSource = sdkOverviewSummaries;
        ToolTipService.SetToolTip(TitlePaneToggleButton, "折叠导航");
        SetNavigationSelection(HomeNavButton, HomeIndicator);
        SetActiveContent(HomeContent);

        // 订阅语言变更：用户在设置页切换语言后即时刷新所有界面文案，无需重启。
        DevSwitch.App.Localization.LocalizationManager.Instance.LanguageChanged += OnLanguageChanged;
        // 首帧按当前语言刷新一次导航/设置文案（默认中文；启动后会按 settings 再校正）。
        RefreshLocalizedTexts();

        RootGrid.Loaded += OnRootGridLoaded;
        Closed += OnMainWindowClosed;
        // 默认窗口尺寸调到 1480x920：
        // - 侧边导航占 280px，主内容区 SDK 列表表格新增「路径」列后总宽度需求显著提升；
        // - 1320 宽度下「路径/状态/操作」列会被裁切，1480 能容纳侧栏 + 表格全部列 + 24px 边距；
        // - 高度 920 让工具栏 + 表头 + 数据行不出现垂直滚动条。
        ResizeWindow(1480, 920);
        // 注册最小尺寸约束（1280x800）：通过 SetWindowSubclass 拦截 WM_GETMINMAXINFO，
        // 防止用户手动拖小窗口导致表格列再次被截断。AppWindow 本身不暴露 MinSize。
        InstallMinimumSizeConstraint(1280, 800);
    }

    /// <summary>
    /// 语言变更事件处理：回到 UI 线程刷新所有本地化文案。
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshLocalizedTexts();
    }

    /// <summary>
    /// 按当前语言刷新导航与设置页的本地化文案（即时热切换的核心刷新点）。
    /// 只更新文本属性，不重建控件，保证切换流畅不卡顿。
    /// </summary>
    private void RefreshLocalizedTexts()
    {
        var loc = DevSwitch.App.Localization.LocalizationManager.Instance;

        // 标题栏右侧"演示模式"标签。
        if (TitleBarDemoModeText is not null)
        {
            TitleBarDemoModeText.Text = loc["titlebar.demoMode"];
        }

        // 导航栏文本。
        NavMainHeaderText.Text = loc["nav.section"];
        HomeNavText.Text = loc["nav.home"];
        SdkNavText.Text = loc["nav.sdk"];
        ProfilesNavText.Text = loc["nav.profiles"];
        DoctorNavText.Text = loc["nav.doctor"];
        LogsNavText.Text = loc["nav.logs"];
        SettingsNavText.Text = loc["nav.settings"];

        // 首页 hero：副标题/描述/两个 CTA + 装饰卡的"使用中""当前运行时" + 快捷操作分区。
        if (HomeHeroSubtitleText is not null)
        {
            HomeHeroSubtitleText.Text = loc["home.subtitle"];
            HomeHeroDescriptionText.Text = loc["home.description"];
            HomeOpenSdkButton.Content = loc["home.openSdk"];
            HomeRunDoctorButton.Content = loc["home.runDoctor"];
            HomeDecoActiveText.Text = loc["home.deco.active"];
            HomeDecoCurrentRuntimeLabel.Text = loc["home.deco.currentRuntime"];
            HomeShortcutsHeaderText.Text = loc["home.shortcuts"];

            // 四张快捷卡：title 复用现有导航文案，描述用专属 key。
            HomeShortcutSdkTitle.Text = loc["nav.sdk"];
            HomeShortcutSdkDescText.Text = loc["home.shortcut.sdk.desc"];
            HomeShortcutDoctorTitle.Text = loc["nav.doctor"];
            HomeShortcutDoctorDescText.Text = loc["home.shortcut.doctor.desc"];
            HomeShortcutProfilesTitle.Text = loc["nav.profiles"];
            HomeShortcutProfilesDescText.Text = loc["home.shortcut.profiles.desc"];
            HomeShortcutSettingsTitle.Text = loc["nav.settings"];
            HomeShortcutSettingsDescText.Text = loc["home.shortcut.settings.desc"];
        }

        // SDK 总览页（点击"SDK 管理"父项进入）：标题与副标题。
        if (SdkOverviewTitleText is not null)
        {
            SdkOverviewTitleText.Text = loc["nav.sdk"];
            SdkOverviewSubtitleText.Text = loc["sdk.overview.subtitle"];
        }

        // SDK 分类页（Java/Maven/Node.js/Go 共用一套）：副标题、5 个工具栏按钮、状态过滤项。
        if (SdkPageSubtitleText is not null)
        {
            SdkPageSubtitleText.Text = loc["sdk.page.subtitle"];
            SdkAddLocalButtonText.Text = loc["sdk.button.addLocal"];
            SdkDownloadButtonText.Text = loc["sdk.button.download"];
            SdkRefreshButtonText.Text = loc["common.refresh"];
            SdkDetectButtonText.Text = loc["sdk.button.detect"];
            SdkResetButtonText.Text = loc["sdk.button.reset"];

            // 状态过滤 ComboBox 选项与占位符。
            if (StatusFilterComboBox is not null)
            {
                StatusFilterComboBox.PlaceholderText = loc["sdk.statusFilter.placeholder"];
            }
            StatusFilterAllItem.Content = loc["sdk.statusFilter.all"];
            StatusFilterActiveItem.Content = loc["sdk.statusFilter.active"];
            StatusFilterUsableItem.Content = loc["sdk.statusFilter.usable"];
            StatusFilterUnavailableItem.Content = loc["sdk.statusFilter.unavailable"];

            // 空状态：标题 + 行动按钮。
            if (EmptyAddLocalSdkButton is not null)
            {
                EmptyAddLocalSdkButton.Content = loc["sdk.button.addLocal"];
            }
        }

        // SDK 表格列头（"路径"列已存在，这里补齐其它列头与"操作"列）。
        if (SdkPathColumnHeaderText is not null)
        {
            SdkPathColumnHeaderText.Text = loc["sdk.column.path"];
        }

        // 设置页：标题/副标题与各分区 header（语言、数据目录、下载、更新、反馈）。
        if (SettingsTitleText is not null)
        {
            SettingsTitleText.Text = loc["settings.title"];
            SettingsSubtitleText.Text = loc["settings.subtitle"];
            SettingsLanguageHeaderText.Text = loc["settings.language"];
            SettingsLanguageDescText.Text = loc["settings.language.desc"];
            SettingsAccentHeaderText.Text = loc["settings.accent"];
            SettingsAccentLabelText.Text = loc["settings.accent"];
            SettingsAccentDescText.Text = loc["settings.accent.desc"];
            UpdateAccentSwatchSelection();
        }

        // 语言状态行（当前语言：xxx）。
        UpdateLanguageStatus();
    }

    /// <summary>
    /// 初始化 SDK 列表容器布局。
    /// 清理 WinUI ListViewItem 默认内边距，让表头 Grid 与数据行 Grid 共享同一可用宽度。
    /// </summary>
    private void InitializeSdkListViewLayout()
    {
        var itemContainerStyle = new Style(typeof(ListViewItem));
        itemContainerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        itemContainerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        itemContainerStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0)));
        itemContainerStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0d));

        SdkVersionsListView.ItemContainerStyle = itemContainerStyle;
    }

    /// <summary>
    /// 初始化 tubatools 风格自绘标题栏。
    /// 保留系统最小化/最大化/关闭按钮，只扩展内容到标题栏并设置按钮颜色。
    /// </summary>
    private void InitializeCustomTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);

        AppWindow appWindow = GetAppWindow();
        SetWindowIcon(appWindow);

        AppWindowTitleBar titleBar = appWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = Colors.Black;
        titleBar.ButtonInactiveForegroundColor = Colors.Gray;
        titleBar.ButtonHoverBackgroundColor = Colors.Gainsboro;
        titleBar.ButtonPressedBackgroundColor = Colors.LightGray;
    }

    /// <summary>
    /// 初始化导航控件缓存。
    /// 将频繁访问的按钮、指示条和文本缓存下来，避免每次切换模块时临时创建数组。
    /// </summary>
    private void InitializeNavigationCaches()
    {
        navigationButtons = new[]
        {
            HomeNavButton,
            SdkNavButton,
            JavaNavButton,
            MavenNavButton,
            NodeNavButton,
            GoNavButton,
            RustNavButton,
            ProfilesNavButton,
            DoctorNavButton,
            LogsNavButton,
            SettingsNavButton,
        };

        navigationIndicators = new[]
        {
            HomeIndicator,
            SdkIndicator,
            JavaIndicator,
            MavenIndicator,
            NodeIndicator,
            GoIndicator,
            RustIndicator,
            ProfilesIndicator,
            DoctorIndicator,
            LogsIndicator,
            SettingsIndicator,
        };

        navigationTextBlocks = new[]
        {
            HomeNavText,
            SdkNavText,
            JavaNavText,
            MavenNavText,
            NodeNavText,
            GoNavText,
            RustNavText,
            ProfilesNavText,
            DoctorNavText,
            LogsNavText,
            SettingsNavText,
        };

        selectedNavigationBrush = (Brush)Application.Current.Resources["DevSwitchNavSelectedBrush"];
        navHoverBrush = (Brush)Application.Current.Resources["DevSwitchNavHoverBrush"];
        navPressedBrush = (Brush)Application.Current.Resources["DevSwitchNavPressedBrush"];
        navSelectedHoverBrush = (Brush)Application.Current.Resources["DevSwitchNavSelectedHoverBrush"];
        navHoverBorderBrush = (Brush)Application.Current.Resources["DevSwitchNavHoverBorderBrush"];
        navSelectedBorderBrush = (Brush)Application.Current.Resources["DevSwitchNavSelectedBorderBrush"];
        navIconBrush = (Brush)Application.Current.Resources["DevSwitchNavIconBrush"];
        navSelectedTextBrush = (Brush)Application.Current.Resources["DevSwitchAccentTextBrush"];

        InitializeNavigationTooltips();
    }

    /// <summary>
    /// 初始化侧栏按钮的 Fluent hover/pressed 资源。
    /// 使用按钮本地资源覆盖默认模板读取的画刷，不污染 App.xaml，也不需要自定义 ControlTemplate。
    /// </summary>
    private void InitializeNavigationVisuals()
    {
        foreach (Button button in navigationButtons)
        {
            ApplyNavigationButtonVisual(button, isSelected: false);
        }
    }

    /// <summary>
    /// 为导航项设置稳定 tooltip。
    /// 折叠态只显示图标时，tooltip 是保持可访问性和可理解性的关键反馈。
    /// </summary>
    private void InitializeNavigationTooltips()
    {
        ToolTipService.SetToolTip(HomeNavButton, "首页");
        ToolTipService.SetToolTip(SdkNavButton, "SDK 管理");
        ToolTipService.SetToolTip(JavaNavButton, "Java");
        ToolTipService.SetToolTip(MavenNavButton, "Maven");
        ToolTipService.SetToolTip(NodeNavButton, "Node.js");
        ToolTipService.SetToolTip(GoNavButton, "Go");
        ToolTipService.SetToolTip(RustNavButton, "Rust");
        ToolTipService.SetToolTip(ProfilesNavButton, "配置档案");
        ToolTipService.SetToolTip(DoctorNavButton, "环境诊断");
        ToolTipService.SetToolTip(LogsNavButton, "日志");
        ToolTipService.SetToolTip(SettingsNavButton, "设置");
    }

    /// <summary>
    /// 切换左侧导航栏展开/折叠状态。
    /// 折叠宽度使用 64px，并把按钮 Padding 归零，避免图标被裁剪成半截。
    /// </summary>
    private void OnToggleSidebarClick(object sender, RoutedEventArgs e)
    {
        isSidebarExpanded = !isSidebarExpanded;
        SidebarColumn.Width = new GridLength(isSidebarExpanded ? 280 : 64);

        Visibility expandedVisibility = isSidebarExpanded ? Visibility.Visible : Visibility.Collapsed;
        NavMainHeaderText.Visibility = expandedVisibility;
        // 子菜单可见性：折叠侧栏时强制隐藏；展开侧栏时恢复到用户的子菜单展开状态。
        SdkChildrenPanel.Visibility = isSidebarExpanded && isSdkSubmenuExpanded ? Visibility.Visible : Visibility.Collapsed;
        // 折叠态隐藏父项箭头（只剩图标），展开态恢复。
        SdkChevronIcon.Visibility = expandedVisibility;

        foreach (TextBlock textBlock in navigationTextBlocks)
        {
            textBlock.Visibility = expandedVisibility;
        }

        if (!isSidebarExpanded && IsSdkChildNavigationButton(currentNavigationButton))
        {
            SetNavigationSelection(SdkNavButton, SdkIndicator);
        }

        ToolTipService.SetToolTip(TitlePaneToggleButton, isSidebarExpanded ? "折叠导航" : "展开导航");
    }

    /// <summary>
    /// 判断当前选中项是否属于 SDK 子导航。
    /// 折叠侧栏时子导航整体隐藏，需要把父级 SDK 管理项作为可见选中态。
    /// </summary>
    private bool IsSdkChildNavigationButton(Button? button)
    {
        return button == JavaNavButton
            || button == MavenNavButton
            || button == NodeNavButton
            || button == GoNavButton
            || button == RustNavButton;
    }

    /// <summary>
    /// 显示首页。
    /// </summary>
    private void OnHomeNavClick(object sender, RoutedEventArgs e)
    {
        SetNavigationSelection(HomeNavButton, HomeIndicator);
        ShowHomeContent();
    }

    /// <summary>
    /// SDK 管理主入口：显示 SDK 总览页，并切换子菜单（Java/Maven/...）的展开/收起状态。
    /// 父项本身是「总览 + 可折叠分组头」，不再直接等同于 Java 页。
    /// </summary>
    private void OnSdkNavClick(object sender, RoutedEventArgs e)
    {
        // 切换子菜单展开状态（仅在侧栏展开态下有视觉意义；折叠态由 toggle 控制）。
        isSdkSubmenuExpanded = !isSdkSubmenuExpanded;
        UpdateSdkSubmenuVisual();

        SetNavigationSelection(SdkNavButton, SdkIndicator);
        ShowSdkOverviewContent();
    }

    /// <summary>
    /// 总览页分类卡片点击：进入对应分类管理页，并确保子菜单展开。
    /// </summary>
    private void OnSdkOverviewCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string category)
        {
            return;
        }

        // 进入具体分类前确保子菜单展开，保持导航上下文清晰。
        if (!isSdkSubmenuExpanded)
        {
            isSdkSubmenuExpanded = true;
            UpdateSdkSubmenuVisual();
        }

        switch (category)
        {
            case "Java":
                ShowSdkCategory("Java", JavaNavButton, JavaIndicator);
                break;
            case "Maven":
                ShowSdkCategory("Maven", MavenNavButton, MavenIndicator);
                break;
            case "Node.js":
                ShowSdkCategory("Node.js", NodeNavButton, NodeIndicator);
                break;
            case "Go":
                ShowSdkCategory("Go", GoNavButton, GoIndicator);
                break;
            case "Rust":
                ShowSdkCategory("Rust", RustNavButton, RustIndicator);
                break;
        }
    }

    private void OnJavaNavClick(object sender, RoutedEventArgs e)
    {
        ShowSdkCategory("Java", JavaNavButton, JavaIndicator);
    }

    private void OnMavenNavClick(object sender, RoutedEventArgs e)
    {
        ShowSdkCategory("Maven", MavenNavButton, MavenIndicator);
    }

    private void OnNodeNavClick(object sender, RoutedEventArgs e)
    {
        ShowSdkCategory("Node.js", NodeNavButton, NodeIndicator);
    }

    private void OnGoNavClick(object sender, RoutedEventArgs e)
    {
        ShowSdkCategory("Go", GoNavButton, GoIndicator);
    }

    private void OnRustNavClick(object sender, RoutedEventArgs e)
    {
        ShowSdkCategory("Rust", RustNavButton, RustIndicator);
    }

    private void OnProfilesNavClick(object sender, RoutedEventArgs e)
    {
        SetNavigationSelection(ProfilesNavButton, ProfilesIndicator);
        ShowProfilesContent();
    }

    private async void OnDoctorNavClick(object sender, RoutedEventArgs e)
    {
        SetNavigationSelection(DoctorNavButton, DoctorIndicator);
        ShowDoctorContent();

        // 进入诊断页自动运行一次诊断；已有结果时仍刷新以反映最新状态。
        await RunDoctorAsync();
    }

    private void OnLogsNavClick(object sender, RoutedEventArgs e)
    {
        SetNavigationSelection(LogsNavButton, LogsIndicator);
        ShowLogsContent();
    }

    private void OnSettingsNavClick(object sender, RoutedEventArgs e)
    {
        SetNavigationSelection(SettingsNavButton, SettingsIndicator);
        ShowSettingsContent();
    }

    /// <summary>
    /// 首页快捷入口：跳转到 Java SDK 管理演示页。
    /// </summary>
    private void OnShowJavaSdkClick(object sender, RoutedEventArgs e)
    {
        ShowSdkCategory("Java", JavaNavButton, JavaIndicator);
    }

    /// <summary>
    /// 根布局加载后异步读取真实 sdks.json 并把设置同步到设置页，避免构造函数阻塞 UI 首帧。
    /// WinUI 3 的 Window 不暴露 Loaded 事件，这里改用根 Grid 的 Loaded。
    /// </summary>
    private async void OnRootGridLoaded(object sender, RoutedEventArgs e)
    {
        // 首帧之后再加载磁盘数据；catalog 与 settings 互不依赖，先并行启动以减少可交互后的等待。
        Task refreshCatalogTask = RefreshSdkCatalogAsync();
        Task loadSettingsTask = LoadSettingsIntoUiAsync();

        // 环境漂移校正可能触发 helper 进程与 WM_SETTINGCHANGE 广播；后台 fire-and-forget，避免串行卡住 Loaded。
        _ = CorrectEnvironmentDriftAsync();

        await Task.WhenAll(refreshCatalogTask, loadSettingsTask);
    }

    /// <summary>
    /// 窗口关闭时取消仍未完成的 catalog 读取或导入刷新。
    /// </summary>
    private void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        // 退订语言变更事件，避免单例长期持有已关闭窗口的引用导致泄漏。
        DevSwitch.App.Localization.LocalizationManager.Instance.LanguageChanged -= OnLanguageChanged;
        // 移除 WM_GETMINMAXINFO 子类化钩子，确保 SubclassProc 委托可被回收，避免野指针。
        UninstallMinimumSizeConstraint();
        windowLifetime.Cancel();
        windowLifetime.Dispose();
    }

    /// <summary>
    /// 刷新按钮：重新读取真实 sdks.json。
    /// </summary>
    private async void OnRefreshSdkCatalogClick(object sender, RoutedEventArgs e)
    {
        await RefreshSdkCatalogAsync();
    }

    /// <summary>
    /// “添加本地 SDK”按钮：选择目录、导入 catalog、刷新当前列表。
    /// </summary>
    private async void OnAddLocalSdkClick(object sender, RoutedEventArgs e)
    {
        string? selectedPath = await PickSdkFolderAsync();
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return;
        }

        await ImportSelectedLocalSdkAsync(selectedPath);
    }

    /// <summary>
    /// 通过 WinUI FolderPicker 选择 SDK 根目录。
    /// Desktop WinUI 必须绑定窗口句柄，否则 picker 可能无法显示。
    /// </summary>
    private async Task<string?> PickSdkFolderAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");

        nint hwnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(picker, hwnd);

        StorageFolder? folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    /// <summary>
    /// 调用 Core 导入服务登记本地 SDK，并在成功后刷新 GUI 列表。
    /// </summary>
    /// <param name="selectedPath">用户选择的 SDK 目录。</param>
    private async Task ImportSelectedLocalSdkAsync(string selectedPath)
    {
        SetImportBusy(true);

        try
        {
            var result = await sdkImportRegistrationService.ImportAndVerifyAsync(selectedPath, cancellationToken: windowLifetime.Token);
            if (result.Success && result.Record is not null)
            {
                ShowImportedSdkCategory(result.Record.Type);
                await RefreshSdkCatalogAsync();
            }

            await ShowImportResultDialogAsync(result);
        }
        catch (Exception ex)
        {
            await ShowSimpleDialogAsync("导入失败", ex.Message);
        }
        finally
        {
            SetImportBusy(false);
        }
    }

    /// <summary>
    /// 按导入结果切换到对应 SDK 分类，让用户立刻看到新增记录。
    /// </summary>
    private void ShowImportedSdkCategory(SdkType type)
    {
        switch (type)
        {
            case SdkType.Java:
                ShowSdkCategory("Java", JavaNavButton, JavaIndicator);
                break;
            case SdkType.Maven:
                ShowSdkCategory("Maven", MavenNavButton, MavenIndicator);
                break;
            case SdkType.Node:
                ShowSdkCategory("Node.js", NodeNavButton, NodeIndicator);
                break;
            case SdkType.Go:
                ShowSdkCategory("Go", GoNavButton, GoIndicator);
                break;
            case SdkType.Rust:
                ShowSdkCategory("Rust", RustNavButton, RustIndicator);
                break;
        }
    }

    /// <summary>
    /// 统一刷新 ViewModel，处理窗口关闭导致的取消场景。
    /// </summary>
    private async Task RefreshSdkCatalogAsync()
    {
        try
        {
            await viewModel.RefreshAsync(windowLifetime.Token);
        }
        catch (OperationCanceledException)
        {
            // NOTE: 关闭窗口时取消刷新无需反馈。
        }
    }

    /// <summary>
    /// 设置导入按钮 busy 状态，防止用户重复打开 picker 或重复写入 sdks.json。
    /// </summary>
    private void SetImportBusy(bool isBusy)
    {
        AddLocalSdkButton.IsEnabled = !isBusy;
        EmptyAddLocalSdkButton.IsEnabled = !isBusy;
    }

    /// <summary>
    /// 展示本地 SDK 导入结果。
    /// </summary>
    private Task ShowImportResultDialogAsync(LocalSdkImportResult result)
    {
        string title = result.Success ? "导入成功" : "导入失败";
        string content = result.Success && result.Record is not null
            ? $"已导入 {result.Record.Name}。\n路径：{result.Record.Path}"
            : result.Message;

        return ShowSimpleDialogAsync(title, content);
    }

    /// <summary>
    /// 显示简单反馈弹窗。
    /// </summary>
    private async Task ShowSimpleDialogAsync(string title, string content)
    {
        if (RootGrid.XamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "知道了",
            XamlRoot = RootGrid.XamlRoot,
        };

        await dialog.ShowAsync();
    }

    /// <summary>
    /// 所有尚未接入真实业务按钮的占位反馈。
    /// </summary>
    private async void OnPlaceholderActionClick(object sender, RoutedEventArgs e)
    {
        if (RootGrid.XamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "功能占位",
            Content = "当前是手工测试空壳，真实功能将在后续纵切接入。",
            CloseButtonText = "知道了",
            XamlRoot = RootGrid.XamlRoot,
        };

        await dialog.ShowAsync();
    }

    /// <summary>
    /// 显示指定 SDK 分类并更新 Gallery 风格导航选中态。
    /// 重复点击当前分类时直接短路，避免不必要的绑定刷新和布局更新。
    /// </summary>
    /// <param name="category">SDK 分类名称。</param>
    /// <param name="selectedButton">当前选中的导航按钮。</param>
    /// <param name="selectedIndicator">当前选中的左侧 Accent 条。</param>
    private void ShowSdkCategory(string category, Button selectedButton, Border selectedIndicator)
    {
        if (currentNavigationButton == selectedButton
            && currentContent == SdkContent
            && viewModel.SelectedCategory == category)
        {
            return;
        }

        viewModel.SelectCategory(category);
        SetNavigationSelection(selectedButton, selectedIndicator);
        ShowSdkContent();
    }

    /// <summary>
    /// 设置左侧导航选中态。
    /// 仅更新旧选中项和新选中项，避免每次模块切换都遍历全部按钮和指示条。
    /// </summary>
    private void SetNavigationSelection(Button selectedButton, Border selectedIndicator)
    {
        if (currentNavigationButton == selectedButton && currentNavigationIndicator == selectedIndicator)
        {
            return;
        }

        if (currentNavigationButton is not null)
        {
            ApplyNavigationButtonVisual(currentNavigationButton, isSelected: false);
        }

        if (currentNavigationIndicator is not null)
        {
            currentNavigationIndicator.Visibility = Visibility.Collapsed;
        }

        ApplyNavigationButtonVisual(selectedButton, isSelected: true);
        selectedIndicator.Visibility = Visibility.Visible;

        currentNavigationButton = selectedButton;
        currentNavigationIndicator = selectedIndicator;
    }

    /// <summary>
    /// 应用侧栏按钮的正常/选中视觉状态，并覆盖本地 hover/pressed 资源。
    /// </summary>
    /// <param name="button">需要更新视觉的导航按钮。</param>
    /// <param name="isSelected">是否为当前选中项。</param>
    private void ApplyNavigationButtonVisual(Button button, bool isSelected)
    {
        button.Background = isSelected && selectedNavigationBrush is not null ? selectedNavigationBrush : navTransparentBrush;
        button.BorderBrush = isSelected && navSelectedBorderBrush is not null ? navSelectedBorderBrush : navTransparentBrush;
        button.BorderThickness = new Thickness(1);
        button.CornerRadius = new CornerRadius(8);

        button.Resources["ButtonBackgroundPointerOver"] = isSelected ? navSelectedHoverBrush : navHoverBrush;
        button.Resources["ButtonBackgroundPressed"] = isSelected ? navSelectedHoverBrush : navPressedBrush;
        button.Resources["ButtonBorderBrushPointerOver"] = isSelected ? navSelectedBorderBrush : navHoverBorderBrush;
        button.Resources["ButtonBorderBrushPressed"] = isSelected ? navSelectedBorderBrush : navHoverBorderBrush;

        Brush? foregroundBrush = isSelected ? navSelectedTextBrush : navIconBrush;
        foreach (TextBlock textBlock in FindDescendants<TextBlock>(button))
        {
            textBlock.Foreground = foregroundBrush;
            textBlock.FontWeight = isSelected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }

        foreach (FontIcon icon in FindDescendants<FontIcon>(button))
        {
            icon.Foreground = foregroundBrush;
        }
    }

    /// <summary>
    /// 查找某个导航按钮内部指定类型的后代元素。
    /// 该方法仅服务侧栏视觉状态更新，避免在 XAML 中引入高风险复杂 ControlTemplate。
    /// </summary>
    private static IEnumerable<T> FindDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T typedChild)
            {
                yield return typedChild;
            }

            foreach (T descendant in FindDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>
    /// 设置右侧当前显示内容。
    /// 只隐藏旧内容并显示新内容，避免每次切换都重设全部内容区 Visibility。
    /// </summary>
    /// <param name="activeContent">需要显示的内容控件。</param>
    private void SetActiveContent(FrameworkElement activeContent)
    {
        if (currentContent == activeContent)
        {
            return;
        }

        if (currentContent is not null)
        {
            currentContent.Visibility = Visibility.Collapsed;
        }

        activeContent.Visibility = Visibility.Visible;
        currentContent = activeContent;
    }

    /// <summary>
    /// 确保 x:Load=False 的非首屏内容已实例化。
    /// </summary>
    /// <typeparam name="T">预期的控件类型。</typeparam>
    /// <param name="elementName">XAML 中的 x:Name。</param>
    /// <returns>已加载的控件实例。</returns>
    private T EnsureDeferredContent<T>(string elementName)
        where T : FrameworkElement
    {
        // FindName 会触发 x:Load=False 控件实例化；已加载时直接返回现有实例。
        if (RootGrid.FindName(elementName) is T content)
        {
            // 新加载的内容仍是默认静态文案，立即同步当前语言和强调色选中态。
            RefreshLocalizedTexts();
            return content;
        }

        throw new InvalidOperationException($"无法加载界面区域：{elementName}");
    }

    /// <summary>
    /// 显示首页。
    /// </summary>
    private void ShowHomeContent()
    {
        SetActiveContent(HomeContent);
    }

    /// <summary>
    /// 显示 SDK 管理页面。
    /// </summary>
    private void ShowSdkContent()
    {
        SetActiveContent(SdkContent);
    }

    /// <summary>
    /// 显示 SDK 管理总览页（四类卡片入口），并按当前 catalog 刷新各分类汇总。
    /// </summary>
    private void ShowSdkOverviewContent()
    {
        RefreshSdkOverviewCards();
        SetActiveContent(SdkOverviewContent);
    }

    /// <summary>
    /// 按当前 ViewModel 行重算 SDK 总览卡片（数量 + 活跃版本）。
    /// 复用 Core 的 SdkCategorySummaryBuilder 纯逻辑，UI 只做投影与展示。
    /// </summary>
    private void RefreshSdkOverviewCards()
    {
        var inputs = viewModel.Versions
            .Select(r => new DevSwitch.Core.SdkSummaryInput(r.Category, r.Name, r.Status))
            .ToList();
        var cards = DevSwitch.Core.SdkCategorySummaryBuilder.Build(inputs);

        sdkOverviewSummaries.Clear();
        foreach (var card in cards)
        {
            sdkOverviewSummaries.Add(card);
        }
    }

    /// <summary>
    /// 更新 SDK 子菜单展开/收起视觉：子项面板可见性 + 父项箭头朝向。
    /// 仅在侧栏展开态下生效；折叠态由 OnToggleSidebarClick 统一隐藏子项。
    /// </summary>
    private void UpdateSdkSubmenuVisual()
    {
        if (!isSidebarExpanded)
        {
            return;
        }

        SdkChildrenPanel.Visibility = isSdkSubmenuExpanded ? Visibility.Visible : Visibility.Collapsed;
        // E70D=向下箭头（展开）、E70E=向上箭头（收起）。
        SdkChevronIcon.Glyph = isSdkSubmenuExpanded ? "\uE70D" : "\uE70E";
    }

    /// <summary>
    /// 显示设置页。
    /// 检查更新与反馈入口均收纳在该页面中。
    /// </summary>
    private void ShowSettingsContent()
    {
        var settingsContent = EnsureDeferredContent<ScrollViewer>(nameof(SettingsContent));
        InitializeAccentSwatches();
        _ = LoadSettingsIntoUiAsync();
        SetActiveContent(settingsContent);
    }

    // 配置档案视图懒初始化标记：仅首次进入时注入 dataRoot 并加载，避免重复 IO。
    private bool isProfilesInitialized;

    /// <summary>
    /// 显示配置档案页；首次进入时注入数据根并加载列表。
    /// </summary>
    private void ShowProfilesContent()
    {
        var profilesContent = EnsureDeferredContent<Views.ProfilesView>(nameof(ProfilesContent));
        SetActiveContent(profilesContent);

        if (!isProfilesInitialized)
        {
            profilesContent.Initialize(dataRoot);
            isProfilesInitialized = true;
        }
    }

    /// <summary>
    /// 显示日志页；每次进入都重新加载，保证看到最新记录。
    /// </summary>
    private void ShowLogsContent()
    {
        var logsContent = EnsureDeferredContent<Views.LogsView>(nameof(LogsContent));
        logsContent.Initialize(dataRoot);
        SetActiveContent(logsContent);
    }

    /// <summary>
    /// 显示暂未实现页面的占位内容。
    /// </summary>
    private void ShowPlaceholderContent(string title, string description)
    {
        PlaceholderTitleText.Text = title;
        PlaceholderDescriptionText.Text = description;
        SetActiveContent(PlaceholderContent);
    }

    /// <summary>
    /// 设置运行时窗口图标。
    /// ApplicationIcon 负责 exe 文件图标，AppWindow.SetIcon 负责任务栏和 Alt-Tab 窗口图标。
    /// </summary>
    /// <param name="appWindow">当前窗口对应的 AppWindow。</param>
    private static void SetWindowIcon(AppWindow appWindow)
    {
        string iconPath = Path.Combine(AppContext.BaseDirectory, "princess.ico");
        if (File.Exists(iconPath))
        {
            appWindow.SetIcon(iconPath);
        }
    }

    /// <summary>
    /// 获取当前窗口对应的 AppWindow。
    /// </summary>
    private AppWindow GetAppWindow()
    {
        nint windowHandle = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
        return AppWindow.GetFromWindowId(windowId);
    }

    // ============== 路径列交互（Tapped 复制 + Hover 高亮 + ToolTip 完整路径 + InfoBar 反馈） ==============

    // 路径列默认色（次要文本，对应 Fluent2 colorNeutralForeground2）。
    private Brush? sdkPathDefaultBrush;
    // 路径列 hover 高亮色（品牌色，对应 Fluent2 colorBrandForeground1，给予链接观感）。
    private Brush? sdkPathHoverBrush;
    // InfoBar 自动关闭计时器；与单个 InfoBar 实例共生，重复触发时复位 3 秒倒计时。
    private DispatcherTimer? pathCopiedTimer;

    /// <summary>
    /// SDK 表格路径列单元格 TextBlock 加载完成时挂载交互事件。
    /// 由 DataTemplate 内 TextBlock.Loaded 调用：每个数据行的路径文本一旦进入可视化树即注册一次。
    /// 通过 Tag 携带绑定路径（DataContext 在虚拟化场景下会重用，但 Tag 的 Binding 由模板每次重新求值，可靠）。
    /// </summary>
    private void OnSdkPathTextLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBlock textBlock)
        {
            return;
        }

        // 懒初始化 brush：首次有路径单元格出现时再读资源，避免构造函数顺序耦合。
        sdkPathDefaultBrush ??= (Brush)Application.Current.Resources["DevSwitchMutedTextBrush"];
        sdkPathHoverBrush ??= (Brush)Application.Current.Resources["DevSwitchAccentTextBrush"];

        // 初版策略：一律挂 ToolTip（系统会在文本完整可见时按需自动隐藏行为由用户设置决定，
        // 简单可靠；后续若有体验诉求可再切换为按截断条件智能挂载）。
        var loc = DevSwitch.App.Localization.LocalizationManager.Instance;
        ToolTipService.SetToolTip(textBlock, !string.IsNullOrEmpty(textBlock.Text) ? textBlock.Text : loc["sdk.path.tooltip"]);

        // 防止重复订阅：模板复用时 Loaded 可能被重复触发。
        textBlock.PointerEntered -= OnSdkPathPointerEntered;
        textBlock.PointerExited -= OnSdkPathPointerExited;
        textBlock.Tapped -= OnSdkPathTapped;
        textBlock.PointerEntered += OnSdkPathPointerEntered;
        textBlock.PointerExited += OnSdkPathPointerExited;
        textBlock.Tapped += OnSdkPathTapped;
    }

    /// <summary>
    /// 鼠标进入路径文本：切到品牌色高亮，营造链接质感。
    /// </summary>
    private void OnSdkPathPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is TextBlock tb && sdkPathHoverBrush is not null)
        {
            tb.Foreground = sdkPathHoverBrush;
        }
    }

    /// <summary>
    /// 鼠标移出路径文本：还原次要文本颜色。
    /// </summary>
    private void OnSdkPathPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is TextBlock tb && sdkPathDefaultBrush is not null)
        {
            tb.Foreground = sdkPathDefaultBrush;
        }
    }

    /// <summary>
    /// 点击路径文本：复制完整路径到剪贴板并显示 InfoBar 成功提示。
    /// 路径优先取 Tag（DataTemplate 中通过 {Binding Path} 锁定），次取当前 Text。
    /// </summary>
    private void OnSdkPathTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not TextBlock tb)
        {
            return;
        }

        string? path = tb.Tag as string ?? tb.Text;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        // 写入剪贴板：DataPackage + SetText 是 WinUI3 桌面端的标准做法。
        var package = new DataPackage();
        package.SetText(path);
        Clipboard.SetContent(package);

        ShowPathCopiedInfoBar();
    }

    /// <summary>
    /// 显示「已复制路径」InfoBar（Severity=Success），3 秒后自动关闭。
    /// 重复复制会重置倒计时，让用户有时间继续阅读。
    /// </summary>
    private void ShowPathCopiedInfoBar()
    {
        if (PathCopiedInfoBar is null)
        {
            return;
        }

        var loc = DevSwitch.App.Localization.LocalizationManager.Instance;
        PathCopiedInfoBar.Message = loc["sdk.path.copied"];
        PathCopiedInfoBar.IsOpen = true;

        // 复位 3 秒倒计时：若已存在计时器先停掉，再重新启动，避免多个 Tick 同时关闭。
        if (pathCopiedTimer is null)
        {
            pathCopiedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            pathCopiedTimer.Tick += (s, _) =>
            {
                pathCopiedTimer?.Stop();
                if (PathCopiedInfoBar is not null)
                {
                    PathCopiedInfoBar.IsOpen = false;
                }
            };
        }
        else
        {
            pathCopiedTimer.Stop();
        }
        pathCopiedTimer.Start();
    }

    /// <summary>
    /// 通过 Windows App SDK 的 AppWindow 设置桌面窗口尺寸。
    /// 如果窗口句柄尚不可用，则保持系统默认尺寸，避免影响空壳启动。
    /// </summary>
    /// <param name="width">目标宽度，单位为有效像素。</param>
    /// <param name="height">目标高度，单位为有效像素。</param>
    private void ResizeWindow(int width, int height)
    {
        AppWindow appWindow = GetAppWindow();
        appWindow.Resize(new SizeInt32(width, height));
    }

    // ====== 最小尺寸约束（拦截 WM_GETMINMAXINFO） ======
    // AppWindow 不直接提供 MinSize 属性。WinUI 3 桌面窗口本质仍是 HWND，
    // 因此通过 comctl32 的 SetWindowSubclass 注入子类过程，在 WM_GETMINMAXINFO 中
    // 改写 ptMinTrackSize，即可阻止用户拖小窗口导致表格列被截断。

    private const int WM_GETMINMAXINFO = 0x0024;

    // 子类化标识 ID：随便选一个不会与系统冲突的常量即可。
    private const uint MinSizeSubclassId = 0xD75BC400;

    // 持有委托引用，防止被 GC 回收导致回调进入野指针。
    private SUBCLASSPROC? minSizeSubclassProc;
    private nint minSizeSubclassHwnd;
    private int minSizeWidthPx;
    private int minSizeHeightPx;

    /// <summary>
    /// 安装最小尺寸约束。WM_GETMINMAXINFO 单位为物理像素，这里直接传入像素值。
    /// </summary>
    /// <param name="minWidth">最小宽度（物理像素）。</param>
    /// <param name="minHeight">最小高度（物理像素）。</param>
    private void InstallMinimumSizeConstraint(int minWidth, int minHeight)
    {
        minSizeWidthPx = minWidth;
        minSizeHeightPx = minHeight;
        minSizeSubclassHwnd = WindowNative.GetWindowHandle(this);
        // 必须把委托存在字段里，否则会被 GC 回收。
        minSizeSubclassProc = MinSizeSubclassProc;
        SetWindowSubclass(minSizeSubclassHwnd, minSizeSubclassProc, MinSizeSubclassId, 0);
    }

    /// <summary>
    /// 卸载最小尺寸约束，避免窗口关闭后委托被回收引发回调失败。
    /// </summary>
    private void UninstallMinimumSizeConstraint()
    {
        if (minSizeSubclassProc is not null && minSizeSubclassHwnd != 0)
        {
            RemoveWindowSubclass(minSizeSubclassHwnd, minSizeSubclassProc, MinSizeSubclassId);
            minSizeSubclassProc = null;
            minSizeSubclassHwnd = 0;
        }
    }

    /// <summary>
    /// 子类窗口过程：拦截 WM_GETMINMAXINFO，把 ptMinTrackSize 改写为期望的最小尺寸。
    /// 其他消息一律透传给 DefSubclassProc 走默认链。
    /// </summary>
    private nint MinSizeSubclassProc(nint hWnd, uint msg, nint wParam, nint lParam, uint subclassId, nint refData)
    {
        if (msg == WM_GETMINMAXINFO && lParam != 0)
        {
            // MINMAXINFO 内含 5 个 POINT，ptMinTrackSize 是第 4 个（索引 3）。
            MINMAXINFO info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            info.ptMinTrackSize.X = minSizeWidthPx;
            info.ptMinTrackSize.Y = minSizeHeightPx;
            Marshal.StructureToPtr(info, lParam, fDeleteOld: false);
            return 0;
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    private delegate nint SUBCLASSPROC(nint hWnd, uint uMsg, nint wParam, nint lParam, uint uIdSubclass, nint dwRefData);

    [DllImport("comctl32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetWindowSubclass(nint hWnd, SUBCLASSPROC pfnSubclass, uint uIdSubclass, nint dwRefData);

    [DllImport("comctl32.dll", CharSet = CharSet.Unicode)]
    private static extern bool RemoveWindowSubclass(nint hWnd, SUBCLASSPROC pfnSubclass, uint uIdSubclass);

    [DllImport("comctl32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);
}
