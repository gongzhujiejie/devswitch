// 文件用途：MainWindow 的真实业务操作接线（partial）。把 SDK 行操作（切换/验证/编辑/删除）、
//           顶部下载/重置、诊断、设置页（语言/检查更新/并发/反馈）等 UI 事件桥接到 DevSwitch.Core/
//           Sources/Downloader 的真实后端服务。
// 创建/修改日期：2026-06-10
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：DevSwitch.Core、DevSwitch.Sources、DevSwitch.Downloader、Microsoft.UI.Xaml
// NOTE: 合法授权学习使用，仅限本地环境。
//       - 所有耗时操作 async/await，绝不阻塞 UI 线程；操作期间禁用按钮并显示进度。
//       - 错误统一走 ContentDialog，文案中文、含 errorCode 便于排错。
//       - helper 缺失时捕获 HelperUnavailableException 给出友好提示，不崩溃。

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using DevSwitch.App.Models;
using DevSwitch.App.Services;
using DevSwitch.Core;
using DevSwitch.Downloader;
using DevSwitch.Sources;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DevSwitch.App;

public sealed partial class MainWindow
{
    // GitHub 反馈页地址；点击「反馈」时用系统默认浏览器打开。
    private const string FeedbackUrl = "https://github.com/gongzhujiejie/devswitch/issues";

    // 当前应用版本（用于「检查更新」比较）：从程序集版本读取，集中在 csproj &lt;Version&gt; 维护。
    // 这样发新版只改 csproj 并重新打包，新包版本号就真比旧版高，自更新可被正确判定与验证。
    private static string CurrentAppVersion
    {
        get
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            // 程序集版本形如 0.2.0.0；取前三段拼成 vX.Y.Z，与 GitHub release tag（vX.Y.Z）一致便于比较。
            return version is null ? "v0.0.0" : $"v{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    // 设置初始化标记：避免在代码设置 ComboBox 初值时误触发持久化。
    private bool isSettingsInitializing;

    // 当前选中的强调色 key；色块按钮点击后即时应用并写入 settings.json。
    private string selectedAccentKey = AccentPalette.DefaultKey;

    // ===================== SDK 行操作 =====================

    /// <summary>
    /// 状态筛选下拉选择变更：把 ComboBoxItem.Tag 的稳定值解析为 <see cref="SdkStatusFilter"/> 写回 ViewModel，
    /// ViewModel 内部会重算可见集合并通知列表绑定刷新。
    /// </summary>
    private void OnStatusFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        // XAML 中 ComboBoxItem 上的 IsSelected="True" 会在 InitializeComponent 期间立即触发本 handler，
        // 此时构造函数尚未把依赖字段赋值；任何依赖 viewModel 的 handler 都必须先做 null 防御，
        // 避免一行 NRE 把 InitializeComponent 包装成 XamlParseException、主窗口构造直接失败。
        if (viewModel is null)
        {
            return;
        }

        if (sender is not ComboBox combo || combo.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        // ComboBoxItem.Tag 是稳定业务值；Content 会随本地化变化，不能用于解析过滤条件。
        string? tag = item.Tag?.ToString();
        viewModel.SelectedStatusFilter = SdkStatusFilterMatcher.ParseFromComboBoxTag(tag);
    }

    /// <summary>
    /// 行「切换/当前」主按钮：调用 SdkSwitchService 切换到该记录。
    /// </summary>
    private async void OnSwitchSdkClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetRow(sender, out var row) || isRowOperationBusy)
        {
            return;
        }

        await RunRowOperationAsync(sender, async () =>
        {
            // 切换主链路（shim 单目录方案，快且无卡顿）：
            // 1) switchSdkBatch：单个 helper 进程内完成 junction 切换 + 一次广播（避免多进程与重复广播）。
            // 2) 确保用户级 HOME 变量已写（JAVA_HOME/MAVEN_HOME/GOROOT 指向 current，幂等，无 UAC）。
            // 3) rebuildShims：按 current 下真实可执行刷新 shims（通常很快；版本不变时几乎无开销）。
            // 4) 确保系统 PATH 含 shims 目录（仅首次/缺失时按需提权一次；稳态切换不写 PATH、无 UAC）。
            var switchService = appServices.CreateSwitchService();
            var batchResult = await switchService.SwitchBatchAsync(row.Type, row.Id, broadcast: true, windowLifetime.Token);

            if (!batchResult.Success)
            {
                await ShowSimpleDialogAsync("切换失败", FormatError(batchResult.ErrorCode, batchResult.Message));
                return;
            }

            string note;
            string dialogTitle = "切换成功";
            try
            {
                note = await FinalizeSwitchEnvironmentAsync(row.Type);
            }
            catch (Exception ex)
            {
                // 环境收尾失败不回滚已完成的 junction 切换，仅提示。
                note = $"\n\n⚠ 环境收尾异常：{ex.Message}";
            }

            // 判定终端是否真正命中 DevSwitch（shims 是否在有效 PATH 优先位）。
            var pathCheck = BuildShimsPathCheck();
            if (!pathCheck.ShimsOnEffectivePath)
            {
                dialogTitle = "切换已完成，但终端未生效";
            }

            await RefreshSdkCatalogAsync();
            await ShowSimpleDialogAsync(dialogTitle, $"已切换到 {row.Name}。{note}");
        });
    }

    /// <summary>
    /// 行菜单「验证」：先轻量验证，再询问是否运行命令验证。
    /// </summary>
    private async void OnVerifySdkClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetRow(sender, out var row) || isRowOperationBusy)
        {
            return;
        }

        await RunRowOperationAsync(sender, async () =>
        {
            var service = appServices.CreateVerificationService();
            var record = BuildRecordFromRow(row);

            // 1) 轻量验证：链接指向 + 关键文件存在性。
            var light = await service.LightweightVerifyAsync(record, windowLifetime.Token);

            string statusText = MapStatusText(light.Status);
            string missing = light.MissingKeyFiles.Count == 0
                ? "无"
                : string.Join("、", light.MissingKeyFiles);
            string detail =
                $"状态：{statusText}\n" +
                $"当前使用：{(light.IsCurrent ? "是" : "否")}\n" +
                $"路径存在：{(light.PathExists ? "是" : "否")}\n" +
                $"缺失关键文件：{missing}";

            // 2) 询问是否进一步运行命令验证（会启动外部进程，耗时更长）。
            bool runCommand = await ShowConfirmDialogAsync(
                "轻量验证完成",
                detail + "\n\n是否运行命令验证（执行版本命令读取真实版本）？",
                "运行命令验证",
                "关闭");

            if (!runCommand)
            {
                return;
            }

            var command = await service.RunCommandVerificationAsync(record, windowLifetime.Token);
            string commandDetail = command.Outcome switch
            {
                CommandVerificationOutcome.Verified => $"命令 {command.FileName} 解析到版本：{command.ParsedVersion}。",
                CommandVerificationOutcome.ParseFailed => $"命令 {command.FileName} 执行成功，但无法解析版本号。",
                CommandVerificationOutcome.NotStarted => $"命令 {command.FileName} 未能启动（可能不在 PATH 中）。",
                CommandVerificationOutcome.TimedOut => $"命令 {command.FileName} 执行超时。",
                CommandVerificationOutcome.NonZeroExit => $"命令 {command.FileName} 退出码非零（{command.ExitCode}）。",
                _ => command.Message,
            };

            // 把命令验证结果回写到 catalog：成功时刷新真实版本和 LastVerifiedAt；失败时标记不可用。
            // 刷新 ViewModel 后列表版本列、状态徽章和过滤器都会与 sdks.json 保持一致。
            await PersistCommandVerificationResultAsync(row, command);
            bool verifiedOk = command.Outcome == CommandVerificationOutcome.Verified;
            viewModel.ApplyCommandVerificationResult(row.Id, verifiedOk);
            await RefreshSdkCatalogAsync();

            await ShowSimpleDialogAsync("命令验证结果", commandDetail);
        });
    }

    /// <summary>
    /// 将命令验证结果回写到 sdks.json，确保版本列和状态在刷新后持久一致。
    /// </summary>
    private async Task PersistCommandVerificationResultAsync(SdkVersionRow row, CommandVerificationResult command)
    {
        var store = appServices.CatalogStore;
        var catalog = await store.LoadOrCreateAsync(dataRoot);
        var updated = catalog with
        {
            Items = catalog.Items.Select(item =>
            {
                if (!string.Equals(item.Id, row.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return item;
                }

                bool verifiedOk = command.Outcome == CommandVerificationOutcome.Verified && !string.IsNullOrWhiteSpace(command.ParsedVersion);
                var nextStatus = verifiedOk
                    ? (item.Status == SdkRecordStatus.Active ? SdkRecordStatus.Active : SdkRecordStatus.Usable)
                    : SdkRecordStatus.Unavailable;
                return item with
                {
                    Version = verifiedOk ? command.ParsedVersion! : item.Version,
                    Status = nextStatus,
                    LastVerifiedAt = DateTimeOffset.UtcNow,
                };
            }).ToArray(),
        };
        await store.SaveAsync(dataRoot, updated);
    }

    /// <summary>
    /// 行菜单「打开所在位置」：在资源管理器中定位 SDK 根目录，不阻塞 UI。
    /// </summary>
    private async void OnOpenSdkLocationClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetRow(sender, out var row))
        {
            return;
        }

        if (!SdkLocationExplorer.TryCreateSelectArguments(row.Path, out var fullPath, out var arguments, out var errorMessage))
        {
            var loc = DevSwitch.App.Localization.LocalizationManager.Instance;
            string title = string.IsNullOrWhiteSpace(row.Path)
                ? loc["sdk.openLocation.emptyTitle"]
                : loc["sdk.openLocation.missingTitle"];
            string message = string.IsNullOrWhiteSpace(fullPath)
                ? loc["sdk.openLocation.emptyMessage"]
                : string.Format(loc["sdk.openLocation.missingMessage"], fullPath);
            if (!string.IsNullOrWhiteSpace(errorMessage) && string.IsNullOrWhiteSpace(fullPath))
            {
                message = errorMessage;
            }

            await ShowSimpleDialogAsync(title, message);
            return;
        }

        try
        {
            // explorer.exe 只负责打开窗口，绝不等待退出；路径已预先存在性校验，避免创建空 SDK 目录掩盖问题。
            Process.Start(new ProcessStartInfo("explorer.exe")
            {
                Arguments = arguments!,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            var loc = DevSwitch.App.Localization.LocalizationManager.Instance;
            await ShowSimpleDialogAsync(loc["sdk.openLocation.failedTitle"], ex.Message);
        }
    }

    /// <summary>
    /// 行菜单「编辑名称」：弹输入框修改显示名称并落盘到 sdks.json。
    /// </summary>
    private async void OnEditSdkClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetRow(sender, out var row) || isRowOperationBusy)
        {
            return;
        }

        // 输入框对话框：预填当前名称。
        var input = new TextBox { Text = row.Name, AcceptsReturn = false };
        var dialog = new ContentDialog
        {
            Title = "编辑 SDK 名称",
            Content = input,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        string newName = input.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(newName) || newName == row.Name)
        {
            return;
        }

        await RunRowOperationAsync(sender, async () =>
        {
            // Core 暂无重命名 API，这里直接改 catalog 记录 Name 并保存（与既有 LocalSdkImportService 一致的存储方式）。
            var store = appServices.CatalogStore;
            var catalog = await store.LoadOrCreateAsync(dataRoot);
            var target = catalog.Items.FirstOrDefault(item =>
                string.Equals(item.Id, row.Id, StringComparison.OrdinalIgnoreCase));

            if (target is null)
            {
                await ShowSimpleDialogAsync("编辑失败", "未找到对应的 SDK 记录，可能已被删除，请刷新后重试。");
                return;
            }

            var updated = catalog with
            {
                Items = catalog.Items
                    .Select(item => string.Equals(item.Id, row.Id, StringComparison.OrdinalIgnoreCase)
                        ? item with { Name = newName }
                        : item)
                    .ToArray(),
            };

            await store.SaveAsync(dataRoot, updated);
            await RefreshSdkCatalogAsync();
        });
    }

    /// <summary>
    /// 行菜单「删除」：确认后调用 SdkDeletionService。外部 SDK 仅删记录，托管 SDK 可选删实体。
    /// </summary>
    private async void OnDeleteSdkClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetRow(sender, out var row) || isRowOperationBusy)
        {
            return;
        }

        // 托管 SDK 才提供「同时删除实体文件」的复选项；外部 SDK 永不删实体。
        bool isManaged = string.Equals(row.Source, "托管", StringComparison.Ordinal);
        var deleteEntityCheck = new CheckBox
        {
            Content = "同时删除已下载的 SDK 文件（仅托管 SDK）",
            IsChecked = false,
            IsEnabled = isManaged,
        };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = isManaged
                ? $"确认删除「{row.Name}」？\n默认仅移除登记记录；如需连同已下载文件一起删除，请勾选下方选项。"
                : $"确认删除「{row.Name}」？\n这是外部导入的 SDK，只会移除 DevSwitch 的登记记录，不会删除你的原始目录。",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(deleteEntityCheck);

        var dialog = new ContentDialog
        {
            Title = "删除 SDK",
            Content = panel,
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        bool confirmDeleteFiles = isManaged && deleteEntityCheck.IsChecked == true;

        await RunRowOperationAsync(sender, async () =>
        {
            var service = appServices.CreateDeletionService();
            var result = await service.DeleteAsync(dataRoot, row.Id, confirmDeleteFiles, windowLifetime.Token);

            if (result.Success)
            {
                await RefreshSdkCatalogAsync();
                string entityNote = result.EntityDeleted
                    ? "已删除登记记录与 SDK 文件。"
                    : result.EntityPreservedPendingConfirmation
                        ? "已删除登记记录，SDK 文件已保留。"
                        : "已删除登记记录。";
                await ShowSimpleDialogAsync("删除成功", entityNote);
            }
            else
            {
                await ShowSimpleDialogAsync("删除失败", FormatError(result.ErrorCode, result.Message));
            }
        });
    }

    // ===================== 顶部：下载 / 重置 =====================

    /// <summary>
    /// 顶部「下载」按钮：打开下载对话框，列出真实版本并执行下载。
    /// </summary>
    private async void OnDownloadSdkClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // helper 不是下载必须，但下载对话框需要数据根与并发数等设置。
            // 传入窗口句柄，供对话框内 FolderPicker 自定义安装目录使用。
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var dialog = new Dialogs.DownloadDialog(appServices, viewModel.SelectedCategory, hwnd)
            {
                XamlRoot = RootGrid.XamlRoot,
            };

            var result = await dialog.ShowAsync();

            // 若对话框完成了一次下载并登记，刷新列表展示新托管 SDK。
            if (dialog.DidRegisterSdk)
            {
                await RefreshSdkCatalogAsync();
            }
        }
        catch (Exception ex)
        {
            await ShowSimpleDialogAsync("下载失败", ex.Message);
        }
    }

    /// <summary>
    /// 顶部「重置」按钮：确认后调用 EnvironmentResetService 恢复 DevSwitch 管理的环境，不删 SDK。
    /// </summary>
    private async void OnResetEnvironmentClick(object sender, RoutedEventArgs e)
    {
        bool confirmed = await ShowConfirmDialogAsync(
            "重置工具环境",
            "此操作会恢复 DevSwitch 管理的环境状态：\n" +
            "· 移除 DevSwitch 写入的 PATH 片段\n" +
            "· 清除 DEVSWITCH_HOME / JAVA_HOME / MAVEN_HOME / GOROOT 等托管变量\n" +
            "· 移除 current 入口链接\n" +
            "· 清空当前使用状态\n\n" +
            "不会删除任何 SDK 文件。是否继续？",
            "重置",
            "取消");

        if (!confirmed)
        {
            return;
        }

        var button = ResetEnvironmentButton;
        button.IsEnabled = false;
        try
        {
            var service = appServices.CreateResetService();
            var result = await service.ResetAsync(dataRoot, EnvironmentResetOptions.Default, windowLifetime.Token);

            if (result.Success)
            {
                await RefreshSdkCatalogAsync();
                await ShowSimpleDialogAsync(
                    "重置完成",
                    $"已移除托管 PATH 片段 {result.RemovedPathEntries.Count} 项、current 链接 {result.RemovedCurrentLinks.Count} 个。\n" +
                    "请重启终端使环境变量变更生效。");
            }
            else
            {
                await ShowSimpleDialogAsync("重置失败", FormatError(result.ErrorCode, result.Message));
            }
        }
        catch (HelperUnavailableException ex)
        {
            await ShowSimpleDialogAsync("无法重置", ex.Message);
        }
        catch (Exception ex)
        {
            await ShowSimpleDialogAsync("重置失败", ex.Message);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    // ===================== 顶部：检测当前 =====================

    // 「检测当前」防重入标志：避免连续点击同时启动多个外部进程。
    private bool isDetectCurrentBusy;

    // 检测命令统一超时：8 秒足够版本命令返回。
    private static readonly TimeSpan DetectCommandTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// 顶部「检测当前」按钮：同时检测两个口径并对比，帮助判断切换是否真正对终端生效。
    /// 1) PATH 口径：裸命令名经 cmd /c 走系统 PATH，反映「当前终端实际会用到的版本」——
    ///    若 PATH 中存在其它 SDK 的残留条目排在 DevSwitch 托管片段之前，这里会暴露被遮蔽的旧版本。
    /// 2) JAVA_HOME 口径（仅 Java/Maven/Go 有意义）：用 HOME 变量（JAVA_HOME/MAVEN_HOME/GOROOT）
    ///    展开后的绝对路径运行版本命令，反映「DevSwitch 期望生效的版本」。
    /// 两者不一致即说明发生了 PATH 遮蔽，切换尚未真正落到终端。
    /// </summary>
    private async void OnDetectCurrentClick(object sender, RoutedEventArgs e)
    {
        // 防重入：检测进行中直接忽略后续点击。
        if (isDetectCurrentBusy)
        {
            return;
        }

        // 当前 SDK 分类（"Java"/"Maven"/"Node.js"/"Go"）映射为 SdkType。
        var type = MapCategoryToSdkType(viewModel.SelectedCategory);

        var button = sender as Button;
        if (button is not null)
        {
            button.IsEnabled = false;
        }
        isDetectCurrentBusy = true;

        try
        {
            // 并行执行两个口径检测，互不依赖，缩短总耗时；均不阻塞 UI。
            var pathTask = DetectViaPathAsync(type);
            var homeTask = DetectViaHomeAsync(type);
            await Task.WhenAll(pathTask, homeTask);

            var pathResult = pathTask.Result;
            var homeResult = homeTask.Result;

            // 组装两口径对比文案并展示。
            string message = BuildDetectMessage(viewModel.SelectedCategory, pathResult, homeResult);
            await ShowSimpleDialogAsync("检测当前", message);
        }
        catch (OperationCanceledException)
        {
            // 窗口关闭等导致取消：静默退出，不打扰用户。
        }
        catch (Exception ex)
        {
            await ShowSimpleDialogAsync("检测失败", ex.Message);
        }
        finally
        {
            isDetectCurrentBusy = false;
            if (button is not null)
            {
                button.IsEnabled = true;
            }
        }
    }

    /// <summary>
    /// PATH 口径检测结果。Detected 表示成功解析到版本；Version 为解析版本；Raw 为命令首行佐证。
    /// </summary>
    private readonly record struct PathDetectResult(bool Detected, string? Version, string Raw, string CommandName);

    /// <summary>
    /// JAVA_HOME 口径检测结果。
    /// Applicable=false 表示该类型无对应 HOME 变量（如 Node）；Configured=false 表示 HOME 变量未配置或目录/可执行无效。
    /// </summary>
    private readonly record struct HomeDetectResult(bool Applicable, bool Configured, string? Version, string Raw, string HomeVarName);

    /// <summary>
    /// shims 单条 PATH 写后校验结果：shims 是否存在于有效 PATH，且无更靠前条目遮蔽命令解析。
    /// </summary>
    private readonly record struct ShimsPathCheck(
        bool ShimsPresent,
        bool ShimsOnEffectivePath,
        string? ShadowingEntry,
        string? ShadowingScope);

    /// <summary>
    /// PATH 口径：经 cmd.exe /c &lt;cmd&gt; &lt;args&gt; 走系统 PATH 运行版本命令，解析当前终端实际生效版本。
    /// NOTE: Windows 下 Process.Start("mvn") 找不到 mvn.cmd；交给 cmd /c 才能稳定命中 java/mvn/node/go。
    /// </summary>
    private async Task<PathDetectResult> DetectViaPathAsync(SdkType type)
    {
        // GetVersionCommand 返回裸命令名与参数，如 ("java", ["-version"])；裸名正是用来走系统 PATH。
        var (commandName, commandArgs) = SdkVerificationService.GetVersionCommand(type);

        string comSpec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        var argList = new List<string>(commandArgs.Count + 2) { "/c", commandName };
        argList.AddRange(commandArgs);

        // 使用注册表里的 Machine+User 合并 PATH 覆盖子进程环境，模拟“新打开的 Windows 终端”。
        // 不能沿用 DevSwitch App 当前进程 PATH：应用启动后 PATH 不会自动刷新，会误报切换失败/成功。
        var envOverrides = BuildRegistryBackedEnvironmentOverrides();
        var execution = await ProcessExecution.RunAsync(
            comSpec, argList, DetectCommandTimeout, windowLifetime.Token, envOverrides);

        // 解析版本：Java 在 stderr，其余在 stdout，ParseVersion 已兼容处理。
        string? version = execution.Started && execution.ExitCode == 0 && !execution.TimedOut
            ? SdkVerificationService.ParseVersion(type, execution.StdOut, execution.StdErr)
            : null;

        string raw = FirstNonEmptyLine(execution.StdOut, execution.StdErr);
        return new PathDetectResult(!string.IsNullOrWhiteSpace(version), version, raw, commandName);
    }

    /// <summary>
    /// JAVA_HOME 口径：读 HKCU 用户级 HOME 变量（JAVA_HOME/MAVEN_HOME/GOROOT），展开后用绝对路径运行版本命令，
    /// 反映 DevSwitch 期望生效的版本。Node 无对应 HOME 变量，返回「不适用」。
    /// </summary>
    private async Task<HomeDetectResult> DetectViaHomeAsync(SdkType type)
    {
        // HOME 变量名映射：Java→JAVA_HOME、Maven→MAVEN_HOME、Go→GOROOT；Node 不适用。
        string? homeVarName = type switch
        {
            SdkType.Java => "JAVA_HOME",
            SdkType.Maven => "MAVEN_HOME",
            SdkType.Go => "GOROOT",
            _ => null,
        };

        if (homeVarName is null)
        {
            // Node：DevSwitch 不设 NODE_HOME，PATH 直接指向 current\node，HOME 口径不适用。
            return new HomeDetectResult(Applicable: false, Configured: false, Version: null, Raw: string.Empty, HomeVarName: string.Empty);
        }

        // 读用户级变量并展开 %VAR% 占位（HOME 多引用 %DEVSWITCH_HOME%）。
        string? rawHome = Environment.GetEnvironmentVariable(homeVarName, EnvironmentVariableTarget.User);
        string home = string.IsNullOrWhiteSpace(rawHome)
            ? string.Empty
            : Environment.ExpandEnvironmentVariables(rawHome);

        // HOME 未配置或目录不存在：视为未配置/无效。
        if (string.IsNullOrWhiteSpace(home) || !Directory.Exists(home))
        {
            return new HomeDetectResult(Applicable: true, Configured: false, Version: null, Raw: string.Empty, HomeVarName: homeVarName);
        }

        ProcessExecutionResult execution;
        if (type == SdkType.Maven)
        {
            // Maven：mvn.cmd 是批处理，经 cmd /c 运行；并把展开后的 JAVA_HOME 注入子进程环境，
            //        否则 mvn 可能因 JAVA_HOME 缺失/被遮蔽而报错或用错 JDK。
            string mvnCmd = Path.Combine(home, "bin", "mvn.cmd");
            if (!File.Exists(mvnCmd))
            {
                return new HomeDetectResult(Applicable: true, Configured: false, Version: null, Raw: string.Empty, HomeVarName: homeVarName);
            }

            string comSpec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            var (_, mvnArgs) = SdkVerificationService.GetVersionCommand(SdkType.Maven);
            var argList = new List<string>(mvnArgs.Count + 2) { "/c", mvnCmd };
            argList.AddRange(mvnArgs);

            // 注入展开后的用户级 JAVA_HOME 到子进程，供 mvn.cmd 定位 JDK。
            IReadOnlyDictionary<string, string>? envOverrides = null;
            string? rawJavaHome = Environment.GetEnvironmentVariable("JAVA_HOME", EnvironmentVariableTarget.User);
            if (!string.IsNullOrWhiteSpace(rawJavaHome))
            {
                string javaHome = Environment.ExpandEnvironmentVariables(rawJavaHome);
                if (Directory.Exists(javaHome))
                {
                    envOverrides = new Dictionary<string, string> { ["JAVA_HOME"] = javaHome };
                }
            }

            execution = await ProcessExecution.RunAsync(
                comSpec, argList, DetectCommandTimeout, windowLifetime.Token, envOverrides);
        }
        else
        {
            // Java→bin\java.exe，Go→bin\go.exe：可直接以绝对路径启动（.exe 无需 cmd /c）。
            string exeName = type == SdkType.Java ? "java.exe" : "go.exe";
            string exePath = Path.Combine(home, "bin", exeName);
            if (!File.Exists(exePath))
            {
                return new HomeDetectResult(Applicable: true, Configured: false, Version: null, Raw: string.Empty, HomeVarName: homeVarName);
            }

            var (_, args) = SdkVerificationService.GetVersionCommand(type);
            execution = await ProcessExecution.RunAsync(
                exePath, args, DetectCommandTimeout, windowLifetime.Token);
        }

        string? version = execution.Started && execution.ExitCode == 0 && !execution.TimedOut
            ? SdkVerificationService.ParseVersion(type, execution.StdOut, execution.StdErr)
            : null;

        if (string.IsNullOrWhiteSpace(version))
        {
            // 命令存在但执行失败/无法解析：按未配置/无效处理（避免误判为一致）。
            return new HomeDetectResult(Applicable: true, Configured: false, Version: null, Raw: string.Empty, HomeVarName: homeVarName);
        }

        string raw = FirstNonEmptyLine(execution.StdOut, execution.StdErr);
        return new HomeDetectResult(Applicable: true, Configured: true, version, raw, homeVarName);
    }

    /// <summary>
    /// 组装两口径对比文案：PATH（终端实际生效）对照 HOME（DevSwitch 期望），并给出一致/遮蔽提示。
    /// </summary>
    private static string BuildDetectMessage(string category, PathDetectResult pathResult, HomeDetectResult homeResult)
    {
        // PATH 口径展示值。
        string pathLine = pathResult.Detected
            ? $"当前终端实际生效（PATH）：{pathResult.Version}"
            : $"当前终端实际生效（PATH）：未检测到 {pathResult.CommandName}（可能未加入 PATH 或未切换）";

        // HOME 口径展示值。
        string homeLine;
        if (!homeResult.Applicable)
        {
            homeLine = "DevSwitch 期望（HOME）：不适用";
        }
        else if (!homeResult.Configured)
        {
            homeLine = $"DevSwitch 期望（{homeResult.HomeVarName}）：未配置/无效";
        }
        else
        {
            homeLine = $"DevSwitch 期望（{homeResult.HomeVarName}）：{homeResult.Version}";
        }

        var sb = new System.Text.StringBuilder();
        sb.Append(pathLine).Append('\n').Append(homeLine);

        // 仅当两个口径都拿到版本时才比较一致性；否则给出相应说明。
        if (homeResult.Applicable && homeResult.Configured && pathResult.Detected)
        {
            bool consistent = string.Equals(
                pathResult.Version?.Trim(), homeResult.Version?.Trim(), StringComparison.OrdinalIgnoreCase);

            if (consistent)
            {
                sb.Append("\n\n").Append($"PATH 与 DevSwitch 期望一致：{homeResult.Version}，切换已生效。");
            }
            else
            {
                sb.Append("\n\n")
                  .Append("⚠ 两者不一致：PATH 中有其它条目遮蔽了 DevSwitch，切换未对终端生效。\n")
                  .Append("建议：在「环境诊断」查看 PATH 前序冲突，或重启终端；DevSwitch 已尝试把托管片段置顶。");
            }
        }
        else if (!homeResult.Applicable && pathResult.Detected)
        {
            // Node 等无 HOME 口径：PATH 已能反映 current 指向，给出温和说明。
            sb.Append("\n\n").Append($"该类型无 HOME 口径，PATH 已直接指向 DevSwitch current 入口。");
        }
        else if (homeResult.Applicable && !homeResult.Configured)
        {
            sb.Append("\n\n").Append($"未读到有效的 {homeResult.HomeVarName}，可在「环境诊断」检查或切换一个版本后重试。");
        }
        else if (!pathResult.Detected)
        {
            sb.Append("\n\n").Append("PATH 口径未检测到命令，请切换一个版本后重启终端再试。");
        }

        // 附命令原始输出佐证（优先 PATH，其次 HOME），便于核对。
        string raw = !string.IsNullOrEmpty(pathResult.Raw) ? pathResult.Raw : homeResult.Raw;
        if (!string.IsNullOrEmpty(raw))
        {
            sb.Append("\n\n命令输出：").Append(raw);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 把 SDK 分类名映射为 SdkType（与 DownloadDialog.ParseSdkType 一致）。
    /// </summary>
    private static SdkType MapCategoryToSdkType(string category) => category switch
    {
        "Java" => SdkType.Java,
        "Maven" => SdkType.Maven,
        "Node.js" => SdkType.Node,
        "Go" => SdkType.Go,
        "Rust" => SdkType.Rust,
        _ => SdkType.Unknown,
    };

    /// <summary>
    /// 从注册表重建新 Windows 终端会看到的环境覆盖：Machine PATH 在前、User PATH 在后，
    /// 并把用户级 DEVSWITCH_HOME/JAVA_HOME/MAVEN_HOME/GOROOT 一并注入，避免检测沿用 App 旧进程环境。
    /// </summary>
    private static IReadOnlyDictionary<string, string>? BuildRegistryBackedEnvironmentOverrides()
    {
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string effectivePath = BuildEffectivePathFromRegistry();
        if (!string.IsNullOrWhiteSpace(effectivePath))
        {
            overrides["Path"] = effectivePath;
        }

        foreach (var name in new[] { "DEVSWITCH_HOME", "JAVA_HOME", "MAVEN_HOME", "GOROOT" })
        {
            string? value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
            if (!string.IsNullOrWhiteSpace(value))
            {
                overrides[name] = value;
            }
        }

        return overrides.Count == 0 ? null : overrides;
    }

    /// <summary>
    /// 按 Windows 新进程规则合并 PATH：系统 PATH（Machine/HKLM）在前，用户 PATH（User/HKCU）在后。
    /// </summary>
    private static string BuildEffectivePathFromRegistry()
    {
        string? machinePath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine);
        string? userPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);

        if (string.IsNullOrWhiteSpace(machinePath))
        {
            return userPath ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(userPath))
        {
            return machinePath;
        }

        return machinePath.TrimEnd(';') + ";" + userPath.TrimStart(';');
    }

    /// <summary>
    /// 切换后的环境收尾（shim 单目录方案）：
    /// 1) 确保用户级 HOME 变量已写（JAVA_HOME/MAVEN_HOME/GOROOT 指向 current，幂等，无 UAC）；
    /// 2) rebuildShims：按 current 下真实可执行刷新 shims；
    /// 3) 确保系统 PATH 含 shims 目录（仅缺失时按需提权一次写入）。
    /// 返回用于切换对话框的说明文本。
    /// </summary>
    private async Task<string> FinalizeSwitchEnvironmentAsync(SdkType type)
    {
        var sb = new System.Text.StringBuilder();

        // 1) 写用户级 HOME 变量（不含 PATH，不占系统 PATH 条目；mvn 等依赖 JAVA_HOME）。
        var environmentService = appServices.CreateEnvironmentService();
        var variables = new[]
        {
            new EnvironmentVariable(EnvironmentLayout.DevSwitchHomeName, dataRoot),
            new EnvironmentVariable("JAVA_HOME", System.IO.Path.Combine(dataRoot, "current", "java")),
            new EnvironmentVariable("MAVEN_HOME", System.IO.Path.Combine(dataRoot, "current", "maven")),
            new EnvironmentVariable("GOROOT", System.IO.Path.Combine(dataRoot, "current", "go")),
        };
        var writeResult = await environmentService.WriteVariablesAsync(variables, broadcast: false, windowLifetime.Token);
        sb.Append("\n\n已更新环境：");
        sb.Append(writeResult.Success
            ? "\n· 用户变量 DEVSWITCH_HOME / JAVA_HOME / MAVEN_HOME / GOROOT ✓"
            : $"\n· ⚠ 用户变量写入未完成（{writeResult.ErrorCode}）");

        // 2) 重建 shims（按 current 下真实可执行）。shim 源由 App 随包提供。
        if (string.IsNullOrWhiteSpace(appServices.ShimPath))
        {
            sb.Append("\n· ⚠ 未找到 DevSwitch.Shim.exe，无法生成命令转发器。");
        }
        else
        {
            var shimResult = await environmentService.RebuildShimsAsync(dataRoot, appServices.ShimPath!, windowLifetime.Token);
            sb.Append(shimResult.Success
                ? $"\n· 命令转发器 shims 已刷新 ✓（{shimResult.AddedPathEntries.Count} 个）"
                : $"\n· ⚠ shims 刷新失败（{shimResult.ErrorCode}）");
        }

        // 3) 确保系统 PATH 含 shims 目录（仅当缺失时写一次；稳态切换跳过，无 UAC）。
        string shimsDir = EnvironmentLayout.BuildShimsPathEntry(dataRoot);
        bool shimsInMachinePath = SplitPathVariable(Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine))
            .Any(entry => PathsEquivalent(entry, shimsDir));

        if (shimsInMachinePath)
        {
            sb.Append("\n· 系统 PATH 已含 shims（无需改动）✓");
        }
        else
        {
            // 按需提权写一次系统 PATH（asInvoker 进程通过 runas 启动 helper CLI）。
            var install = await EnsureShimsInMachinePathAsync(shimsDir);
            sb.Append(install);
        }

        sb.Append("\n\n注意：已打开的终端需重启后才会读取新环境。");
        return sb.ToString();
    }

    /// <summary>
    /// 校验 shims 单条目是否已在 Windows 有效 PATH（系统 PATH 在前、用户 PATH 在后）中优先命中。
    /// </summary>
    private ShimsPathCheck BuildShimsPathCheck()
    {
        string shimsDir = EnvironmentLayout.BuildShimsPathEntry(dataRoot);

        var machine = SplitPathVariable(Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine))
            .Select(entry => (Entry: entry, Scope: "系统 PATH"));
        var user = SplitPathVariable(Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User))
            .Select(entry => (Entry: entry, Scope: "用户 PATH"));
        var scoped = machine.Concat(user).ToList();

        bool present = scoped.Any(e => PathsEquivalent(e.Entry, shimsDir));

        // shims 提供 java/mvn/node/go；任一这些命令在 shims 之前被其它条目命中即视为遮蔽。
        var commandNames = new[] { "java.exe", "mvn.cmd", "node.exe", "go.exe" };
        foreach (var (entry, scope) in scoped)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }
            if (PathsEquivalent(entry, shimsDir))
            {
                // 命中 shims：优先生效。
                return new ShimsPathCheck(present, ShimsOnEffectivePath: true, null, null);
            }
            if (EntryProvidesAnyCommand(entry, commandNames))
            {
                // 在 shims 之前有其它条目提供这些命令 → 遮蔽。
                return new ShimsPathCheck(present, ShimsOnEffectivePath: false, entry, scope);
            }
        }

        return new ShimsPathCheck(present, ShimsOnEffectivePath: present, null, null);
    }

    /// <summary>
    /// 确保 shims 目录已写入系统 PATH 最前：通过 runas 提权启动 helper CLI 模式（一次性，仅缺失时调用）。
    /// </summary>
    private async Task<string> EnsureShimsInMachinePathAsync(string shimsDir)
    {
        if (string.IsNullOrWhiteSpace(appServices.HelperPath))
        {
            return "\n· ⚠ 未找到 helper，无法写入系统 PATH。";
        }

        try
        {
            int exitCode = await Task.Run(() => RunHelperElevated("--install-machine-path", shimsDir));
            return exitCode switch
            {
                0 => "\n· 系统 PATH：已加入 shims 目录（已提权写入）✓",
                5 => "\n· ⚠ 系统 PATH 写入被拒（未通过管理员授权）。请在切换时点「是」允许提权。",
                1223 => "\n· ⚠ 已取消管理员授权，未写入系统 PATH。下次切换可再授权。",
                _ => $"\n· ⚠ 系统 PATH 写入失败（退出码 {exitCode}）。",
            };
        }
        catch (Exception ex)
        {
            return $"\n· ⚠ 系统 PATH 写入异常：{ex.Message}";
        }
    }

    /// <summary>
    /// 以管理员权限（UAC runas）启动 helper 的 CLI 模式并等待退出，返回退出码。
    /// 退出码 1223 表示用户取消了 UAC。
    /// </summary>
    private int RunHelperElevated(string mode, string argument)
    {
        var startInfo = new ProcessStartInfo(appServices.HelperPath!)
        {
            UseShellExecute = true,   // runas 必须 UseShellExecute=true
            Verb = "runas",
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = $"{mode} \"{argument}\"",
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return -1;
            }
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 用户在 UAC 弹窗点了「否」。
            return 1223;
        }
    }

    /// <summary>
    /// 拆分 PATH 变量，保留非空条目原始顺序。
    /// </summary>
    private static IReadOnlyList<string> SplitPathVariable(string? rawPath)
    {
        return string.IsNullOrWhiteSpace(rawPath)
            ? Array.Empty<string>()
            : rawPath.Split(';', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// 比较两个 PATH 条目是否等价：展开环境变量、取完整路径、去掉尾部分隔符、Windows 下忽略大小写。
    /// </summary>
    private static bool PathsEquivalent(string left, string right)
    {
        try
        {
            string Normalize(string value)
            {
                string expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
                return System.IO.Path.GetFullPath(expanded)
                    .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            }

            return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// 判断 PATH 条目目录是否提供任一目标命令文件。条目可能含环境变量，需先展开。
    /// </summary>
    private static bool EntryProvidesAnyCommand(string entry, IReadOnlyList<string> commandNames)
    {
        if (string.IsNullOrWhiteSpace(entry) || commandNames.Count == 0)
        {
            return false;
        }

        string directory;
        try
        {
            directory = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        foreach (string commandName in commandNames)
        {
            try
            {
                if (System.IO.File.Exists(System.IO.Path.Combine(directory, commandName)))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // 非法 PATH 条目跳过，不让诊断本身失败。
            }
        }

        return false;
    }

    /// <summary>
    /// SDK 类型对应的 current 子目录名。
    /// </summary>
    private static string SdkTypeSlug(SdkType type) => type switch
    {
        SdkType.Java => "java",
        SdkType.Maven => "maven",
        SdkType.Node => "node",
        SdkType.Go => "go",
        SdkType.Rust => "rust",
        _ => "sdk",
    };

    /// <summary>
    /// 取多段文本中第一段非空内容的首行（已去除首尾空白），用于展示命令原始输出佐证。
    /// </summary>
    private static string FirstNonEmptyLine(params string[] texts)
    {
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            // 兼容 \r\n / \n 换行，取首个非空行。
            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    return line.Trim();
                }
            }
        }

        return string.Empty;
    }

    // ===================== 诊断 =====================

    /// <summary>
    /// 显示诊断内容区。
    /// </summary>
    private void ShowDoctorContent()
    {
        var doctorContent = EnsureDeferredContent<Grid>(nameof(DoctorContent));
        SetActiveContent(doctorContent);
    }

    /// <summary>
    /// 「重新诊断」按钮：运行 DoctorService 并渲染结果。
    /// </summary>
    private async void OnRunDoctorClick(object sender, RoutedEventArgs e)
    {
        await RunDoctorAsync();
    }

    /// <summary>
    /// 运行 Doctor 诊断并把报告渲染到结果列表。
    /// </summary>
    private async Task RunDoctorAsync()
    {
        if (isDoctorRunning)
        {
            return;
        }

        isDoctorRunning = true;
        RunDoctorButton.IsEnabled = false;
        DoctorProgressRing.IsActive = true;
        DoctorProgressRing.Visibility = Visibility.Visible;
        DoctorSummaryText.Text = "正在运行诊断检查……";

        try
        {
            var service = appServices.CreateDoctorService();
            var report = await service.RunAsync(windowLifetime.Token);

            var rows = report.Results.Select(DoctorResultRow.FromResult).ToArray();
            DoctorResultsControl.ItemsSource = rows;

            DoctorSummaryText.Text =
                $"诊断完成：整体等级 {MapSeverityText(report.OverallSeverity)}，" +
                $"通过 {report.CountOf(DiagnosticSeverity.Pass)} · " +
                $"信息 {report.CountOf(DiagnosticSeverity.Info)} · " +
                $"警告 {report.CountOf(DiagnosticSeverity.Warning)} · " +
                $"错误 {report.CountOf(DiagnosticSeverity.Error)} · " +
                $"致命 {report.CountOf(DiagnosticSeverity.Fatal)}。";
        }
        catch (HelperUnavailableException ex)
        {
            DoctorSummaryText.Text = ex.Message;
        }
        catch (OperationCanceledException)
        {
            // 窗口关闭取消，静默。
        }
        catch (Exception ex)
        {
            DoctorSummaryText.Text = $"诊断执行异常：{ex.Message}";
        }
        finally
        {
            isDoctorRunning = false;
            RunDoctorButton.IsEnabled = true;
            DoctorProgressRing.IsActive = false;
            DoctorProgressRing.Visibility = Visibility.Collapsed;
        }
    }

    // ===================== 设置页 =====================

    /// <summary>
    /// 初始化强调色色块列表。
    /// 色块使用普通 Button + Border 绘制，不依赖第三方控件；按钮 Tag 保存调色板 key，点击后统一处理。
    /// </summary>
    private void InitializeAccentSwatches()
    {
        if (AccentSwatchItems is null)
        {
            return;
        }

        AccentSwatchItems.Items.Clear();

        foreach (var option in AccentPalette.All)
        {
            // NOTE: 每个按钮内部放一个圆角色块；选中态通过按钮边框颜色/粗细表达，可被键盘聚焦与点击。
            var swatch = new Button
            {
                Tag = option.Key,
                Width = 34,
                Height = 34,
                MinWidth = 34,
                Padding = new Thickness(3),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderBrush = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(1),
                Content = new Border
                {
                    Width = 20,
                    Height = 20,
                    CornerRadius = new CornerRadius(10),
                    Background = new SolidColorBrush(AccentThemeService.ColorFromHex(option.Accent)),
                },
            };

            swatch.Click += OnAccentSwatchClick;
            var displayName = GetAccentDisplayName(option);
            ToolTipService.SetToolTip(swatch, displayName);
            AutomationProperties.SetName(swatch, displayName);
            AccentSwatchItems.Items.Add(swatch);
        }

        UpdateAccentSwatchSelection();
    }

    /// <summary>
    /// 强调色色块点击：即时应用主题色，并持久化到 settings.AccentColor。
    /// </summary>
    private async void OnAccentSwatchClick(object sender, RoutedEventArgs e)
    {
        if (isSettingsInitializing || sender is not Button button)
        {
            return;
        }

        var accentKey = button.Tag?.ToString();
        var option = AccentPalette.Resolve(accentKey);
        selectedAccentKey = option.Key;

        // 先即时换色，让用户点击后马上看到反馈；随后异步写 settings.json。
        AccentThemeService.Apply(option.Key);
        UpdateAccentSwatchSelection();
        await SaveSettingsAsync(settings => settings with { AccentColor = option.Key });
    }

    /// <summary>
    /// 根据当前语言返回强调色显示名，用于色块 ToolTip 与无障碍名称。
    /// </summary>
    private static string GetAccentDisplayName(AccentColorOption option)
    {
        var language = DevSwitch.App.Localization.LocalizationManager.Instance.CurrentLanguage;
        return language == DevSwitch.Core.Localization.AppLanguage.English
            ? option.DisplayNameEn
            : option.DisplayNameZh;
    }

    /// <summary>
    /// 刷新色块选中态：当前色为强调色边框，其余为透明边框。
    /// 语言切换时也会调用，顺带刷新 ToolTip 显示名。
    /// </summary>
    private void UpdateAccentSwatchSelection()
    {
        if (AccentSwatchItems is null)
        {
            return;
        }

        foreach (var item in AccentSwatchItems.Items)
        {
            if (item is not Button button)
            {
                continue;
            }

            var option = AccentPalette.Resolve(button.Tag?.ToString());
            bool selected = string.Equals(option.Key, selectedAccentKey, StringComparison.OrdinalIgnoreCase);
            button.BorderBrush = selected
                ? new SolidColorBrush(AccentThemeService.ColorFromHex(option.Accent))
                : new SolidColorBrush(Colors.Transparent);
            button.BorderThickness = selected ? new Thickness(2) : new Thickness(1);
            var displayName = GetAccentDisplayName(option);
            ToolTipService.SetToolTip(button, displayName);
            AutomationProperties.SetName(button, displayName);
        }
    }

    /// <summary>
    /// 启动后加载设置，把语言与下载并发数初值同步到设置页控件。
    /// </summary>
    private async Task LoadSettingsIntoUiAsync()
    {
        try
        {
            var settings = await DevSwitchSettingsStore.LoadOrCreateAsync(dataRoot);

            isSettingsInitializing = true;

            // 语言：启动阶段必须立即应用到 LocalizationManager；设置页控件可能因 x:Load=False 尚未创建。
            // ApplyFromSettings 若与当前语言不同会触发 LanguageChanged，自动刷新已加载界面文案。
            DevSwitch.App.Localization.LocalizationManager.Instance.ApplyFromSettings(settings.Language);
            if (LanguageComboBox is not null)
            {
                SelectComboBoxByTag(LanguageComboBox, settings.Language);
                UpdateLanguageStatus();
            }

            // 强调色：启动时 App.xaml.cs 已先应用默认色，这里按 settings 校正；色块控件未加载时只保存 key。
            selectedAccentKey = AccentPalette.Resolve(settings.AccentColor).Key;
            AccentThemeService.Apply(selectedAccentKey);
            if (AccentSwatchItems is not null)
            {
                UpdateAccentSwatchSelection();
            }

            // 下载并发数：设置页首次打开后再同步 ComboBox，避免为了显示设置强制实例化整页。
            if (DownloadParallelismComboBox is not null)
            {
                SelectComboBoxByTag(DownloadParallelismComboBox, settings.Download.Parallelism.ToString());
                DownloadParallelismStatusText.Text = $"当前并发数：{settings.Download.Parallelism}";
            }

            // 数据目录与更新配置属于设置页文案；控件未加载时跳过，打开设置页后会再次调用本方法补齐。
            if (DataRootPathText is not null)
            {
                UpdateDataRootText();
            }

            if (UpdateRepositoryTextBox is not null)
            {
                // 更新：回填 GitHub 仓库与当前版本号。仓库为空时用默认仓库占位，方便用户直接检查更新。
                UpdateRepositoryTextBox.Text = string.IsNullOrWhiteSpace(settings.Update.Repository)
                    ? "gongzhujiejie/devswitch"
                    : settings.Update.Repository;
                UpdateCurrentVersionText.Text = $"DevSwitch 当前版本 {CurrentAppVersion}";
            }
        }
        catch
        {
            // 设置读取失败不影响主流程，保留控件默认值。
        }
        finally
        {
            isSettingsInitializing = false;
        }
    }

    /// <summary>
    /// 语言选择变更：持久化到 settings.json 并即时热切换界面语言（无需重启）。
    /// </summary>
    private async void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isSettingsInitializing || LanguageComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        string languageTag = item.Tag?.ToString() ?? "auto";

        // 即时热切换：用 ApplyFromSettings 解析（auto 按系统区域），变更会触发 LanguageChanged 事件，
        // 由 RefreshLocalizedTexts 重新拉取所有界面文案，无需重启。
        DevSwitch.App.Localization.LocalizationManager.Instance.ApplyFromSettings(languageTag);

        // 持久化用户选择。
        await SaveSettingsAsync(settings => settings with { Language = languageTag });

        UpdateLanguageStatus();
    }

    /// <summary>
    /// 下载并发数变更：持久化到 settings.Download.Parallelism。
    /// </summary>
    private async void OnDownloadParallelismChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isSettingsInitializing || DownloadParallelismComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        if (!int.TryParse(item.Tag?.ToString(), out int parallelism))
        {
            return;
        }

        DownloadParallelismStatusText.Text = $"当前并发数：{parallelism}";
        await SaveSettingsAsync(settings => settings with
        {
            Download = settings.Download with { Parallelism = parallelism },
        });
    }

    // 最近一次检查发现的新版本计划（供「下载并更新」使用）。
    private DevSwitch.Core.SelfUpdatePlan? pendingUpdatePlan;

    /// <summary>
    /// 「检查更新」按钮：用设置的 GitHub 仓库检查最新版本；发现新版则显示「下载并更新」。
    /// </summary>
    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        DownloadUpdateButton.Visibility = Visibility.Collapsed;
        pendingUpdatePlan = null;
        UpdateStatusText.Visibility = Visibility.Visible;
        UpdateStatusText.Text = "正在检查更新……";

        try
        {
            // 以输入框当前值为准（已回填默认仓库），避免旧 settings.json 中 Repository 为空导致无法检查。
            string repo = UpdateRepositoryTextBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(repo))
            {
                UpdateStatusText.Text = "请先填写 GitHub 仓库（owner/repo）再检查更新。";
                return;
            }

            // 用自更新服务解析计划（含资产）；同时据此判断是否有新版本。
            var selfUpdate = appServices.CreateSelfUpdateService();
            var plan = await selfUpdate.ResolvePlanAsync(repo, includePrerelease: false, windowLifetime.Token);
            if (plan is null)
            {
                UpdateStatusText.Text = "未能从该仓库获取发布信息，请检查仓库名是否正确。";
                return;
            }

            bool hasUpdate = DevSwitch.Core.GitHubReleaseParser.IsNewer(CurrentAppVersion, plan.Version);
            if (!hasUpdate)
            {
                UpdateStatusText.Text = $"已是最新版本（{CurrentAppVersion}）。";
                return;
            }

            if (string.IsNullOrWhiteSpace(plan.DownloadUrl))
            {
                // 有新版但没有可用 Windows 安装包：引导手动下载。
                UpdateStatusText.Text = $"发现新版本 {plan.Version}，但该发布未提供 Windows 安装包。";
                if (!string.IsNullOrWhiteSpace(plan.ReleaseUrl))
                {
                    bool open = await ShowConfirmDialogAsync("发现新版本", $"最新版本：{plan.Version}\n该发布无可用安装包，是否打开发布页手动下载？", "打开发布页", "稍后");
                    if (open)
                    {
                        OpenUrl(plan.ReleaseUrl!);
                    }
                }
                return;
            }

            // 发现可自动更新的新版本：暂存计划并显示「下载并更新」。
            pendingUpdatePlan = plan;
            UpdateStatusText.Text = $"发现新版本 {plan.Version}（当前 {CurrentAppVersion}），可点击「下载并更新」自动升级。";
            DownloadUpdateButton.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText.Text = "检查已取消。";
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"检查失败：{ex.Message}";
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 「下载并更新」按钮：下载新版 → 校验 → 解压 → 启动 updater 覆盖并重启。
    /// </summary>
    private async void OnDownloadUpdateClick(object sender, RoutedEventArgs e)
    {
        if (pendingUpdatePlan is null)
        {
            return;
        }

        bool confirmed = await ShowConfirmDialogAsync(
            "下载并更新",
            $"将下载新版本 {pendingUpdatePlan.Version} 并自动覆盖当前程序，完成后应用会自动重启。\n（你的 SDK、配置与日志数据不会被改动。）\n\n是否继续？",
            "开始更新",
            "取消");
        if (!confirmed)
        {
            return;
        }

        CheckUpdateButton.IsEnabled = false;
        DownloadUpdateButton.IsEnabled = false;
        UpdateProgressPanel.Visibility = Visibility.Visible;
        UpdateProgressBar.Value = 0;
        UpdateProgressText.Text = "准备下载……";

        // 进度上报回 UI 线程更新进度条与阶段文案。
        var progress = new Progress<(DevSwitch.Core.SelfUpdateStage Stage, double Percent)>(p =>
        {
            UpdateProgressBar.Value = p.Percent;
            UpdateProgressText.Text = p.Stage switch
            {
                DevSwitch.Core.SelfUpdateStage.Downloading => $"正在下载…… {p.Percent:P0}",
                DevSwitch.Core.SelfUpdateStage.Verifying => "正在校验完整性……",
                DevSwitch.Core.SelfUpdateStage.Extracting => "正在解压……",
                DevSwitch.Core.SelfUpdateStage.LaunchingUpdater => "正在启动更新器……",
                _ => UpdateProgressText.Text,
            };
        });

        try
        {
            var selfUpdate = appServices.CreateSelfUpdateService();
            var result = await selfUpdate.RunAsync(pendingUpdatePlan, progress, windowLifetime.Token);

            if (result.Success)
            {
                UpdateProgressText.Text = "更新器已启动，应用即将退出并完成覆盖更新……";
                // 让出文件锁：稍等片刻后退出主程序，由 updater 覆盖并重启。
                await Task.Delay(800, windowLifetime.Token);
                Microsoft.UI.Xaml.Application.Current.Exit();
            }
            else
            {
                UpdateStatusText.Text = $"更新失败：{result.Message}";
                UpdateProgressPanel.Visibility = Visibility.Collapsed;
                // 失败回退：若有发布页，提示手动下载。
                if (!string.IsNullOrWhiteSpace(pendingUpdatePlan.ReleaseUrl))
                {
                    bool open = await ShowConfirmDialogAsync("更新失败", $"{result.Message}\n\n是否打开发布页手动下载？", "打开发布页", "关闭");
                    if (open)
                    {
                        OpenUrl(pendingUpdatePlan.ReleaseUrl!);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            UpdateProgressPanel.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"更新失败：{ex.Message}";
            UpdateProgressPanel.Visibility = Visibility.Collapsed;
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
            DownloadUpdateButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// GitHub 仓库输入框失焦：校验并持久化 owner/repo。
    /// </summary>
    private async void OnUpdateRepositoryLostFocus(object sender, RoutedEventArgs e)
    {
        if (isSettingsInitializing)
        {
            return;
        }

        string repo = UpdateRepositoryTextBox.Text?.Trim() ?? string.Empty;
        // 空允许（清空仓库）；非空则校验形态，避免存入无效值。
        if (!string.IsNullOrWhiteSpace(repo) && !DevSwitch.Core.GitHubRepoResolver.IsValidRepository(repo))
        {
            UpdateStatusText.Visibility = Visibility.Visible;
            UpdateStatusText.Text = "仓库格式应为 owner/repo，例如 myname/devswitch。";
            return;
        }

        await SaveSettingsAsync(settings => settings with
        {
            Update = settings.Update with { Repository = string.IsNullOrWhiteSpace(repo) ? null : repo },
        });
    }

    /// <summary>
    /// 「反馈」按钮：用系统默认浏览器打开 GitHub issues。
    /// </summary>
    private void OnFeedbackClick(object sender, RoutedEventArgs e)
    {
        OpenUrl(FeedbackUrl);
    }

    // ===================== 公共工具 =====================

    /// <summary>
    /// 从触发事件的控件 DataContext 取出对应的 SDK 行。
    /// </summary>
    private static bool TryGetRow(object sender, out SdkVersionRow row)
    {
        if (sender is FrameworkElement element && element.DataContext is SdkVersionRow typedRow)
        {
            row = typedRow;
            return true;
        }

        if (sender is MenuFlyoutItem menuItem && menuItem.DataContext is SdkVersionRow menuRow)
        {
            row = menuRow;
            return true;
        }

        row = null!;
        return false;
    }

    /// <summary>
    /// 统一执行行操作：设繁忙、禁用触发控件、捕获 helper 缺失与异常，最终复原。
    /// </summary>
    private async Task RunRowOperationAsync(object sender, Func<Task> operation)
    {
        isRowOperationBusy = true;
        var control = sender as Control;
        if (control is not null)
        {
            control.IsEnabled = false;
        }

        try
        {
            await operation();
        }
        catch (HelperUnavailableException ex)
        {
            await ShowSimpleDialogAsync("无法执行操作", ex.Message);
        }
        catch (OperationCanceledException)
        {
            // 窗口关闭取消，静默。
        }
        catch (Exception ex)
        {
            await ShowSimpleDialogAsync("操作失败", ex.Message);
        }
        finally
        {
            isRowOperationBusy = false;
            if (control is not null)
            {
                control.IsEnabled = true;
            }
        }
    }

    /// <summary>
    /// 由 UI 行构造一个用于验证的 SdkRecord（验证服务只依赖 Type/Path 等字段）。
    /// </summary>
    private static SdkRecord BuildRecordFromRow(SdkVersionRow row)
    {
        return new SdkRecord(
            Id: row.Id,
            Type: row.Type,
            Name: row.Name,
            Version: row.Version,
            Distribution: "unknown",
            Architecture: SdkArchitecture.Unknown,
            Source: string.Equals(row.Source, "托管", StringComparison.Ordinal) ? SdkSourceKind.Managed : SdkSourceKind.External,
            Path: row.Path,
            Status: SdkRecordStatus.Unverified,
            CreatedAt: DateTimeOffset.UtcNow,
            LastVerifiedAt: null);
    }

    /// <summary>
    /// 持久化设置：读取当前设置，应用变换函数后保存。
    /// </summary>
    private async Task SaveSettingsAsync(Func<DevSwitchSettings, DevSwitchSettings> mutate)
    {
        try
        {
            var settings = await DevSwitchSettingsStore.LoadOrCreateAsync(dataRoot);
            var updated = mutate(settings);
            await DevSwitchSettingsStore.SaveAsync(dataRoot, updated);
        }
        catch (Exception ex)
        {
            await ShowSimpleDialogAsync("保存设置失败", ex.Message);
        }
    }

    /// <summary>
    /// 按 Tag 选中 ComboBox 选项；找不到则不改动。
    /// </summary>
    private static void SelectComboBoxByTag(ComboBox comboBox, string tag)
    {
        foreach (var obj in comboBox.Items)
        {
            if (obj is ComboBoxItem item && string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }
    }

    /// <summary>
    /// 同步语言状态提示文本（不触发持久化），使用当前语言文案。
    /// </summary>
    private void UpdateLanguageStatus()
    {
        if (LanguageStatusText is null || LanguageComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        var loc = DevSwitch.App.Localization.LocalizationManager.Instance;
        string languageName = item.Content?.ToString() ?? loc["language.zhCN"];
        LanguageStatusText.Text = $"{loc["settings.language.current"]}：{languageName}";
    }

    /// <summary>
    /// 格式化错误文案，附带 errorCode 便于排错。
    /// </summary>
    private static string FormatError(string? errorCode, string message)
    {
        return string.IsNullOrWhiteSpace(errorCode)
            ? message
            : $"{message}\n\n错误码：{errorCode}";
    }

    /// <summary>
    /// SDK 记录状态 → 中文文案。
    /// </summary>
    private static string MapStatusText(SdkRecordStatus status) => status switch
    {
        SdkRecordStatus.Active => "使用中",
        SdkRecordStatus.Usable => "可用",
        SdkRecordStatus.Unavailable => "不可用",
        SdkRecordStatus.Unverified => "未验证",
        _ => "未知",
    };

    /// <summary>
    /// 诊断严重度 → 中文文案。
    /// </summary>
    private static string MapSeverityText(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Pass => "通过",
        DiagnosticSeverity.Info => "信息",
        DiagnosticSeverity.Warning => "警告",
        DiagnosticSeverity.Error => "错误",
        DiagnosticSeverity.Fatal => "致命",
        _ => "未知",
    };

    /// <summary>
    /// 显示确认对话框，返回用户是否点击主操作。
    /// </summary>
    private async Task<bool> ShowConfirmDialogAsync(string title, string content, string primaryText, string closeText)
    {
        if (RootGrid.XamlRoot is null)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>
    /// 用系统默认程序打开 URL。
    /// </summary>
    private static void OpenUrl(string url)
    {
        try
        {
            // UseShellExecute=true 让系统用默认浏览器打开外部链接。
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // 打开失败不抛出，避免影响 UI。
        }
    }

    // ===================== 数据目录 =====================
    /// <summary>
    /// 启动时检测并校正环境位置漂移：
    /// 若用户移动了工具目录（便携模式），HKCU 里 DEVSWITCH_HOME 仍指向旧路径，
    /// 导致 JAVA_HOME/MAVEN_HOME/GOROOT（引用 %DEVSWITCH_HOME%）全部失效。
    /// 这里把 DEVSWITCH_HOME 重写为当前实际数据根并广播——其余变量与 current 链接自动跟随，无需重建。
    /// 仅在「之前已初始化过环境」且「确实漂移」时才动手；helper 不可用则静默跳过。
    /// </summary>
    private async Task CorrectEnvironmentDriftAsync()
    {
        try
        {
            // 读取 HKCU 当前 DEVSWITCH_HOME（用户级），与当前数据根比对。
            string? registered = Environment.GetEnvironmentVariable(
                EnvironmentLayout.DevSwitchHomeName, EnvironmentVariableTarget.User);

            var drift = EnvironmentDriftDetector.Detect(dataRoot, registered);
            if (!drift.IsInitialized || !drift.HasDrift)
            {
                // 从未初始化（首次切换时才写）或路径一致：无需校正。
                return;
            }

            if (!appServices.IsHelperAvailable)
            {
                // helper 缺失时无法写环境，跳过；用户后续切换/诊断会再处理。
                return;
            }

            // 重写 DEVSWITCH_HOME 为当前数据根绝对路径，并广播 WM_SETTINGCHANGE。
            var environmentService = appServices.CreateEnvironmentService();
            var variables = new[] { new EnvironmentVariable(EnvironmentLayout.DevSwitchHomeName, dataRoot) };
            var result = await environmentService.WriteVariablesAsync(variables, broadcast: true, windowLifetime.Token);

            if (result.Success)
            {
                await ShowSimpleDialogAsync(
                    "已校正环境位置",
                    $"检测到工具目录已移动，DEVSWITCH_HOME 已更新为：\n{dataRoot}\n\n" +
                    "JAVA_HOME / MAVEN_HOME / GOROOT 会自动跟随。已打开的终端需重启后生效。");
            }
        }
        catch (OperationCanceledException)
        {
            // 窗口关闭导致取消，正常忽略。
        }
        catch
        {
            // 校正失败不应影响应用使用，静默处理（用户可在环境诊断中查看）。
        }
    }

    /// <summary>
    /// 刷新数据目录显示文本与位置模式下拉框。
    /// </summary>
    private void UpdateDataRootText()
    {
        var config = DataRootBootstrap.ReadConfig(AppContext.BaseDirectory);
        string modeLabel = config.Mode switch
        {
            DataLocationMode.Portable => "便携（应用同目录）",
            DataLocationMode.LocalAppData => "固定到 C 盘",
            DataLocationMode.Custom => "自定义",
            _ => "便携",
        };
        DataRootPathText.Text = $"当前数据目录：{dataRoot}（{modeLabel}）";

        // 同步下拉框选中项，置 isSettingsInitializing 避免触发持久化。
        isSettingsInitializing = true;
        string tag = config.Mode switch
        {
            DataLocationMode.Portable => "portable",
            DataLocationMode.LocalAppData => "localappdata",
            DataLocationMode.Custom => "custom",
            _ => "portable",
        };
        SelectComboBoxByTag(DataModeComboBox, tag);
        isSettingsInitializing = false;
    }

    /// <summary>
    /// 位置模式选择变更：便携/固定C盘直接写引导配置；自定义引导用户去「自定义并迁移」。
    /// </summary>
    private async void OnDataModeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isSettingsInitializing || DataModeComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        string tag = item.Tag?.ToString() ?? "portable";
        try
        {
            if (tag == "custom")
            {
                // 自定义路径需要选目录，交给「自定义并迁移」按钮处理；这里仅提示。
                await ShowSimpleDialogAsync("自定义目录", "请点击「自定义并迁移」选择目录并可选迁移现有数据。");
                UpdateDataRootText();
                return;
            }

            var mode = tag == "localappdata" ? DataLocationMode.LocalAppData : DataLocationMode.Portable;

            // 计算切换后的目标数据根，询问是否迁移现有数据。
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string target = DataRootResolver.ResolveByMode(
                new DataLocationConfig(mode, null), AppContext.BaseDirectory, localAppData);

            if (string.Equals(
                System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(target)),
                System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(dataRoot)),
                StringComparison.OrdinalIgnoreCase))
            {
                // 目标与当前一致：直接写模式即可。
                DataRootBootstrap.WriteConfig(AppContext.BaseDirectory, new DataLocationConfig(mode, null));
                UpdateDataRootText();
                return;
            }

            // 三选一：迁移并切换 / 仅切换 / 取消。用户取消则不做任何改动直接退出。
            var choice = await ShowThreeChoiceDialogAsync(
                "切换数据位置模式",
                $"新数据目录：\n{target}\n\n是否把当前数据迁移到新位置？\n选择「仅切换」则从新位置重新开始（原数据保留）。",
                "迁移并切换",
                "仅切换",
                "取消");

            if (choice == ThreeChoiceResult.Cancel)
            {
                // 用户放弃：恢复下拉框到当前真实模式，不改配置、不迁移。
                UpdateDataRootText();
                return;
            }

            bool migrate = choice == ThreeChoiceResult.Primary;

            if (migrate)
            {
                // 迁移可能耗时较久，显示忙碌动画覆盖层，避免用户误判卡死。
                ShowBusyOverlay("正在迁移数据", $"正在把数据迁移到：\n{target}\n请稍候，期间请勿关闭应用。");
                try
                {
                    var migration = new DataRootMigrationService();
                    var result = await migration.MigrateAsync(dataRoot, target, overwrite: true, windowLifetime.Token);
                    if (!result.Success)
                    {
                        HideBusyOverlay();
                        await ShowSimpleDialogAsync("迁移失败", FormatError(result.ErrorCode, result.Message));
                        UpdateDataRootText();
                        return;
                    }
                }
                finally
                {
                    HideBusyOverlay();
                }
            }

            DataRootBootstrap.WriteConfig(AppContext.BaseDirectory, new DataLocationConfig(mode, null));
            UpdateDataRootText();
            await ShowSimpleDialogAsync(
                "已切换数据位置",
                $"数据位置已设为：\n{target}\n\n请重启 DevSwitch 使新位置生效。" +
                (migrate ? "\n（已迁移现有数据，原位置数据保留未删除。）" : string.Empty));
        }
        catch (Exception ex)
        {
            await ShowSimpleDialogAsync("切换失败", ex.Message);
            UpdateDataRootText();
        }
    }

    /// <summary>
    /// 「打开目录」：用资源管理器打开当前数据目录。
    /// </summary>
    private void OnOpenDataRootClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(dataRoot);
            Process.Start(new ProcessStartInfo(dataRoot) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _ = ShowSimpleDialogAsync("打开失败", ex.Message);
        }
    }

    /// <summary>
    /// 「自定义并迁移」：选新目录 → 询问是否迁移 → 写 Custom 模式引导 → 提示重启。
    /// </summary>
    private async void OnChangeDataRootClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null || string.IsNullOrWhiteSpace(folder.Path))
            {
                return;
            }

            // 目标目录内单独建 DevSwitch 子目录，避免与用户其它文件混放。
            string target = System.IO.Path.Combine(folder.Path, "DevSwitch");

            if (string.Equals(
                System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(target)),
                System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(dataRoot)),
                StringComparison.OrdinalIgnoreCase))
            {
                await ShowSimpleDialogAsync("无需更改", "所选目录与当前数据目录相同。");
                return;
            }

            // 三选一：迁移并切换 / 仅切换 / 取消。用户取消则不做任何改动直接退出。
            var choice = await ShowThreeChoiceDialogAsync(
                "自定义数据目录",
                $"新数据目录：\n{target}\n\n是否把当前数据迁移到新目录？\n选择「仅切换」则从新目录重新开始。",
                "迁移并切换",
                "仅切换",
                "取消");

            if (choice == ThreeChoiceResult.Cancel)
            {
                // 用户放弃：不写配置、不迁移、不建目录，直接退出。
                return;
            }

            bool migrate = choice == ThreeChoiceResult.Primary;

            if (migrate)
            {
                // 迁移可能耗时较久，显示忙碌动画覆盖层，避免用户误判卡死。
                ShowBusyOverlay("正在迁移数据", $"正在把数据迁移到：\n{target}\n请稍候，期间请勿关闭应用。");
                try
                {
                    var migration = new DataRootMigrationService();
                    var result = await migration.MigrateAsync(dataRoot, target, overwrite: true, windowLifetime.Token);
                    if (!result.Success)
                    {
                        HideBusyOverlay();
                        await ShowSimpleDialogAsync("迁移失败", FormatError(result.ErrorCode, result.Message));
                        return;
                    }
                }
                finally
                {
                    HideBusyOverlay();
                }
            }
            else
            {
                System.IO.Directory.CreateDirectory(target);
            }

            DataRootBootstrap.WriteConfig(AppContext.BaseDirectory, new DataLocationConfig(DataLocationMode.Custom, target));
            UpdateDataRootText();

            await ShowSimpleDialogAsync(
                "已设置数据目录",
                $"数据目录已设置为：\n{target}\n\n请重启 DevSwitch 使新目录生效。" +
                (migrate ? "\n（已迁移现有数据，原目录数据保留未删除。）" : string.Empty));
        }
        catch (Exception ex)
        {
            await ShowSimpleDialogAsync("更改失败", ex.Message);
        }
    }
}
