using System;
using System.IO;
using System.Text;
using DevSwitch.App.Services;
using DevSwitch.App.ViewModels;
using DevSwitch.Core;
using Microsoft.UI.Xaml;

namespace DevSwitch.App;

/// <summary>
/// DevSwitch WinUI 3 应用入口。
/// 当前仅用于手工测试空壳启动，不加载假数据、不初始化业务服务。
/// </summary>
public partial class App : Application
{
    private Window? _window;

    /// <summary>
    /// 初始化 XAML 组件，保持最小启动链路。
    /// 启动阶段额外写本地日志，方便定位双击 exe 无窗口时的 WinUI/XAML 异常。
    /// </summary>
    public App()
    {
        StartupLog.Write("App constructor entered.");

        // NOTE: WinUI 3 XAML 解析异常经常表现为 0xc000027b 崩溃，先注册异常钩子便于落盘排查。
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            StartupLog.Write("AppDomain unhandled exception.", args.ExceptionObject as Exception);
        };

        try
        {
            InitializeComponent();
            StartupLog.Write("App.InitializeComponent completed.");
        }
        catch (Exception ex)
        {
            StartupLog.Write("App.InitializeComponent failed.", ex);
            throw;
        }

        UnhandledException += (_, args) =>
        {
            StartupLog.Write("WinUI unhandled exception.", args.Exception);
        };
    }

    /// <summary>
    /// 应用启动时创建并激活主窗口。
    /// 这里作为组合根创建真实 sdks.json provider，再注入主窗口 ViewModel。
    /// </summary>
    /// <param name="args">Windows App SDK 启动参数。</param>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupLog.Write("OnLaunched entered.");

        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            // 数据根优先级：dataroot.txt 自定义路径 → 应用同目录 data\（默认便携，不进 C 盘）→ 不可写时回退 LocalAppData。
            string dataRoot = DataRootResolver.ResolveEffective(AppContext.BaseDirectory, localAppData);

            // ResolveEffective 只返回路径不创建目录；这里确保数据根存在，便于首次启动直接可用。
            try
            {
                Directory.CreateDirectory(dataRoot);
            }
            catch (Exception createEx)
            {
                // 极端情况下（如自定义目录已不可写）回退到 LocalAppData，保证应用仍能启动。
                StartupLog.Write($"Create data root failed at '{dataRoot}', falling back to LocalAppData.", createEx);
                dataRoot = Path.Combine(localAppData, "DevSwitch");
                Directory.CreateDirectory(dataRoot);
            }

            StartupLog.Write($"Resolved data root: {dataRoot}");

            // 启动即清理：自更新留下的 data\updates\<version>\ 暂存仅在「下载→覆盖→重启」期间需要，
            // 主程序此刻已重启，意味着 updater 必然已退出，所有历史版本的更新包都可安全释放，
            // 避免磁盘占用随版本累积。清理失败不阻塞启动（DataRootMaintenance 内部已吞异常）。
            try
            {
                DataRootMaintenance.PurgeUpdatesStagingDirectory(dataRoot);
                StartupLog.Write("Updates staging directory purged.");
            }
            catch (Exception purgeEx)
            {
                // 兜底：理论上 PurgeUpdatesStagingDirectory 已吞所有 IO 异常；这里再加一层保险。
                StartupLog.Write("Purge updates staging failed (ignored).", purgeEx);
            }

            // 启动即应用已保存的强调色：在构造 MainWindow 之前完成，确保首帧即为目标配色。
            // 同步读取 settings.json 仅取 AccentColor 一个标量；任何失败都回退默认色，绝不阻塞启动。
            try
            {
                var settings = DevSwitchSettingsStore.LoadOrCreateAsync(dataRoot)
                    .GetAwaiter().GetResult();
                AccentThemeService.Apply(settings.AccentColor);
                StartupLog.Write($"Accent color applied: {settings.AccentColor}.");
            }
            catch (Exception accentEx)
            {
                // 读取/解析失败时回退默认强调色，保证窗口仍以一致配色启动。
                StartupLog.Write("Apply accent color failed, falling back to default.", accentEx);
                AccentThemeService.Apply(AccentPalette.DefaultKey);
            }

            // 组合根：集中创建后端服务工厂与共享依赖（catalog store、HttpClient、helper 路径解析）。
            var appServices = new AppServices(dataRoot);
            var catalogProvider = new FileSdkCatalogProvider(dataRoot, appServices.CatalogStore);
            var viewModel = new MainWindowViewModel(catalogProvider);

            _window = new MainWindow(viewModel, dataRoot, appServices);
            StartupLog.Write("MainWindow constructed with real SDK catalog provider.");

            _window.Activate();
            StartupLog.Write("MainWindow activated.");
        }
        catch (Exception ex)
        {
            StartupLog.Write("OnLaunched failed.", ex);
            throw;
        }
    }

    /// <summary>
    /// 启动日志工具。
    /// 仅写入当前用户 LocalAppData，不修改注册表或系统环境，便于定位本地 WinUI 启动异常。
    /// </summary>
    private static class StartupLog
    {
        private static readonly object SyncRoot = new();

        /// <summary>
        /// 写入启动诊断日志。
        /// </summary>
        /// <param name="message">诊断消息。</param>
        /// <param name="exception">可选异常对象。</param>
        public static void Write(string message, Exception? exception = null)
        {
            try
            {
                lock (SyncRoot)
                {
                    string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    string logDirectory = Path.Combine(localAppData, "DevSwitch", "logs");
                    Directory.CreateDirectory(logDirectory);

                    string logPath = Path.Combine(logDirectory, "startup.log");
                    var builder = new StringBuilder();
                    builder.Append('[')
                        .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                        .Append("] ")
                        .AppendLine(message);

                    if (exception is not null)
                    {
                        builder.AppendLine(exception.ToString());
                    }

                    File.AppendAllText(logPath, builder.ToString(), Encoding.UTF8);
                }
            }
            catch
            {
                // NOTE: 日志本身不能影响 GUI 启动；任何 IO 权限问题都直接吞掉。
            }
        }
    }
}
