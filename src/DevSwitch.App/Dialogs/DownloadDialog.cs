// 文件用途：SDK 下载对话框。列出真实版本（DevSwitch.Sources）、选择架构与版本，
//           使用 DevSwitch.Downloader 的 DownloadEngine 下载并显示进度，完成后走校验/解压/登记流程。
// 创建/修改日期：2026-06-10
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：DevSwitch.Core、DevSwitch.Sources、DevSwitch.Downloader、Microsoft.UI.Xaml
// NOTE: 合法授权学习使用，仅限本地环境。
//       - UI 在代码中构建，避免高风险自定义 ControlTemplate 触发 XAML parse 崩溃。
//       - 全异步：列出版本与下载均不阻塞 UI 线程；进度通过 IProgress 节流回到 UI 线程。
//       - 仅在用户点击「列出版本」「下载」时联网。

using DevSwitch.App.Services;
using DevSwitch.Core;
using DevSwitch.Downloader;
using DevSwitch.Sources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace DevSwitch.App.Dialogs;

/// <summary>
/// SDK 下载对话框：选择 SDK 类型/架构 → 列出版本 → 下载 → 校验解压登记。
/// </summary>
public sealed class DownloadDialog : ContentDialog
{
    private readonly AppServices appServices;

    // 父窗口句柄，用于 FolderPicker 初始化（自定义安装目录）。
    private readonly nint ownerWindowHandle;

    // UI 控件引用（代码构建，便于状态切换）。
    private readonly ComboBox sdkTypeCombo;
    private readonly ComboBox archCombo;
    private readonly ComboBox versionCombo;
    private readonly Button listButton;
    private readonly Button chooseDirButton;
    private readonly TextBlock installDirText;
    private readonly ProgressRing busyRing;
    private readonly TextBlock statusText;
    private readonly ProgressBar downloadProgress;

    // 当前安装根目录（托管 SDK 解压父目录）。默认 dataRoot\sdks，可由用户自定义。
    private string installRootDir;

    // 当前列出的版本列表，索引与 versionCombo 对齐。
    private IReadOnlyList<SdkSourceVersion> currentVersions = Array.Empty<SdkSourceVersion>();

    // 取消下载用令牌源。
    private CancellationTokenSource? downloadCts;

    private bool isBusy;

    /// <summary>
    /// 本次对话框是否成功登记了新的托管 SDK（供窗口层决定是否刷新列表）。
    /// </summary>
    public bool DidRegisterSdk { get; private set; }

    /// <summary>
    /// 创建下载对话框。
    /// </summary>
    /// <param name="appServices">App 组合根，提供版本目录、下载引擎与完成流程。</param>
    /// <param name="initialCategory">初始 SDK 分类（来自当前页面）。</param>
    /// <param name="ownerWindowHandle">父窗口句柄，用于自定义安装目录的 FolderPicker。</param>
    public DownloadDialog(AppServices appServices, string initialCategory, nint ownerWindowHandle)
    {
        this.appServices = appServices ?? throw new ArgumentNullException(nameof(appServices));
        this.ownerWindowHandle = ownerWindowHandle;

        // 默认安装根：数据根目录下的 sdks（设计约定的托管 SDK 根）。
        installRootDir = Path.Combine(appServices.DataRoot, "sdks");

        Title = "下载 SDK";
        PrimaryButtonText = "下载";
        CloseButtonText = "关闭";
        DefaultButton = ContentDialogButton.Primary;
        IsPrimaryButtonEnabled = false;

        // ===== 构建对话框内容 =====
        sdkTypeCombo = new ComboBox { MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Stretch };
        sdkTypeCombo.Items.Add(new ComboBoxItem { Content = "Java", Tag = "Java" });
        sdkTypeCombo.Items.Add(new ComboBoxItem { Content = "Maven", Tag = "Maven" });
        sdkTypeCombo.Items.Add(new ComboBoxItem { Content = "Node.js", Tag = "Node.js" });
        sdkTypeCombo.Items.Add(new ComboBoxItem { Content = "Go", Tag = "Go" });
        SelectByTag(sdkTypeCombo, initialCategory, fallbackIndex: 0);

        archCombo = new ComboBox { MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Stretch };
        archCombo.Items.Add(new ComboBoxItem { Content = "x64", Tag = "x64" });
        archCombo.Items.Add(new ComboBoxItem { Content = "arm64", Tag = "arm64" });
        archCombo.SelectedIndex = 0;

        versionCombo = new ComboBox
        {
            MinWidth = 320,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = "请先列出版本",
            IsEnabled = false,
        };
        versionCombo.SelectionChanged += (_, _) => UpdatePrimaryButtonState();

        listButton = new Button { Content = "列出版本" };
        listButton.Click += OnListVersionsClick;

        busyRing = new ProgressRing { Width = 18, Height = 18, IsActive = false, Visibility = Visibility.Collapsed };

        // 安装目录显示 + 自定义按钮：让用户知道默认下载位置并可更改。
        installDirText = new TextBlock
        {
            Text = FormatInstallDirLabel(),
            TextWrapping = TextWrapping.Wrap,
        };
        chooseDirButton = new Button { Content = "更改目录" };
        chooseDirButton.Click += OnChooseInstallDirClick;

        statusText = new TextBlock
        {
            Text = "选择 SDK 类型与架构后点击「列出版本」。",
            TextWrapping = TextWrapping.Wrap,
        };

        downloadProgress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 1,
            Value = 0,
            Visibility = Visibility.Collapsed,
        };

        var panel = new StackPanel { Spacing = 12, MinWidth = 420 };
        panel.Children.Add(BuildLabeledRow("SDK 类型", sdkTypeCombo));
        panel.Children.Add(BuildLabeledRow("架构", archCombo));

        var listRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        listRow.Children.Add(listButton);
        listRow.Children.Add(busyRing);
        panel.Children.Add(listRow);

        panel.Children.Add(BuildLabeledRow("版本", versionCombo));

        // 安装目录区：标签 + 路径文本 + 更改按钮。
        var dirHeader = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        dirHeader.Children.Add(new TextBlock { Text = "安装目录", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        dirHeader.Children.Add(chooseDirButton);
        var dirBlock = new StackPanel { Spacing = 4 };
        dirBlock.Children.Add(dirHeader);
        dirBlock.Children.Add(installDirText);
        panel.Children.Add(dirBlock);

        panel.Children.Add(downloadProgress);
        panel.Children.Add(statusText);

        Content = panel;

        // 拦截主按钮点击：自行执行异步下载，不关闭对话框直到完成或失败。
        PrimaryButtonClick += OnDownloadClick;
        Closing += OnDialogClosing;
    }


    /// <summary>
    /// 「列出版本」：通过真实多源目录抓取并填充版本下拉框。
    /// </summary>
    private async void OnListVersionsClick(object sender, RoutedEventArgs e)
    {
        if (isBusy)
        {
            return;
        }

        SetBusy(true);
        statusText.Text = "正在获取版本列表……";
        versionCombo.Items.Clear();
        versionCombo.IsEnabled = false;
        currentVersions = Array.Empty<SdkSourceVersion>();
        UpdatePrimaryButtonState();

        try
        {
            var sdkType = ParseSdkType(GetTag(sdkTypeCombo));
            var arch = ParseArch(GetTag(archCombo));

            var catalog = appServices.CreateSourceCatalog();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            var versions = await catalog.ListVersionsAsync(sdkType, arch, cts.Token);

            // 部分源（如 Maven）架构为 Any；若按架构过滤为空则回退展示全部。
            currentVersions = versions;
            if (currentVersions.Count == 0)
            {
                statusText.Text = "未获取到可用版本，请稍后重试或更换架构。";
                return;
            }

            foreach (var version in currentVersions)
            {
                string label = version.DisplayName
                    ?? $"{version.Distribution} {version.Version} ({version.Architecture})";
                versionCombo.Items.Add(new ComboBoxItem { Content = label });
            }

            versionCombo.IsEnabled = true;
            versionCombo.SelectedIndex = 0;
            statusText.Text = $"已列出 {currentVersions.Count} 个版本，请选择后点击「下载」。";
        }
        catch (OperationCanceledException)
        {
            statusText.Text = "获取版本超时，请检查网络后重试。";
        }
        catch (Exception ex)
        {
            statusText.Text = $"获取版本失败：{ex.Message}";
        }
        finally
        {
            SetBusy(false);
            UpdatePrimaryButtonState();
        }
    }

    /// <summary>
    /// 「下载」：使用 DownloadEngine 下载选中版本并显示进度，完成后走校验/解压/登记。
    /// </summary>
    private async void OnDownloadClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // 阻止对话框默认关闭：下载过程中保持打开。
        var deferral = args.GetDeferral();
        args.Cancel = true;

        if (isBusy || versionCombo.SelectedIndex < 0 || versionCombo.SelectedIndex >= currentVersions.Count)
        {
            deferral.Complete();
            return;
        }

        var selected = currentVersions[versionCombo.SelectedIndex];

        SetBusy(true);
        downloadProgress.Visibility = Visibility.Visible;
        downloadProgress.Value = 0;
        downloadProgress.IsIndeterminate = false;
        statusText.Text = "准备下载……";

        downloadCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);

        try
        {
            // 读取并发数设置。
            var settings = await DevSwitchSettingsStore.LoadOrCreateAsync(appServices.DataRoot);
            int parallelism = Math.Clamp(settings.Download.Parallelism, 1, 8);

            // 目标路径：dataRoot\downloads\<archive>，托管根：dataRoot\sdks\<dist>-<version>-<arch>。
            string downloadsDir = Path.Combine(appServices.DataRoot, "downloads");
            Directory.CreateDirectory(downloadsDir);

            string archiveName = BuildArchiveName(selected);
            string archivePath = Path.Combine(downloadsDir, archiveName);

            string installDir = Path.Combine(
                installRootDir,
                SanitizeFolder($"{selected.Distribution}-{selected.Version}-{selected.Architecture}"));

            // 构建下载任务。
            var task = DownloadTask.CreateQueued(
                id: Guid.NewGuid().ToString("N"),
                sdkType: selected.SdkType,
                version: selected.Version,
                distribution: selected.Distribution,
                arch: selected.Architecture,
                url: selected.DownloadUrl,
                expectedSha256: selected.Sha256);

            // 进度回调：节流后回到 UI 线程更新进度条。
            var progress = new Progress<DownloadProgress>(p =>
            {
                if (p.Fraction is { } fraction)
                {
                    downloadProgress.IsIndeterminate = false;
                    downloadProgress.Value = fraction;
                    statusText.Text = $"下载中…… {fraction * 100:F0}%";
                }
                else
                {
                    // 总长度未知：用不确定进度条。
                    downloadProgress.IsIndeterminate = true;
                    statusText.Text = "下载中（未知大小）……";
                }
            });

            var engine = appServices.CreateDownloadEngine(parallelism);
            var downloaded = await engine.DownloadAsync(task, archivePath, progress, downloadCts.Token);

            if (downloaded.Status == DownloadStatus.Failed)
            {
                statusText.Text = "下载失败，请检查网络或更换版本后重试。";
                return;
            }

            if (downloaded.Status == DownloadStatus.Paused)
            {
                statusText.Text = "下载已取消。";
                return;
            }

            // 校验 → 解压 → 登记。
            statusText.Text = "正在校验并解压……";
            downloadProgress.IsIndeterminate = true;

            var pipeline = appServices.CreateCompletionPipeline();
            var completion = await pipeline.CompleteAsync(downloaded, archivePath, installDir, downloadCts.Token);

            if (completion.Task.Status == DownloadStatus.Completed)
            {
                DidRegisterSdk = true;

                // 完成后按设置决定是否保留安装包。
                if (!settings.Download.KeepArchives)
                {
                    TryDeleteFile(archivePath);
                }

                statusText.Text = "下载并登记完成。可关闭对话框查看 SDK 列表。";
                IsPrimaryButtonEnabled = false;
            }
            else
            {
                statusText.Text = $"完成流程失败：{completion.FailureReason ?? "未知原因"}";
            }
        }
        catch (OperationCanceledException)
        {
            statusText.Text = "下载已取消。";
        }
        catch (Exception ex)
        {
            statusText.Text = $"下载失败：{ex.Message}";
        }
        finally
        {
            SetBusy(false);
            downloadProgress.IsIndeterminate = false;
            deferral.Complete();
        }
    }

    /// <summary>
    /// 关闭对话框时取消进行中的下载。
    /// </summary>
    private void OnDialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        downloadCts?.Cancel();
    }

    /// <summary>
    /// 「更改目录」：用 FolderPicker 让用户自定义托管 SDK 安装根目录。
    /// </summary>
    private async void OnChooseInstallDirClick(object sender, RoutedEventArgs e)
    {
        if (isBusy)
        {
            return;
        }

        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            // Desktop WinUI 的 picker 必须绑定窗口句柄才能正常弹出。
            InitializeWithWindow.Initialize(picker, ownerWindowHandle);

            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null && !string.IsNullOrWhiteSpace(folder.Path))
            {
                installRootDir = folder.Path;
                installDirText.Text = FormatInstallDirLabel();
            }
        }
        catch (Exception ex)
        {
            statusText.Text = $"选择目录失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 安装目录标签文本：明确告诉用户下载的 SDK 会解压登记到哪里。
    /// </summary>
    private string FormatInstallDirLabel()
    {
        bool isDefault = string.Equals(
            Path.TrimEndingDirectorySeparator(installRootDir),
            Path.TrimEndingDirectorySeparator(Path.Combine(appServices.DataRoot, "sdks")),
            StringComparison.OrdinalIgnoreCase);

        string suffix = isDefault ? "（默认位置）" : "（自定义）";
        return $"{installRootDir}{suffix}\n下载的 SDK 会解压到此目录并登记到列表。";
    }

    // ===================== 工具 =====================

    /// <summary>
    /// 设置繁忙态：禁用交互控件并显示/隐藏进度环。
    /// </summary>
    private void SetBusy(bool busy)
    {
        isBusy = busy;
        busyRing.IsActive = busy;
        busyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        listButton.IsEnabled = !busy;
        sdkTypeCombo.IsEnabled = !busy;
        archCombo.IsEnabled = !busy;
    }

    /// <summary>
    /// 根据是否选中版本更新主按钮可用性。
    /// </summary>
    private void UpdatePrimaryButtonState()
    {
        IsPrimaryButtonEnabled = !isBusy
            && versionCombo.SelectedIndex >= 0
            && versionCombo.SelectedIndex < currentVersions.Count
            && !DidRegisterSdk;
    }

    /// <summary>
    /// 构建「标签 + 控件」一行。
    /// </summary>
    private static StackPanel BuildLabeledRow(string label, FrameworkElement control)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock { Text = label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        stack.Children.Add(control);
        return stack;
    }

    private static string GetTag(ComboBox combo)
    {
        return (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
    }

    private static void SelectByTag(ComboBox combo, string tag, int fallbackIndex)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }

        combo.SelectedIndex = fallbackIndex;
    }

    private static SdkType ParseSdkType(string category) => category switch
    {
        "Java" => SdkType.Java,
        "Maven" => SdkType.Maven,
        "Node.js" => SdkType.Node,
        "Go" => SdkType.Go,
        _ => SdkType.Java,
    };

    private static SdkArchitecture ParseArch(string tag) => tag switch
    {
        "x64" => SdkArchitecture.X64,
        "arm64" => SdkArchitecture.Arm64,
        _ => SdkArchitecture.X64,
    };

    /// <summary>
    /// 根据下载 URL 推断安装包文件名，缺失时回退到合成名。
    /// </summary>
    private static string BuildArchiveName(SdkSourceVersion version)
    {
        try
        {
            var uri = new Uri(version.DownloadUrl);
            string name = Path.GetFileName(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return SanitizeFolder(name);
            }
        }
        catch
        {
            // URL 解析失败时回退合成名。
        }

        return SanitizeFolder($"{version.Distribution}-{version.Version}-{version.Architecture}.zip");
    }

    /// <summary>
    /// 去除文件/目录名中的非法字符。
    /// </summary>
    private static string SanitizeFolder(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '-');
        }

        return name;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理失败不影响主流程。
        }
    }
}
