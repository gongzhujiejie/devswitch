// 文件用途：DevSwitch Doctor 诊断编排器，按设计文档第 11 节执行 9 项检查并汇总分级报告。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.Threading.Tasks（隐式 using）
// NOTE: 合法授权学习使用，仅限本地环境。
//       Doctor 不自动修复外部冲突，只输出手动建议；所有外部能力均通过抽象注入，便于 fake 测试。

using System.Text.Json;

namespace DevSwitch.Core;

/// <summary>
/// Doctor 诊断服务：协调可写性、current 链接、环境变量、PATH、命令版本、helper 等检查。
/// </summary>
/// <remarks>
/// 设计要点：
/// - 每个检查项一个私有 async 方法，独立返回一条 <see cref="DiagnosticResult"/>。
/// - 检查项之间相互独立，使用 <c>Task.WhenAll</c> 并行执行，但按固定顺序汇总，保证报告稳定可读。
/// - 任意检查项抛异常都被降级为 Error 结果，绝不让单项失败中断整份报告。
/// - 命令类检查的超时由注入的 <see cref="ICommandRunner"/> 负责，Doctor 只透传 CancellationToken。
/// </remarks>
public sealed class DoctorService
{
    private readonly string dataRoot;
    private readonly SdkCatalogStore catalogStore;
    private readonly IEnvironmentReader environmentReader;
    private readonly ICurrentLinkInspector currentLinkInspector;
    private readonly ICommandRunner commandRunner;
    private readonly IHelperPing helperPing;
    private readonly ISdkCurrentPathProvider currentPathProvider;
    private readonly DevSwitchEnvironmentExpectations expectations;
    private readonly int supportedSchemaVersion;
    private readonly TimeProvider timeProvider;

    /// <summary>
    /// 创建 Doctor 诊断服务。
    /// </summary>
    /// <param name="dataRoot">DevSwitch 数据根目录。</param>
    /// <param name="catalogStore">SDK 目录存储，用于读取 active 与 schemaVersion。</param>
    /// <param name="environmentReader">环境读取抽象（变量、PATH）。</param>
    /// <param name="currentLinkInspector">current 链接探测抽象。</param>
    /// <param name="commandRunner">命令执行抽象（版本解析、npm prefix）。</param>
    /// <param name="helperPing">helper 可用性探测抽象。</param>
    /// <param name="currentPathProvider">current 路径提供者；为空时使用默认实现。</param>
    /// <param name="supportedSchemaVersion">当前支持的最高 schemaVersion；默认为 1。</param>
    /// <param name="timeProvider">时间源；为空时使用系统时间，便于测试注入固定时间。</param>
    public DoctorService(
        string dataRoot,
        SdkCatalogStore catalogStore,
        IEnvironmentReader environmentReader,
        ICurrentLinkInspector currentLinkInspector,
        ICommandRunner commandRunner,
        IHelperPing helperPing,
        ISdkCurrentPathProvider? currentPathProvider = null,
        int supportedSchemaVersion = 1,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("Data root is required.", nameof(dataRoot));
        }

        this.dataRoot = dataRoot;
        this.catalogStore = catalogStore ?? throw new ArgumentNullException(nameof(catalogStore));
        this.environmentReader = environmentReader ?? throw new ArgumentNullException(nameof(environmentReader));
        this.currentLinkInspector = currentLinkInspector ?? throw new ArgumentNullException(nameof(currentLinkInspector));
        this.commandRunner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));
        this.helperPing = helperPing ?? throw new ArgumentNullException(nameof(helperPing));
        this.currentPathProvider = currentPathProvider ?? new SdkCurrentPathProvider();
        this.expectations = new DevSwitchEnvironmentExpectations(dataRoot);
        this.supportedSchemaVersion = supportedSchemaVersion;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// 执行全部诊断检查并返回报告。
    /// </summary>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>包含所有检查结果与汇总的诊断报告。</returns>
    public async Task<DoctorReport> RunAsync(CancellationToken cancellationToken = default)
    {
        // NOTE: 各检查项相互独立，并行启动；用固定顺序的数组保证汇总结果稳定。
        var checkTasks = new[]
        {
            SafeCheck(CheckDataRootWritableAsync, "data-root-writable", "数据根目录可写性", cancellationToken),
            SafeCheck(CheckCurrentLinksAsync, "current-link-integrity", "current 链接完整性", cancellationToken),
            SafeCheck(CheckEnvironmentVariablesAsync, "hkcu-environment", "HKCU 环境变量", cancellationToken),
            SafeCheck(CheckManagedPathSegmentsAsync, "managed-path-segments", "DevSwitch PATH 片段", cancellationToken),
            SafeCheck(CheckPathConflictsAsync, "path-conflict", "PATH 前序冲突", cancellationToken),
            SafeCheck(CheckCommandVersionsAsync, "command-version", "命令解析版本", cancellationToken),
            SafeCheck(CheckGoToolchainAndNpmPrefixAsync, "gotoolchain-npm-prefix", "GOTOOLCHAIN / npm prefix", cancellationToken),
            SafeCheck(CheckHelperAvailabilityAsync, "helper-availability", "helper 可用性", cancellationToken),
            SafeCheck(CheckSchemaVersionAsync, "config-schema-version", "配置 schemaVersion", cancellationToken),
        };

        var results = await Task.WhenAll(checkTasks).ConfigureAwait(false);
        return new DoctorReport(results, timeProvider.GetUtcNow());
    }

    /// <summary>
    /// 包装单个检查，把异常降级为 Error 结果，避免中断整份报告。
    /// </summary>
    private static async Task<DiagnosticResult> SafeCheck(
        Func<CancellationToken, Task<DiagnosticResult>> check,
        string id,
        string title,
        CancellationToken cancellationToken)
    {
        try
        {
            return await check(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 取消是调用方意图，向上传播而不是当成检查失败。
            throw;
        }
        catch (Exception ex)
        {
            // NOTE: 检查项内部异常一律降级为 Error，detail 给出异常类型便于排错。
            return new DiagnosticResult(
                id,
                title,
                DiagnosticSeverity.Error,
                $"检查执行异常：{ex.GetType().Name}: {ex.Message}",
                "请导出诊断包并反馈该检查项异常。");
        }
    }

    /// <summary>
    /// 检查项 1：数据根目录可写性——尝试写临时文件再删除。
    /// </summary>
    private async Task<DiagnosticResult> CheckDataRootWritableAsync(CancellationToken cancellationToken)
    {
        const string id = "data-root-writable";
        const string title = "数据根目录可写性";

        if (!Directory.Exists(dataRoot))
        {
            return new DiagnosticResult(
                id, title, DiagnosticSeverity.Error,
                "数据根目录不存在。",
                "请确认 DevSwitch 已正确初始化数据目录。");
        }

        // 用唯一名临时文件验证写权限，写完立即删除，不留痕迹。
        var probeFile = Path.Combine(dataRoot, $".devswitch-doctor-{Guid.NewGuid():N}.tmp");
        try
        {
            // NOTE: 异步写入探针文件，避免阻塞线程；库代码补 ConfigureAwait(false)。
            // 探测后立即删除的行为与顺序保持不变。
            await File.WriteAllTextAsync(probeFile, "probe", cancellationToken).ConfigureAwait(false);
            File.Delete(probeFile);
            return DiagnosticResult.Pass(id, title, "数据根目录可写。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理可能残留的探针文件。
            TryDelete(probeFile);
            return new DiagnosticResult(
                id, title, DiagnosticSeverity.Error,
                $"数据根目录不可写：{ex.GetType().Name}。",
                "请检查目录权限，或将数据根目录迁移到当前用户可写位置。");
        }
    }

    /// <summary>
    /// 检查项 2：current 链接完整性——逐类型探测 current 入口是否指向有效目标。
    /// </summary>
    private async Task<DiagnosticResult> CheckCurrentLinksAsync(CancellationToken cancellationToken)
    {
        const string id = "current-link-integrity";
        const string title = "current 链接完整性";

        var sdkTypes = new[] { SdkType.Java, SdkType.Maven, SdkType.Node, SdkType.Go, SdkType.Rust };
        var broken = new List<string>();
        var missing = new List<string>();

        foreach (var sdkType in sdkTypes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentPath = currentPathProvider.GetCurrentPath(dataRoot, sdkType);
            var inspection = await currentLinkInspector.InspectAsync(sdkType, currentPath, cancellationToken).ConfigureAwait(false);

            var slug = SdkCurrentPathProvider.GetTypeSlug(sdkType);
            if (!inspection.Exists)
            {
                // 链接不存在通常表示该类型尚未切换，属于信息而非错误。
                missing.Add(slug);
            }
            else if (!inspection.IsHealthy)
            {
                broken.Add(slug);
            }
        }

        if (broken.Count > 0)
        {
            return new DiagnosticResult(
                id, title, DiagnosticSeverity.Error,
                $"以下 current 链接存在但目标无效：{string.Join("、", broken)}。",
                "请重新切换对应 SDK 以重建 current 链接。");
        }

        if (missing.Count == sdkTypes.Length)
        {
            return new DiagnosticResult(
                id, title, DiagnosticSeverity.Info,
                "尚未为任何 SDK 类型建立 current 链接。",
                "在主界面选择并切换 SDK 后会创建 current 链接。");
        }

        if (missing.Count > 0)
        {
            return new DiagnosticResult(
                id, title, DiagnosticSeverity.Info,
                $"以下 SDK 类型尚未建立 current 链接：{string.Join("、", missing)}。",
                null);
        }

        return DiagnosticResult.Pass(id, title, "所有 current 链接均指向有效目标。");
    }

    /// <summary>
    /// 检查项 3：HKCU 环境变量是否包含 DevSwitch 期望的变量。
    /// </summary>
    private Task<DiagnosticResult> CheckEnvironmentVariablesAsync(CancellationToken cancellationToken)
    {
        const string id = "hkcu-environment";
        const string title = "HKCU 环境变量";

        var missing = expectations.ExpectedVariableNames
            .Where(name => string.IsNullOrWhiteSpace(environmentReader.GetVariable(name)))
            .ToArray();

        if (missing.Length == expectations.ExpectedVariableNames.Count)
        {
            return Task.FromResult(new DiagnosticResult(
                id, title, DiagnosticSeverity.Error,
                "未检测到任何 DevSwitch 期望的用户环境变量。",
                "请通过 DevSwitch 初始化环境以写入用户级环境变量。"));
        }

        if (missing.Length > 0)
        {
            return Task.FromResult(new DiagnosticResult(
                id, title, DiagnosticSeverity.Warning,
                $"缺少以下期望的用户环境变量：{string.Join("、", missing)}。",
                "请通过 DevSwitch 重新写入用户级环境变量，并重启终端。"));
        }

        return Task.FromResult(DiagnosticResult.Pass(id, title, "已包含全部期望的用户环境变量。"));
    }

    /// <summary>
    /// 检查项 4：DevSwitch 托管 PATH 片段是否存在于 PATH。
    /// </summary>
    private Task<DiagnosticResult> CheckManagedPathSegmentsAsync(CancellationToken cancellationToken)
    {
        const string id = "managed-path-segments";
        const string title = "DevSwitch PATH 片段";

        // NOTE: 识别口径需与 CheckPathConflictsAsync 一致——只要 PATH 里存在任何 DevSwitch 托管目录
        //       （现代 shim 单目录，或老式 current\<type>\bin 逐类型条目）就视为已托管，不应一律报错。
        //       历史 bug：本检查只精确匹配唯一 shims 片段，老式逐类型 PATH（current\java\bin 等）
        //       因没有 shims 目录而被判定“未检测到任何托管片段”，恒为 Error。
        var pathEntries = environmentReader.GetPathEntries();
        var normalized = pathEntries
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 现代方案的期望片段（唯一 shims 目录），全部存在视为最健康状态。
        var missingShims = expectations.ManagedPathSegments
            .Where(segment => !normalized.Contains(NormalizePath(segment)))
            .ToArray();

        if (missingShims.Length == 0)
        {
            return Task.FromResult(DiagnosticResult.Pass(id, title, "PATH 已包含 DevSwitch 托管 shims 目录。"));
        }

        // shims 缺失时，回退识别老式逐类型托管目录：任何位于当前 dataRoot 下的 PATH 条目即视为 DevSwitch 托管。
        // 用 dataRoot 前缀判断，确保便携模式（dataRoot 非 LocalAppData）也能正确识别当前实际根目录下的条目。
        var normalizedDataRoot = NormalizePath(dataRoot);
        var hasLegacyManagedEntry = normalized.Any(entry =>
            entry.Equals(normalizedDataRoot, StringComparison.OrdinalIgnoreCase)
            || entry.StartsWith(normalizedDataRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

        if (hasLegacyManagedEntry)
        {
            // 老式逐类型片段可用但缺少现代 shims 目录：属于可优化的信息项，而非错误。
            return Task.FromResult(new DiagnosticResult(
                id, title, DiagnosticSeverity.Info,
                "PATH 含 DevSwitch 老式逐类型托管片段，但未检测到 shims 目录。",
                "建议通过 DevSwitch 重新初始化环境，迁移到 shims 单目录方案以规避系统 PATH 长度上限。"));
        }

        return Task.FromResult(new DiagnosticResult(
            id, title, DiagnosticSeverity.Error,
            "PATH 中未检测到任何 DevSwitch 托管片段。",
            "请通过 DevSwitch 初始化环境写入托管 PATH 片段，并重启终端。"));
    }

    /// <summary>
    /// 检查项 5：PATH 前序冲突 / 遮蔽检测，给出手动清理建议，绝不自动修改。
    /// </summary>
    private Task<DiagnosticResult> CheckPathConflictsAsync(CancellationToken cancellationToken)
    {
        const string id = "path-conflict";
        const string title = "PATH 前序冲突";

        // Windows 有效 PATH 顺序是 Machine → User；仅检查 HKCU 用户 PATH 会漏掉系统 PATH 里的旧 JDK。
        // 这里用真实合并顺序分析，确保能暴露“用户 PATH 已置顶但仍被系统 PATH 遮蔽”的场景。
        var machineEntries = environmentReader.GetMachinePathEntries();
        var userEntries = environmentReader.GetPathEntries();
        var report = PathConflictAnalyzer.AnalyzeEffectivePath(machineEntries, userEntries, expectations.ManagedPathSegments);

        if (!report.HasConflicts)
        {
            return Task.FromResult(DiagnosticResult.Pass(id, title, "未检测到可能遮蔽 DevSwitch 的 PATH 前序冲突。"));
        }

        // 把所有冲突的手动建议合并到一条 Warning，遵循“只提示不自动改”原则。
        var detail = string.Join(
            Environment.NewLine,
            report.Conflicts.Select(c => $"[{c.Source}] {c.ShadowingEntry} 可能遮蔽 {string.Join("、", c.Commands)}"));
        var suggestion = string.Join(
            Environment.NewLine,
            report.Conflicts.Select(PathConflictAnalyzer.BuildSuggestion));

        return Task.FromResult(new DiagnosticResult(
            id, title, DiagnosticSeverity.Warning, detail, suggestion));
    }

    /// <summary>
    /// 检查项 6：命令解析版本——通过注入的命令执行抽象运行 java -version 等。
    /// </summary>
    private async Task<DiagnosticResult> CheckCommandVersionsAsync(CancellationToken cancellationToken)
    {
        const string id = "command-version";
        const string title = "命令解析版本";

        // (命令, 参数) 对应设计文档第 9 节版本命令。
        var probes = new (string FileName, string Arguments)[]
        {
            ("java", "-version"),
            ("node", "-v"),
            ("go", "version"),
            ("mvn", "-v"),
            ("rustc", "--version"),
        };

        var unresolved = new List<string>();
        var timedOut = new List<string>();

        foreach (var (fileName, arguments) in probes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await commandRunner.RunAsync(fileName, arguments, cancellationToken).ConfigureAwait(false);
            if (result.TimedOut)
            {
                timedOut.Add(fileName);
            }
            else if (!result.Started || !result.Succeeded)
            {
                unresolved.Add(fileName);
            }
        }

        if (timedOut.Count > 0)
        {
            return new DiagnosticResult(
                id, title, DiagnosticSeverity.Warning,
                $"以下命令解析超时：{string.Join("、", timedOut)}。",
                "命令执行超时可能由 PATH 遮蔽或磁盘繁忙引起，请稍后重试或检查 PATH。");
        }

        if (unresolved.Count == probes.Length)
        {
            return new DiagnosticResult(
                id, title, DiagnosticSeverity.Warning,
                "未能解析任何 SDK 命令版本。",
                "请确认已切换 SDK 并重启终端，使 PATH 生效。");
        }

        if (unresolved.Count > 0)
        {
            return new DiagnosticResult(
                id, title, DiagnosticSeverity.Info,
                $"以下命令未解析到版本：{string.Join("、", unresolved)}。",
                "若这些 SDK 未安装可忽略；否则请检查 PATH 与切换状态。");
        }

        return DiagnosticResult.Pass(id, title, "所有 SDK 命令均解析到版本。");
    }

    /// <summary>
    /// 检查项 7：GOTOOLCHAIN 与 npm prefix 检查。
    /// </summary>
    private async Task<DiagnosticResult> CheckGoToolchainAndNpmPrefixAsync(CancellationToken cancellationToken)
    {
        const string id = "gotoolchain-npm-prefix";
        const string title = "GOTOOLCHAIN / npm prefix";

        var notes = new List<string>();
        var severity = DiagnosticSeverity.Pass;

        // GOTOOLCHAIN=auto/local 可能导致 Go 自动下载其它工具链版本，影响版本一致性。
        var goToolchain = environmentReader.GetOptionalVariable("GOTOOLCHAIN");
        if (!string.IsNullOrWhiteSpace(goToolchain)
            && !string.Equals(goToolchain, "local", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add($"GOTOOLCHAIN={goToolchain} 可能触发 Go 自动切换工具链版本。");
            severity = DiagnosticSeverity.Info;
        }

        // npm prefix 若指向 DevSwitch current 之外的固定目录，会让全局包脱离切换管理。
        cancellationToken.ThrowIfCancellationRequested();
        var npmPrefix = await commandRunner.RunAsync("npm", "prefix -g", cancellationToken).ConfigureAwait(false);
        if (npmPrefix.TimedOut)
        {
            notes.Add("npm prefix 查询超时。");
            severity = MaxSeverity(severity, DiagnosticSeverity.Info);
        }
        else if (npmPrefix.Succeeded)
        {
            var prefix = npmPrefix.StandardOutput.Trim();
            // 若 npm 全局前缀不在 DevSwitch 数据根目录下，提示可能脱离托管。
            if (!string.IsNullOrWhiteSpace(prefix)
                && !prefix.Contains("DevSwitch", StringComparison.OrdinalIgnoreCase)
                && !NormalizePath(prefix).StartsWith(NormalizePath(dataRoot), StringComparison.OrdinalIgnoreCase))
            {
                notes.Add("npm 全局 prefix 不在 DevSwitch 管理范围内，全局包可能脱离版本切换。");
                severity = MaxSeverity(severity, DiagnosticSeverity.Warning);
            }
        }

        if (notes.Count == 0)
        {
            return DiagnosticResult.Pass(id, title, "GOTOOLCHAIN 与 npm prefix 配置正常。");
        }

        return new DiagnosticResult(
            id, title, severity,
            string.Join(Environment.NewLine, notes),
            severity >= DiagnosticSeverity.Warning
                ? "如需保持版本一致，请将 GOTOOLCHAIN 设为 local，并将 npm prefix 指向 DevSwitch current。"
                : null);
    }

    /// <summary>
    /// 检查项 8：helper 可用性。
    /// </summary>
    private async Task<DiagnosticResult> CheckHelperAvailabilityAsync(CancellationToken cancellationToken)
    {
        const string id = "helper-availability";
        const string title = "helper 可用性";

        var available = await helperPing.PingAsync(cancellationToken).ConfigureAwait(false);
        if (available)
        {
            return DiagnosticResult.Pass(id, title, "helper 可用。");
        }

        // helper 不可用对应设计文档第 12 节 Fatal 等级（弹窗 + 导出诊断包入口）。
        return new DiagnosticResult(
            id, title, DiagnosticSeverity.Fatal,
            "helper 不可用，切换与环境写入操作将无法执行。",
            "请重新安装或修复 DevSwitch.Helper，并导出诊断包反馈。");
    }

    /// <summary>
    /// 检查项 9：配置 schemaVersion 检查。
    /// </summary>
    private async Task<DiagnosticResult> CheckSchemaVersionAsync(CancellationToken cancellationToken)
    {
        const string id = "config-schema-version";
        const string title = "配置 schemaVersion";

        // NOTE: 不能用 SdkCatalogStore.LoadOrCreateAsync 读取——它在 schemaVersion 高于支持版本时
        // 会主动抛 InvalidDataException（这正是本检查要诊断的致命场景）。
        // 这里直接读原始 JSON 解析 schemaVersion，把"过高版本"优雅报告为 Fatal 而非异常降级 Error。
        var catalogFile = Path.Combine(dataRoot, "config", "sdks.json");
        if (!File.Exists(catalogFile))
        {
            // 文件尚未创建：等价于全新初始化，按当前支持版本视为通过。
            return DiagnosticResult.Pass(id, title, $"配置 schemaVersion 默认为 {supportedSchemaVersion}（sdks.json 尚未创建）。");
        }

        int schemaVersion;
        try
        {
            // NOTE: schemaVersion 诊断需直接解析原始 JSON（见上方说明），保留 string 读取，补 ConfigureAwait(false)。
            var json = await File.ReadAllTextAsync(catalogFile, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("schemaVersion", out var schemaElement)
                || schemaElement.ValueKind != JsonValueKind.Number)
            {
                return new DiagnosticResult(
                    id, title, DiagnosticSeverity.Error,
                    "sdks.json 缺少有效的 schemaVersion 字段。",
                    "请检查 sdks.json 是否损坏，必要时从 backups/config 恢复。");
            }

            schemaVersion = schemaElement.GetInt32();
        }
        catch (JsonException)
        {
            return new DiagnosticResult(
                id, title, DiagnosticSeverity.Error,
                "sdks.json 不是有效的 JSON。",
                "请检查 sdks.json 是否损坏，必要时从 backups/config 恢复。");
        }

        if (schemaVersion > supportedSchemaVersion)
        {
            // 高于支持版本属于致命：配置迁移失败场景，需弹窗 + 导出诊断包入口。
            return new DiagnosticResult(
                id, title, DiagnosticSeverity.Fatal,
                $"sdks.json schemaVersion {schemaVersion} 高于支持的版本 {supportedSchemaVersion}。",
                "请升级 DevSwitch 到支持该配置版本的版本。");
        }

        if (schemaVersion < supportedSchemaVersion)
        {
            return new DiagnosticResult(
                id, title, DiagnosticSeverity.Info,
                $"sdks.json schemaVersion {schemaVersion} 低于当前版本 {supportedSchemaVersion}，将在启动时迁移。",
                null);
        }

        return DiagnosticResult.Pass(id, title, $"配置 schemaVersion 为 {schemaVersion}，符合预期。");
    }

    /// <summary>
    /// 取两个严重等级中的较高者。
    /// </summary>
    private static DiagnosticSeverity MaxSeverity(DiagnosticSeverity left, DiagnosticSeverity right)
        => left >= right ? left : right;

    /// <summary>
    /// 规整路径用于相等/前缀比较：去引号空白、把正斜杠统一为反斜杠、去掉尾部分隔符。
    /// </summary>
    private static string NormalizePath(string path)
        => path.Trim().Trim('"')
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// 尽力删除探针文件，忽略删除失败。
    /// </summary>
    private static void TryDelete(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // NOTE: 探针文件清理失败不影响诊断结论，静默忽略。
        }
    }
}
