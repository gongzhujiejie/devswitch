// 文件用途：实现 DevSwitch「SDK 验证」服务，提供轻量验证、手动命令验证与纯函数状态推导/版本解析。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：仅 BCL（System、System.IO、System.Linq、System.Text.RegularExpressions、System.Threading）
// NOTE: 合法授权学习使用，仅限本地环境。
//       - 轻量验证不运行任何外部命令，只做链接探测 + 关键文件存在性检查（权威需求 requirements.md 第9节）。
//       - 手动命令验证通过注入的 ICommandRunner 运行版本命令，超时由 runner 负责，服务只透传取消令牌。

using System.Text.RegularExpressions;

namespace DevSwitch.Core;

/// <summary>
/// SDK 验证服务：轻量验证（不跑命令）与手动命令验证（跑版本命令）。
/// 解析器与状态推导均为纯静态函数，便于独立单元测试。
/// </summary>
public sealed partial class SdkVerificationService
{
    // NOTE: 以下 4 个正则用 .NET 8 source-generated 正则（[GeneratedRegex]）。
    //       编译期生成最优匹配代码，零运行时编译开销，替代热路径上每次 Regex.Match 重新解析编译字面量模式。
    //       pattern、RegexOptions、捕获组序号与原 Regex.Match 调用完全一致，行为不变。

    // 解析 java -version：形如 version "21.0.2" 或 version "1.8.0_392"，捕获组 1 为引号内整体版本串。
    [GeneratedRegex("version\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex JavaVersionRegex();

    // 解析 mvn -v：形如 Apache Maven 3.9.6，捕获组 1 为版本串。
    [GeneratedRegex("Apache\\s+Maven\\s+([0-9][0-9A-Za-z\\.\\-]*)", RegexOptions.IgnoreCase)]
    private static partial Regex MavenVersionRegex();

    // 解析 node -v：形如 v20.11.1，捕获组 1 为去掉 v 前缀的数字版本串。原调用无 IgnoreCase，保持 RegexOptions.None。
    [GeneratedRegex("v?([0-9]+\\.[0-9]+\\.[0-9]+)")]
    private static partial Regex NodeVersionRegex();

    // 解析 go version：形如 go1.22.0，捕获组 1 为 go 前缀后的数字版本串。
    [GeneratedRegex("go([0-9]+\\.[0-9]+(?:\\.[0-9]+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex GoVersionRegex();

    // NOTE: 默认命令超时；真实超时控制交由注入的 ICommandRunner，此处仅作为透传默认值。
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(10);

    private readonly ISdkLinkInspector linkInspector;
    private readonly ISdkCommandRunner commandRunner;

    /// <summary>
    /// 创建 SDK 验证服务。
    /// </summary>
    /// <param name="linkInspector">current 入口链接探测抽象。</param>
    /// <param name="commandRunner">外部命令执行抽象。</param>
    public SdkVerificationService(ISdkLinkInspector linkInspector, ISdkCommandRunner commandRunner)
    {
        this.linkInspector = linkInspector ?? throw new ArgumentNullException(nameof(linkInspector));
        this.commandRunner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));
    }

    /// <summary>
    /// 轻量验证：确认 current 入口是否指向目标 SDK，且关键命令文件是否存在。不运行任何外部命令。
    /// </summary>
    /// <param name="record">待验证的 SDK 记录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>版本状态、是否为当前 current、路径存在性与缺失关键文件列表。</returns>
    public async Task<LightweightVerificationResult> LightweightVerifyAsync(SdkRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        // 探测 current 入口当前指向，判断本记录是否为「使用中」。
        var linkInfo = await linkInspector.InspectAsync(record.Type, cancellationToken).ConfigureAwait(false);
        bool isCurrent = linkInfo.Exists
            && !string.IsNullOrWhiteSpace(linkInfo.TargetPath)
            && PathsEqual(linkInfo.TargetPath!, record.Path);

        // 校验根目录是否存在；不存在则关键文件必然全部缺失。
        bool pathExists = !string.IsNullOrWhiteSpace(record.Path) && Directory.Exists(record.Path);
        var missingKeyFiles = pathExists
            ? GetMissingKeyFiles(record.Type, record.Path)
            : GetRequiredKeyFiles(record.Type);

        // 纯函数推导最终状态。
        var status = DeriveStatus(pathExists, missingKeyFiles.Count == 0, isCurrent);

        return new LightweightVerificationResult(status, isCurrent, pathExists, missingKeyFiles);
    }

    /// <summary>
    /// 手动命令验证：运行该 SDK 类型的版本命令并解析版本号。命令未启动/超时/非零退出分别给出清晰结果，不抛未捕获异常。
    /// </summary>
    /// <param name="record">待验证的 SDK 记录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命令验证结果，含结果分类与解析出的版本号。</returns>
    public async Task<CommandVerificationResult> RunCommandVerificationAsync(SdkRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        // BUGFIX: 必须用记录自身 SDK 的绝对可执行路径，否则裸命令名（"java"/"mvn"...）会被
        //         全局 PATH 解析，导致验证任意 JDK 都跑系统 PATH 里的同一个 java（三个 JDK 报同版本），
        //         Maven 因不在 PATH 而报「未能启动」。改为基于 record.Path 解析。
        var (fileName, arguments) = ResolveVersionCommand(record.Type, record.Path);

        // 超时由 runner 负责，服务只透传默认超时与取消令牌。
        var result = await commandRunner.RunAsync(fileName, arguments, DefaultCommandTimeout, cancellationToken).ConfigureAwait(false);

        // 未启动：可执行文件不存在或无法解析（runner 返回 Started=false）。
        // message 直接带上绝对路径，便于定位「该路径下找不到可执行文件」。
        if (!result.Started)
        {
            return new CommandVerificationResult(
                CommandVerificationOutcome.NotStarted, null, fileName, result.ExitCode, result.StdOut, result.StdErr,
                $"Command '{fileName}' did not start.");
        }

        // 超时：命令被 runner 终止。
        if (result.TimedOut)
        {
            return new CommandVerificationResult(
                CommandVerificationOutcome.TimedOut, null, fileName, result.ExitCode, result.StdOut, result.StdErr,
                $"Command '{fileName}' timed out.");
        }

        // 非零退出：命令执行失败。
        if (result.ExitCode is not 0)
        {
            return new CommandVerificationResult(
                CommandVerificationOutcome.NonZeroExit, null, fileName, result.ExitCode, result.StdOut, result.StdErr,
                $"Command '{fileName}' exited with code {result.ExitCode?.ToString() ?? "unknown"}.");
        }

        // 成功退出后从 stdout/stderr 解析版本号（Java 的版本信息位于 stderr）。
        string? version = ParseVersion(record.Type, result.StdOut, result.StdErr);
        if (string.IsNullOrWhiteSpace(version))
        {
            return new CommandVerificationResult(
                CommandVerificationOutcome.ParseFailed, null, fileName, result.ExitCode, result.StdOut, result.StdErr,
                $"Could not parse version from '{fileName}' output.");
        }

        return new CommandVerificationResult(
            CommandVerificationOutcome.Verified, version, fileName, result.ExitCode, result.StdOut, result.StdErr,
            $"Resolved version {version}.");
    }

    /// <summary>
    /// 纯函数：根据「路径是否存在 + 关键文件是否齐全 + 是否 current」推导 SDK 记录状态。
    /// </summary>
    /// <param name="pathExists">SDK 根目录是否存在。</param>
    /// <param name="keyFilesComplete">关键命令文件是否齐全。</param>
    /// <param name="isCurrent">是否为当前 current 指向。</param>
    /// <returns>推导出的版本状态。</returns>
    public static SdkRecordStatus DeriveStatus(bool pathExists, bool keyFilesComplete, bool isCurrent)
    {
        // 路径缺失或关键文件不齐 -> 不可用。
        if (!pathExists || !keyFilesComplete)
        {
            return SdkRecordStatus.Unavailable;
        }

        // 结构可用且为 current -> 使用中；否则仅可用。
        return isCurrent ? SdkRecordStatus.Active : SdkRecordStatus.Usable;
    }

    /// <summary>
    /// 纯函数：根据 SDK 类型与命令输出解析版本号。Java 的版本信息位于 stderr，其余位于 stdout。
    /// </summary>
    /// <param name="sdkType">SDK 类型。</param>
    /// <param name="stdout">标准输出全文。</param>
    /// <param name="stderr">标准错误全文。</param>
    /// <returns>解析出的版本号；失败时为 null。</returns>
    public static string? ParseVersion(SdkType sdkType, string stdout, string stderr)
    {
        // 多数命令版本在 stdout；Java 在 stderr。合并搜索可兼容不同发行版差异。
        string primary = sdkType == SdkType.Java ? stderr : stdout;
        string fallback = sdkType == SdkType.Java ? stdout : stderr;

        return sdkType switch
        {
            SdkType.Java => ParseJavaVersion(primary) ?? ParseJavaVersion(fallback),
            SdkType.Maven => ParseMavenVersion(primary) ?? ParseMavenVersion(fallback),
            SdkType.Node => ParseNodeVersion(primary) ?? ParseNodeVersion(fallback),
            SdkType.Go => ParseGoVersion(primary) ?? ParseGoVersion(fallback),
            _ => null,
        };
    }

    /// <summary>
    /// 解析 java -version 输出，例如：openjdk version "21.0.2" 2024-01-16。
    /// </summary>
    private static string? ParseJavaVersion(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // 形如 version "21.0.2" 或 version "1.8.0_392"，取引号内整体作为版本串。
        var match = JavaVersionRegex().Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// 解析 mvn -v 输出，例如：Apache Maven 3.9.6 (...)。
    /// </summary>
    private static string? ParseMavenVersion(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = MavenVersionRegex().Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// 解析 node -v 输出，例如：v20.11.1。
    /// </summary>
    private static string? ParseNodeVersion(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // Node 版本以 v 前缀输出，去掉前缀只保留数字版本串。
        var match = NodeVersionRegex().Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// 解析 go version 输出，例如：go version go1.22.0 windows/amd64。
    /// </summary>
    private static string? ParseGoVersion(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // 版本以 go 前缀出现，例如 go1.22.0；保留前缀后的数字版本串。
        var match = GoVersionRegex().Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// 纯函数：返回某 SDK 类型的关键命令文件相对路径列表（与 design.md 第9节一致）。
    /// </summary>
    /// <param name="sdkType">SDK 类型。</param>
    /// <returns>关键命令文件相对路径列表。</returns>
    public static IReadOnlyList<string> GetRequiredKeyFiles(SdkType sdkType)
    {
        return sdkType switch
        {
            // JDK：release + bin\java.exe + bin\javac.exe。
            SdkType.Java => new[]
            {
                "release",
                Path.Combine("bin", "java.exe"),
                Path.Combine("bin", "javac.exe"),
            },
            // Maven：bin\mvn.cmd。
            SdkType.Maven => new[] { Path.Combine("bin", "mvn.cmd") },
            // Node.js：node.exe + npm.cmd + npx.cmd（位于根目录）。
            SdkType.Node => new[] { "node.exe", "npm.cmd", "npx.cmd" },
            // Go：bin\go.exe + bin\gofmt.exe。
            SdkType.Go => new[]
            {
                Path.Combine("bin", "go.exe"),
                Path.Combine("bin", "gofmt.exe"),
            },
            _ => Array.Empty<string>(),
        };
    }

    /// <summary>
    /// 复用关键文件清单，返回根目录下缺失的关键命令文件相对路径列表。
    /// </summary>
    /// <param name="sdkType">SDK 类型。</param>
    /// <param name="rootPath">SDK 根目录。</param>
    /// <returns>缺失的关键命令文件相对路径列表；齐全时为空。</returns>
    public static IReadOnlyList<string> GetMissingKeyFiles(SdkType sdkType, string rootPath)
    {
        var required = GetRequiredKeyFiles(sdkType);
        if (required.Count == 0)
        {
            // 未知类型没有关键文件清单可校验，视为「无缺失」由上层路径/类型判定兜底。
            return Array.Empty<string>();
        }

        var missing = new List<string>();
        foreach (var relative in required)
        {
            if (!File.Exists(Path.Combine(rootPath, relative)))
            {
                missing.Add(relative);
            }
        }

        return missing;
    }

    /// <summary>
    /// 纯函数：根据 SDK 类型选择版本命令与参数（与 design.md 第9节一致）。
    /// </summary>
    /// <param name="sdkType">SDK 类型。</param>
    /// <returns>命令名与参数列表。</returns>
    public static (string FileName, IReadOnlyList<string> Arguments) GetVersionCommand(SdkType sdkType)
    {
        return sdkType switch
        {
            SdkType.Java => ("java", new[] { "-version" }),
            SdkType.Maven => ("mvn", new[] { "-v" }),
            SdkType.Node => ("node", new[] { "-v" }),
            SdkType.Go => ("go", new[] { "version" }),
            _ => throw new ArgumentOutOfRangeException(nameof(sdkType), sdkType, "Unsupported SDK type for command verification."),
        };
    }

    /// <summary>
    /// 纯函数：根据 SDK 类型与其根目录解析「绝对可执行文件路径 + 参数」。
    /// 命令验证须用记录自身 SDK 的可执行文件，而非依赖系统 PATH，避免不同记录都解析到同一个全局命令。
    /// </summary>
    /// <param name="sdkType">SDK 类型。</param>
    /// <param name="rootPath">该 SDK 记录的根目录（record.Path）。</param>
    /// <returns>绝对可执行文件路径与参数列表。</returns>
    public static (string FileName, IReadOnlyList<string> Arguments) ResolveVersionCommand(SdkType sdkType, string rootPath)
    {
        return sdkType switch
        {
            // Java：<root>\bin\java.exe -version（版本信息在 stderr）。
            SdkType.Java => (Path.Combine(rootPath, "bin", "java.exe"), new[] { "-version" }),
            // Maven：<root>\bin\mvn.cmd -v。
            SdkType.Maven => (Path.Combine(rootPath, "bin", "mvn.cmd"), new[] { "-v" }),
            // Node：Windows zip 根目录直接有 node.exe -v。
            SdkType.Node => (Path.Combine(rootPath, "node.exe"), new[] { "-v" }),
            // Go：<root>\bin\go.exe version。
            SdkType.Go => (Path.Combine(rootPath, "bin", "go.exe"), new[] { "version" }),
            _ => throw new ArgumentOutOfRangeException(nameof(sdkType), sdkType, "Unsupported SDK type for command verification."),
        };
    }

    /// <summary>
    /// 与 SdkSwitchService 保持一致的路径比较：Windows 大小写不敏感，去尾分隔符。
    /// </summary>
    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(NormalizePath(left), NormalizePath(right), comparison);
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
