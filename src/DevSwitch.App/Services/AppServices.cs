// 文件用途：App 层组合根与真实后端抽象实现集合。集中创建 DevSwitch.Core/Sources/Downloader
//           暴露的领域服务，并提供 GUI 接线所需的真实抽象实现（进程命令执行、helper 探测、
//           HKCU 环境读取、HTTP 抓取、托管 SDK 登记等）。
// 创建/修改日期：2026-06-10
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：DevSwitch.Core、DevSwitch.Sources、DevSwitch.Downloader、System.Net.Http、System.Diagnostics
// NOTE: 合法授权学习使用，仅限本地环境。
//       - 所有耗时操作均为异步，UI 线程不阻塞。
//       - helper.exe 路径解析失败时由调用方给出友好对话框，本层不崩溃。
//       - 仅在用户显式触发「检查更新」/「下载」时才联网，构造时绝不发起网络请求。

using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DevSwitch.Core;
using DevSwitch.Downloader;
using DevSwitch.Sources;

namespace DevSwitch.App.Services;

/// <summary>
/// App 组合根：集中持有数据根、helper 路径与共享 HttpClient，并按需创建各领域服务。
/// </summary>
/// <remarks>
/// 设计原则：
/// - 服务在此集中创建并注入，避免散落 new 在窗口事件里。
/// - 需要 helper 的服务先经 <see cref="IsHelperAvailable"/> 判定，缺失时窗口层给出对话框。
/// - HttpClient 复用单例，避免 socket 耗尽；仅在用户操作时实际发起请求。
/// </remarks>
public sealed class AppServices : IDisposable
{
    // helper 可执行文件名常量，避免散落字符串。
    private const string HelperExecutableName = "DevSwitch.Helper.exe";

    // shim 转发器文件名常量。rebuildShims 时复制成 shims\<cmd>.exe。
    private const string ShimExecutableName = "DevSwitch.Shim.exe";

    private readonly SdkCatalogStore catalogStore = new();
    private readonly ISdkCurrentPathProvider currentPathProvider = new SdkCurrentPathProvider();

    // 共享 HttpClient：设置较长超时供大文件下载，统一附带 User-Agent（GitHub API 强制要求）。
    private readonly HttpClient httpClient;

    /// <summary>
    /// 创建 App 组合根。
    /// </summary>
    /// <param name="dataRoot">DevSwitch 数据根目录。</param>
    public AppServices(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("Data root is required.", nameof(dataRoot));
        }

        DataRoot = dataRoot;
        HelperPath = ResolveHelperPath();
        ShimPath = ResolveExecutablePath(ShimExecutableName);

        httpClient = new HttpClient
        {
            // NOTE: 下载大体积 SDK 包可能耗时，给一个宽松超时；列表/更新请求远小于该值。
            Timeout = TimeSpan.FromMinutes(30),
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DevSwitch/0.1 (+https://github.com/devswitch/devswitch)");
    }

    /// <summary>
    /// DevSwitch 数据根目录。
    /// </summary>
    public string DataRoot { get; }

    /// <summary>
    /// 解析出的 helper.exe 路径；找不到时为 null。
    /// </summary>
    public string? HelperPath { get; }

    /// <summary>
    /// 解析出的 DevSwitch.Shim.exe 路径（转发器源）；找不到时为 null。
    /// </summary>
    public string? ShimPath { get; }

    /// <summary>
    /// helper 是否可用（路径已解析且文件存在）。
    /// </summary>
    public bool IsHelperAvailable => !string.IsNullOrWhiteSpace(HelperPath) && File.Exists(HelperPath);

    /// <summary>
    /// 共享只读 catalog store，供窗口层重命名等轻量落盘复用。
    /// </summary>
    public SdkCatalogStore CatalogStore => catalogStore;

    // ===================== 领域服务工厂 =====================

    /// <summary>
    /// 创建 SDK 切换服务（需要 helper）。
    /// </summary>
    public SdkSwitchService CreateSwitchService()
    {
        var helperClient = new ProcessHelperClient(RequireHelperPath());
        return new SdkSwitchService(DataRoot, catalogStore, helperClient, currentPathProvider);
    }

    /// <summary>
    /// 创建 SDK 验证服务（轻量验证需要 helper 探测 current 链接；命令验证用进程跑版本命令）。
    /// </summary>
    public SdkVerificationService CreateVerificationService()
    {
        var helperClient = new ProcessHelperClient(RequireHelperPath());
        var linkInspector = new HelperSdkLinkInspector(helperClient, currentPathProvider, DataRoot);
        // 为 mvn 命令验证注入有效 JAVA_HOME：优先 active Java 记录路径，否则任一可用 Java。
        var commandRunner = new ProcessSdkCommandRunner(ResolveActiveJavaHome);
        return new SdkVerificationService(linkInspector, commandRunner);
    }

    /// <summary>
    /// 解析一个有效的 JAVA_HOME 供 Maven 命令验证使用：优先 active Java 记录，回退首个可用 Java 记录。
    /// </summary>
    private string? ResolveActiveJavaHome()
    {
        try
        {
            var catalog = catalogStore.LoadOrCreateAsync(DataRoot).GetAwaiter().GetResult();
            var javaRecords = catalog.Items.Where(r => r.Type == SdkType.Java).ToArray();
            var active = javaRecords.FirstOrDefault(r => string.Equals(r.Id, catalog.Active.Java, StringComparison.OrdinalIgnoreCase));
            var chosen = active ?? javaRecords.FirstOrDefault(r => Directory.Exists(r.Path));
            return chosen?.Path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 创建 SDK 删除服务（真实删除器仅删 dataRoot\sdks 内托管实体）。
    /// </summary>
    public SdkDeletionService CreateDeletionService()
    {
        var remover = new SafeManagedFileRemover(DataRoot);
        return new SdkDeletionService(catalogStore, remover);
    }

    /// <summary>
    /// 创建环境重置服务（需要 helper）。
    /// </summary>
    public EnvironmentResetService CreateResetService()
    {
        string helperPath = RequireHelperPath();
        var envHelperClient = new ProcessEnvironmentHelperClient(helperPath);
        var environmentService = new EnvironmentService(envHelperClient);
        var linkClient = new ProcessSdkDeletionLinkClient(helperPath);
        return new EnvironmentResetService(environmentService, envHelperClient, linkClient, catalogStore, currentPathProvider);
    }

    /// <summary>
    /// 创建用户环境写入服务（需要 helper）。
    /// 切换 SDK 成功后用它确保 JAVA_HOME/MAVEN_HOME/GOROOT 与托管 PATH 片段已写入并广播，
    /// 这样新打开的终端才能解析到切换后的版本（变量固定指向 current 入口，幂等可重复调用）。
    /// </summary>
    public EnvironmentService CreateEnvironmentService()
    {
        var envHelperClient = new ProcessEnvironmentHelperClient(RequireHelperPath());
        return new EnvironmentService(envHelperClient);
    }

    /// <summary>
    /// 创建 Doctor 诊断服务（需要 helper 用于 ping 与 current 链接探测）。
    /// </summary>
    public DoctorService CreateDoctorService()
    {
        string helperPath = RequireHelperPath();
        var helperClient = new ProcessHelperClient(helperPath);
        var environmentReader = new HkcuEnvironmentReader();
        var linkInspector = new HelperCurrentLinkInspector(helperClient);
        var commandRunner = new ProcessDoctorCommandRunner();
        var helperPing = new HelperPingClient(helperPath);
        return new DoctorService(DataRoot, catalogStore, environmentReader, linkInspector, commandRunner, helperPing, currentPathProvider);
    }

    /// <summary>
    /// 创建更新检查服务（仅在调用 CheckAsync 时联网）。
    /// </summary>
    public UpdateCheckService CreateUpdateService()
    {
        var feedClient = new HttpReleaseFeedClient(httpClient);
        return new UpdateCheckService(feedClient);
    }

    /// <summary>
    /// 创建多源聚合版本目录（真实 HTTP 抓取，仅在列出版本时联网）。
    /// </summary>
    public ISdkSourceCatalog CreateSourceCatalog()
    {
        SourceTextFetcher fetcher = FetchTextAsync;

        // Java：覆盖 Adoptium 官方主流大版本（LTS 8/11/17/21/25 + 最新特性版 26）。
        // 每个大版本各注册一个源，聚合目录会并发抓取并按版本去重合并；pageSize 控制每个大版本的小版本数量。
        // 这样下拉框不再只有 21，而是从 8 到最新一应俱全，且全部来自官方 api.adoptium.net。
        int[] javaFeatureVersions = { 26, 25, 21, 17, 11, 8 };
        var sourceList = new List<ISdkVersionSource>();
        foreach (int feature in javaFeatureVersions)
        {
            // LTS（8/11/17/21/25）多给几个小版本；非 LTS 特性版只给最新少量，避免列表过长。
            bool isLts = feature is 8 or 11 or 17 or 21 or 25;
            sourceList.Add(HttpSdkVersionSource.CreateTemurinFeature(fetcher, feature, pageSize: isLts ? 5 : 2));
        }

        sourceList.Add(HttpSdkVersionSource.CreateMaven(fetcher));
        sourceList.Add(HttpSdkVersionSource.CreateNode(fetcher));
        sourceList.Add(HttpSdkVersionSource.CreateGo(fetcher));
        return new SdkSourceCatalog(sourceList);
    }

    /// <summary>
    /// 创建下载引擎（复用共享 HttpClient，支持多线程断点续传）。
    /// </summary>
    /// <param name="parallelism">下载并发分块数（1-8，引擎内部会再次收敛）。</param>
    public DownloadEngine CreateDownloadEngine(int parallelism)
    {
        var options = new DownloadEngineOptions { Parallelism = parallelism };
        return new DownloadEngine(httpClient, options);
    }

    /// <summary>
    /// 创建下载完成流程编排器（校验 → 解压 → 登记到 sdks.json）。
    /// </summary>
    public DownloadCompletionPipeline CreateCompletionPipeline()
    {
        var extractor = new ZipArchiveExtractor();
        var registrar = new CatalogManagedSdkRegistrar(catalogStore, DataRoot);
        return new DownloadCompletionPipeline(extractor, registrar);
    }

    // updater 可执行文件名常量。
    private const string UpdaterExecutableName = "DevSwitch.Updater.exe";

    /// <summary>
    /// 创建自更新编排服务（下载新版 zip → 校验 → 解压 → 启动 updater 覆盖并重启）。
    /// </summary>
    internal AppSelfUpdateService CreateSelfUpdateService()
    {
        // updater 与 helper/shim 同布局解析；找不到时由编排层报错。
        string? updaterPath = ResolveExecutablePath(UpdaterExecutableName);
        return new AppSelfUpdateService(httpClient, DataRoot, updaterPath);
    }

    // ===================== 内部工具 =====================

    /// <summary>
    /// 返回 helper 路径，缺失时抛出带友好中文消息的异常，由窗口层捕获弹窗。
    /// </summary>
    private string RequireHelperPath()
    {
        if (!IsHelperAvailable)
        {
            throw new HelperUnavailableException(
                "未找到 DevSwitch.Helper.exe，需要它来执行 SDK 切换、环境写入与链接操作。请确认 helper 已随应用一起部署。");
        }

        return HelperPath!;
    }

    /// <summary>
    /// 解析 helper.exe 路径：优先应用目录，逐级回退到开发期 artifacts\bin 等候选路径。
    /// </summary>
    /// <returns>存在的 helper 路径；都不存在时返回 null。</returns>
    private static string? ResolveHelperPath() => ResolveExecutablePath(HelperExecutableName);

    /// <summary>
    /// 通用：解析与 App 同布局的辅助可执行文件路径（helper/shim 共用同一查找策略）。
    /// </summary>
    /// <param name="executableName">可执行文件名。</param>
    /// <returns>存在的路径；都不存在时返回 null。</returns>
    private static string? ResolveExecutablePath(string executableName)
    {
        // 1) 部署态：与应用同目录（princess 包内与 exe 同级）。
        string primary = Path.Combine(AppContext.BaseDirectory, executableName);
        if (File.Exists(primary))
        {
            return primary;
        }

        // 2) 应用目录下的 bin 子目录（部分部署布局把辅助程序放在 bin\）。
        string appBin = Path.Combine(AppContext.BaseDirectory, "bin", executableName);
        if (File.Exists(appBin))
        {
            return appBin;
        }

        // 3) 开发态：从应用 bin 输出目录逐级向上查找仓库根，再定位 artifacts\bin\<exe>。
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; depth < 8 && directory is not null; depth++)
        {
            string candidate = Path.Combine(directory.FullName, "artifacts", "bin", executableName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// 共享 HttpClient 文本抓取实现，供版本源解析使用。
    /// </summary>
    private async Task<string> FetchTextAsync(string url, CancellationToken cancellationToken)
    {
        return await httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        httpClient.Dispose();
    }
}

/// <summary>
/// helper 不可用异常：窗口层捕获后统一弹友好对话框，不让应用崩溃。
/// </summary>
public sealed class HelperUnavailableException : Exception
{
    /// <summary>
    /// 创建 helper 不可用异常。
    /// </summary>
    public HelperUnavailableException(string message) : base(message)
    {
    }
}

// ===================== 验证服务抽象实现 =====================

/// <summary>
/// 通过 helper inspectLink 包装的 current 链接探测器（实现验证服务用的 <see cref="ISdkLinkInspector"/>）。
/// </summary>
internal sealed class HelperSdkLinkInspector : ISdkLinkInspector
{
    private readonly IHelperClient helperClient;
    private readonly ISdkCurrentPathProvider pathProvider;
    private readonly string dataRoot;

    /// <summary>
    /// 创建链接探测器。
    /// </summary>
    public HelperSdkLinkInspector(IHelperClient helperClient, ISdkCurrentPathProvider pathProvider, string dataRoot)
    {
        this.helperClient = helperClient;
        this.pathProvider = pathProvider;
        this.dataRoot = dataRoot;
    }

    /// <inheritdoc />
    public async Task<CurrentLinkInfo> InspectAsync(SdkType sdkType, CancellationToken cancellationToken = default)
    {
        // current 入口路径由 dataRoot\current\<slug> 推导。
        string currentPath = pathProvider.GetCurrentPath(dataRoot, sdkType);
        var response = await helperClient.InspectLinkAsync(currentPath, cancellationToken).ConfigureAwait(false);

        // 解析 helper 返回 details 中的 exists / targetPath。
        var (exists, targetPath) = HelperJson.ReadLinkInfo(response);
        return new CurrentLinkInfo(exists, targetPath);
    }
}

/// <summary>
/// 基于 System.Diagnostics.Process 的命令执行器（验证服务用的 <see cref="ISdkCommandRunner"/>），含超时控制。
/// </summary>
internal sealed class ProcessSdkCommandRunner : ISdkCommandRunner
{
    // 解析一个有效的 JAVA_HOME（active Java 记录路径），为 mvn 命令验证注入；可为 null。
    private readonly Func<string?>? javaHomeResolver;

    public ProcessSdkCommandRunner(Func<string?>? javaHomeResolver = null)
    {
        this.javaHomeResolver = javaHomeResolver;
    }

    /// <inheritdoc />
    public async Task<SdkCommandResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        // mvn.cmd 依赖 JAVA_HOME 定位 java；若系统 JAVA_HOME 无效会退出码 1。
        // 验证 Maven 时为子进程注入一个有效 JAVA_HOME（取 active Java 记录路径），不改本机环境。
        IReadOnlyDictionary<string, string>? env = null;
        string leaf = Path.GetFileName(fileName);
        if (leaf.StartsWith("mvn", StringComparison.OrdinalIgnoreCase) && javaHomeResolver is not null)
        {
            string? javaHome = javaHomeResolver();
            if (!string.IsNullOrWhiteSpace(javaHome) && Directory.Exists(javaHome))
            {
                env = new Dictionary<string, string> { ["JAVA_HOME"] = javaHome };
            }
        }

        var execution = await ProcessExecution.RunAsync(fileName, arguments, timeout, cancellationToken, env).ConfigureAwait(false);
        return new SdkCommandResult(execution.Started, execution.ExitCode, execution.StdOut, execution.StdErr, execution.TimedOut);
    }
}

// ===================== Doctor 抽象实现 =====================

/// <summary>
/// 读取 HKCU（当前用户）环境变量的 Doctor 环境读取器。
/// </summary>
internal sealed class HkcuEnvironmentReader : IEnvironmentReader
{
    /// <inheritdoc />
    public string? GetVariable(string name)
    {
        // 只读用户级环境变量，不触碰系统级，不需要管理员权限。
        return Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetPathEntries()
    {
        string? path = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);
        if (string.IsNullOrEmpty(path))
        {
            return Array.Empty<string>();
        }

        // 按 ';' 拆分，保留原始书写形式供前序冲突分析。
        return path.Split(';', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetMachinePathEntries()
    {
        string? path = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine);
        if (string.IsNullOrEmpty(path))
        {
            return Array.Empty<string>();
        }

        // 系统 PATH 在新进程中排在用户 PATH 之前；需要单独暴露给 Doctor 才能检测真实遮蔽。
        return path.Split(';', StringSplitOptions.RemoveEmptyEntries);
    }
}

/// <summary>
/// 通过 helper inspectLink 实现的 Doctor current 链接探测器（<see cref="ICurrentLinkInspector"/>）。
/// </summary>
internal sealed class HelperCurrentLinkInspector : ICurrentLinkInspector
{
    private readonly IHelperClient helperClient;

    /// <summary>
    /// 创建 Doctor current 链接探测器。
    /// </summary>
    public HelperCurrentLinkInspector(IHelperClient helperClient)
    {
        this.helperClient = helperClient;
    }

    /// <inheritdoc />
    public async Task<CurrentLinkInspection> InspectAsync(SdkType sdkType, string currentPath, CancellationToken cancellationToken = default)
    {
        var response = await helperClient.InspectLinkAsync(currentPath, cancellationToken).ConfigureAwait(false);
        var (exists, targetPath) = HelperJson.ReadLinkInfo(response);

        // 目标是否真实存在由本地目录判断（helper 只给出指向）。
        bool targetExists = !string.IsNullOrWhiteSpace(targetPath) && Directory.Exists(targetPath);
        return new CurrentLinkInspection(exists, targetPath, targetExists);
    }
}

/// <summary>
/// 基于 Process 的 Doctor 命令执行器（<see cref="ICommandRunner"/>），内置默认超时避免卡死 Doctor。
/// </summary>
internal sealed class ProcessDoctorCommandRunner : ICommandRunner
{
    // Doctor 命令解析超时；超时由本类终止进程并报告 TimedOut。
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

    /// <inheritdoc />
    public async Task<CommandResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken = default)
    {
        // Doctor 契约用单字符串参数；按空白拆分为参数数组传给底层执行器。
        var argList = string.IsNullOrWhiteSpace(arguments)
            ? Array.Empty<string>()
            : arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var execution = await ProcessExecution.RunAsync(fileName, argList, DefaultTimeout, cancellationToken).ConfigureAwait(false);
        return new CommandResult(execution.Started, execution.ExitCode, execution.StdOut, execution.StdErr, execution.TimedOut);
    }
}

/// <summary>
/// 通过 helper ping 操作实现的可用性探测（<see cref="IHelperPing"/>）。
/// </summary>
internal sealed class HelperPingClient : IHelperPing
{
    private readonly string helperPath;

    /// <summary>
    /// 创建 helper ping 客户端。
    /// </summary>
    public HelperPingClient(string helperPath)
    {
        this.helperPath = helperPath;
    }

    /// <inheritdoc />
    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // ping 不需要 payload；helper 返回 success=true 即视为可用。
            var response = await HelperJson.InvokeAsync(helperPath, "ping", null, cancellationToken).ConfigureAwait(false);
            return response.Success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 进程启动失败 / 无效响应都视为不可用，由 Doctor 报告 Fatal。
            return false;
        }
    }
}

// ===================== 下载登记实现 =====================

/// <summary>
/// 把下载完成的托管 SDK 登记进 sdks.json 的 <see cref="IManagedSdkRegistrar"/> 实现。
/// </summary>
internal sealed class CatalogManagedSdkRegistrar : IManagedSdkRegistrar
{
    private readonly SdkCatalogStore catalogStore;
    private readonly string dataRoot;

    /// <summary>
    /// 创建托管 SDK 登记器。
    /// </summary>
    public CatalogManagedSdkRegistrar(SdkCatalogStore catalogStore, string dataRoot)
    {
        this.catalogStore = catalogStore;
        this.dataRoot = dataRoot;
    }

    /// <inheritdoc />
    public async Task RegisterAsync(ManagedSdkRegistration registration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        cancellationToken.ThrowIfCancellationRequested();

        var catalog = await catalogStore.LoadOrCreateAsync(dataRoot).ConfigureAwait(false);

        // 构造托管 SDK 记录：来源为 Managed，路径为解压目录，状态可用。
        var record = new SdkRecord(
            Id: $"{TypeSlug(registration.SdkType)}-{Guid.NewGuid():N}",
            Type: registration.SdkType,
            Name: BuildName(registration),
            Version: registration.Version,
            Distribution: registration.Distribution,
            Architecture: registration.Arch,
            Source: SdkSourceKind.Managed,
            Path: registration.InstallDirectory,
            Status: SdkRecordStatus.Usable,
            CreatedAt: DateTimeOffset.UtcNow,
            LastVerifiedAt: null);

        var updated = catalog with { Items = catalog.Items.Concat(new[] { record }).ToArray() };
        await catalogStore.SaveAsync(dataRoot, updated).ConfigureAwait(false);
    }

    private static string BuildName(ManagedSdkRegistration registration)
    {
        // 友好名称形如 "Temurin 21.0.4 (x64)"，UI 列表更易读。
        string arch = registration.Arch switch
        {
            SdkArchitecture.X64 => " (x64)",
            SdkArchitecture.Arm64 => " (arm64)",
            _ => string.Empty,
        };
        return $"{registration.Distribution} {registration.Version}{arch}";
    }

    private static string TypeSlug(SdkType type) => type switch
    {
        SdkType.Java => "java",
        SdkType.Maven => "maven",
        SdkType.Node => "node",
        SdkType.Go => "go",
        _ => "sdk",
    };
}

// ===================== 更新源 HTTP 实现 =====================

/// <summary>
/// 基于 HttpClient 的 GitHub releases 抓取客户端（<see cref="IReleaseFeedClient"/>）。
/// </summary>
internal sealed class HttpReleaseFeedClient : IReleaseFeedClient
{
    private readonly HttpClient httpClient;

    /// <summary>
    /// 创建 releases 抓取客户端。
    /// </summary>
    public HttpReleaseFeedClient(HttpClient httpClient)
    {
        this.httpClient = httpClient;
    }

    /// <inheritdoc />
    public async Task<string> FetchAsync(string url, CancellationToken cancellationToken)
    {
        // 仅在用户点击「检查更新」时被调用；GitHub API 要求 User-Agent（已在共享 HttpClient 配置）。
        return await httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
    }
}

// ===================== 共享进程执行与 helper JSON 工具 =====================

/// <summary>
/// 进程执行结果（App 内部用，统一两个命令执行抽象的底层逻辑）。
/// </summary>
/// <param name="Started">进程是否成功启动。</param>
/// <param name="ExitCode">退出码；未启动/超时时为 null。</param>
/// <param name="StdOut">标准输出。</param>
/// <param name="StdErr">标准错误。</param>
/// <param name="TimedOut">是否因超时被终止。</param>
internal readonly record struct ProcessExecutionResult(bool Started, int? ExitCode, string StdOut, string StdErr, bool TimedOut);

/// <summary>
/// 进程执行工具：启动外部命令、收集输出、施加超时，绝不抛未捕获异常给上层。
/// </summary>
internal static class ProcessExecution
{
    /// <summary>
    /// 运行外部命令并收集输出。超时则终止进程并标记 TimedOut。
    /// </summary>
    public static async Task<ProcessExecutionResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environmentOverrides = null)
    {
        // NOTE: .cmd/.bat 是批处理脚本，UseShellExecute=false 时无法被 Process 直接启动
        //       （会抛 Win32Exception "不是有效的 Win32 应用程序"），必须经 cmd.exe /c 运行。
        //       mvn.cmd 即属此类；java.exe/node.exe/go.exe 是真 PE 可直接启动。
        string executable = fileName;
        var effectiveArgs = arguments;
        string extension = Path.GetExtension(fileName);
        if (string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase))
        {
            // cmd /c "<batch>" <args...>：把原命令作为第一个参数转交给 cmd.exe。
            var wrapped = new List<string>(arguments.Count + 2) { "/c", fileName };
            wrapped.AddRange(arguments);
            executable = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            effectiveArgs = wrapped;
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // 逐个加入参数，由 ProcessStartInfo 负责正确转义，避免命令注入。
        foreach (var argument in effectiveArgs)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // 应用环境变量覆盖（如为 mvn 注入有效 JAVA_HOME），仅影响子进程，不改本机环境。
        if (environmentOverrides is not null)
        {
            foreach (var pair in environmentOverrides)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                return new ProcessExecutionResult(false, null, string.Empty, string.Empty, false);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // 可执行文件不在 PATH 或无法启动：视为未启动。
            return new ProcessExecutionResult(false, null, string.Empty, string.Empty, false);
        }

        // 异步读取 stdout/stderr，避免缓冲区写满导致死锁。
        Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        // 用链接令牌合并外部取消与超时。
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 仅超时（外部未取消）：终止进程并报告 TimedOut。
            TryKill(process);
            return new ProcessExecutionResult(true, null, await SafeResult(stdOutTask), await SafeResult(stdErrTask), true);
        }
        catch (OperationCanceledException)
        {
            // 外部主动取消：终止进程并向上抛出。
            TryKill(process);
            throw;
        }

        string stdOut = await SafeResult(stdOutTask).ConfigureAwait(false);
        string stdErr = await SafeResult(stdErrTask).ConfigureAwait(false);
        return new ProcessExecutionResult(true, process.ExitCode, stdOut, stdErr, false);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 终止失败不影响结果判定，忽略。
        }
    }

    private static async Task<string> SafeResult(Task<string> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }
}

/// <summary>
/// helper JSON 调用与响应解析工具。
/// </summary>
internal static class HelperJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 以「一次请求一进程」方式调用 helper 指定操作并解析响应。
    /// </summary>
    public static async Task<HelperResponse> InvokeAsync(string helperPath, string operation, object? payload, CancellationToken cancellationToken)
    {
        var request = new HelperRequest(Guid.NewGuid().ToString("N"), operation, payload);
        var startInfo = new ProcessStartInfo(helperPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start DevSwitch helper.");
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request, SerializerOptions)).ConfigureAwait(false);
        await process.StandardInput.FlushAsync().ConfigureAwait(false);
        process.StandardInput.Close();

        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        string error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Deserialize<HelperResponse>(output, SerializerOptions)
            ?? throw new InvalidDataException($"Helper returned invalid JSON. stderr: {error}");
    }

    /// <summary>
    /// 从 helper inspectLink 响应 details 中提取 exists 与 targetPath。
    /// </summary>
    public static (bool Exists, string? TargetPath) ReadLinkInfo(HelperResponse response)
    {
        if (response.Details is not { } details || details.ValueKind != JsonValueKind.Object)
        {
            return (false, null);
        }

        bool exists = details.TryGetProperty("exists", out var existsElement)
            && existsElement.ValueKind is JsonValueKind.True or JsonValueKind.False
            && existsElement.GetBoolean();

        string? targetPath = null;
        if (details.TryGetProperty("targetPath", out var targetElement) && targetElement.ValueKind == JsonValueKind.String)
        {
            targetPath = targetElement.GetString();
        }

        return (exists, targetPath);
    }
}

/// <summary>
/// App 层自更新编排服务：下载新版 zip → 校验 → 解压 → 启动外部 updater 覆盖并重启。
/// </summary>
/// <remarks>
/// 设计要点（高性能 / 安全）：
/// - 仅在用户显式点击「下载并更新」时联网，复用共享 HttpClient（已配 User-Agent，GitHub API 要求）。
/// - 大文件流式下载并上报进度，绝不一次性载入内存。
/// - 校验仅在 release 提供 sha256 资产时执行；不匹配立即失败，防止损坏/被篡改的包覆盖程序。
/// - 覆盖动作交给独立的 DevSwitch.Updater.exe，在主程序退出后执行，绝不触碰 data 用户数据目录。
/// </remarks>
internal sealed class AppSelfUpdateService
{
    private readonly HttpClient httpClient;
    private readonly string dataRoot;
    private readonly string? updaterPath;

    public AppSelfUpdateService(HttpClient httpClient, string dataRoot, string? updaterPath)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.dataRoot = string.IsNullOrWhiteSpace(dataRoot) ? throw new ArgumentException("Data root is required.", nameof(dataRoot)) : dataRoot;
        this.updaterPath = updaterPath;
    }

    /// <summary>
    /// 解析指定仓库的最新 release，挑出 Windows 安装包，构造自更新计划。
    /// </summary>
    /// <param name="repository">GitHub 仓库标识（owner/repo 或 URL）。</param>
    /// <param name="includePrerelease">是否纳入预发布。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>计划 + release 页 URL；无法解析返回 (null, releaseUrl?)。</returns>
    public async Task<SelfUpdatePlan?> ResolvePlanAsync(string? repository, bool includePrerelease, CancellationToken cancellationToken)
    {
        // 1) 仓库 → releases API URL。
        string? apiUrl = GitHubRepoResolver.ResolveReleasesApiUrl(repository);
        if (string.IsNullOrWhiteSpace(apiUrl))
        {
            return null;
        }

        // 2) 拉取 releases JSON 并选出最新版本。
        string json = await httpClient.GetStringAsync(apiUrl, cancellationToken).ConfigureAwait(false);
        var latest = GitHubReleaseParser.ParseAndSelectLatest(json, includePrerelease);
        if (latest is null)
        {
            return null;
        }

        // 3) 从资产里挑 Windows zip（+ 可选 sha256）。
        var selected = UpdateAssetSelector.Select(latest.Assets);
        if (selected is null)
        {
            // 无可用安装包：仍返回 release 页，供 UI 回退到手动下载。
            return new SelfUpdatePlan(latest.TagName, string.Empty, string.Empty, null, latest.HtmlUrl);
        }

        return new SelfUpdatePlan(
            Version: latest.TagName,
            DownloadUrl: selected.Package.BrowserDownloadUrl,
            AssetFileName: selected.Package.Name,
            ChecksumUrl: selected.Checksum?.BrowserDownloadUrl,
            ReleaseUrl: latest.HtmlUrl);
    }

    /// <summary>
    /// 执行下载 → 校验 → 解压 → 启动 updater 的完整流程。成功返回后由调用方退出主程序让出文件锁。
    /// </summary>
    /// <param name="plan">自更新计划（需含有效 DownloadUrl）。</param>
    /// <param name="progress">阶段 + 下载百分比上报（0-1）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<SelfUpdateResult> RunAsync(
        SelfUpdatePlan plan,
        IProgress<(SelfUpdateStage Stage, double Percent)>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (string.IsNullOrWhiteSpace(plan.DownloadUrl))
        {
            return new SelfUpdateResult(false, SelfUpdateStage.Failed, "no-asset", "该版本没有可用的 Windows 安装包，请前往发布页手动下载。");
        }

        if (string.IsNullOrWhiteSpace(updaterPath) || !File.Exists(updaterPath))
        {
            return new SelfUpdateResult(false, SelfUpdateStage.Failed, "updater-missing", "未找到 DevSwitch.Updater.exe，无法自动覆盖更新。");
        }

        // 暂存目录：dataRoot\updates\<version>\，避免污染安装目录与用户数据。
        // NOTE: 更新包仅用于「下载→校验→解压→覆盖」这一次性流程，覆盖完成后不再需要保留。
        //       此前每个版本都会在 updates 下留下 zip + extracted 整套且从不清理，日积月累占用大量磁盘。
        //       这里在开始本次更新前，先清空整个 updates 目录（含所有历史版本残留）。
        //       之所以放在「下次更新开始时」清理、而非「本次结束时」清理：因为外部 updater 是在主程序退出后
        //       才异步从 extracted 复制文件，若本次结束就删会删掉 updater 仍要读取的源文件。
        string updatesRoot = Path.Combine(dataRoot, "updates");
        PurgeDirectoryContents(updatesRoot);

        string safeVersion = MakeSafeName(plan.Version);
        string updateRoot = Path.Combine(updatesRoot, safeVersion);
        string extractedDir = Path.Combine(updateRoot, "extracted");
        Directory.CreateDirectory(updateRoot);

        string zipPath = Path.Combine(updateRoot, string.IsNullOrWhiteSpace(plan.AssetFileName) ? "update.zip" : plan.AssetFileName);

        try
        {
            // 1) 下载（流式 + 进度）。
            progress?.Report((SelfUpdateStage.Downloading, 0));
            await DownloadFileAsync(plan.DownloadUrl, zipPath, progress, cancellationToken).ConfigureAwait(false);

            // 2) 校验（仅当有 sha256 资产）。
            if (!string.IsNullOrWhiteSpace(plan.ChecksumUrl))
            {
                progress?.Report((SelfUpdateStage.Verifying, 1));
                string expected = (await httpClient.GetStringAsync(plan.ChecksumUrl, cancellationToken).ConfigureAwait(false)).Trim();
                // sha256 文件常见格式 "<hash>  <filename>"，取首段。
                expected = expected.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? expected;
                var verify = await Sha256Verifier.VerifyAsync(zipPath, expected, cancellationToken).ConfigureAwait(false);
                if (!verify.IsMatch)
                {
                    return new SelfUpdateResult(false, SelfUpdateStage.Verifying, "checksum-mismatch", "下载包校验失败（SHA256 不匹配），已中止更新以保护程序完整性。");
                }
            }

            // 3) 解压到 extracted 目录（先清旧）。
            progress?.Report((SelfUpdateStage.Extracting, 1));
            if (Directory.Exists(extractedDir))
            {
                Directory.Delete(extractedDir, recursive: true);
            }
            Directory.CreateDirectory(extractedDir);
            await new ZipArchiveExtractor().ExtractAsync(zipPath, extractedDir, cancellationToken).ConfigureAwait(false);

            // 解压结果可能多套一层目录（zip 内含单个根文件夹）；若 extracted 下只有一个目录且无 exe，则下探一层。
            string sourceDir = ResolveSourceRoot(extractedDir);

            // 4) 启动 updater：等主程序退出 → 覆盖安装目录 → 重启。
            progress?.Report((SelfUpdateStage.LaunchingUpdater, 1));
            string targetDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string exePath = Path.Combine(targetDir, "DevSwitch.App.exe");
            string logPath = Path.Combine(dataRoot, "logs", "update.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

            string args = UpdaterArgumentsBuilder.Build(
                sourceDir, targetDir, exePath, Environment.ProcessId, logPath);

            var startInfo = new ProcessStartInfo(updaterPath!)
            {
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = updateRoot,
            };
            Process.Start(startInfo);

            return new SelfUpdateResult(true, SelfUpdateStage.LaunchingUpdater, null, "更新器已启动，应用即将退出并完成覆盖更新。");
        }
        catch (OperationCanceledException)
        {
            return new SelfUpdateResult(false, SelfUpdateStage.Failed, "cancelled", "更新已取消。");
        }
        catch (Exception ex)
        {
            return new SelfUpdateResult(false, SelfUpdateStage.Failed, "update-failed", $"更新失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 流式下载文件并上报进度（有 Content-Length 时为精确百分比，否则按已下载字节粗略推进）。
    /// </summary>
    private async Task DownloadFileAsync(
        string url,
        string destinationPath,
        IProgress<(SelfUpdateStage Stage, double Percent)>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long received = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            double percent = total is > 0 ? Math.Min(1.0, (double)received / total.Value) : 0.0;
            progress?.Report((SelfUpdateStage.Downloading, percent));
        }
    }

    /// <summary>
    /// 解析解压后的真实源根：若 extracted 下只有一个子目录且该层无 DevSwitch.App.exe，则下探到该子目录。
    /// </summary>
    private static string ResolveSourceRoot(string extractedDir)
    {
        if (File.Exists(Path.Combine(extractedDir, "DevSwitch.App.exe")))
        {
            return extractedDir;
        }

        var dirs = Directory.GetDirectories(extractedDir);
        var files = Directory.GetFiles(extractedDir);
        if (dirs.Length == 1 && files.Length == 0)
        {
            string nested = dirs[0];
            if (File.Exists(Path.Combine(nested, "DevSwitch.App.exe")))
            {
                return nested;
            }
        }

        return extractedDir;
    }

    /// <summary>
    /// 把版本号转成可作目录名的安全字符串。
    /// </summary>
    private static string MakeSafeName(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return "latest";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = version.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    /// <summary>
    /// 清空目录下的所有内容（文件与子目录），但保留目录本身。目录不存在时静默返回。
    /// </summary>
    /// <remarks>
    /// 用于在新一轮自更新前清理 updates 暂存目录里的历史残留，避免磁盘占用持续增长。
    /// 容错设计：逐项删除，单项失败（例如被占用）只跳过该项、不抛异常，确保清理尽力而为、
    /// 绝不因清理失败而中断后续更新流程。
    /// </remarks>
    private static void PurgeDirectoryContents(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            try
            {
                // 清除只读属性，避免删除被拒。
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
            catch
            {
                // 单个文件删除失败（占用/权限）不影响整体清理，跳过即可。
            }
        }

        foreach (var subDir in Directory.EnumerateDirectories(directory))
        {
            try
            {
                Directory.Delete(subDir, recursive: true);
            }
            catch
            {
                // 子目录删除失败同样跳过，最大化清理已能释放的空间。
            }
        }
    }
}

