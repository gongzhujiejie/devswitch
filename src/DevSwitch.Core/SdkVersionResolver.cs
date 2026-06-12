// 文件用途：纯文件/目录名解析本地 SDK 版本号，不运行任何外部命令。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.IO、System.Text.RegularExpressions
// NOTE: 合法授权学习使用，仅限本地环境。解析仅读取元数据文件与目录名，绝不执行 SDK 可执行文件。

using System.Text.RegularExpressions;

namespace DevSwitch.Core;

/// <summary>
/// 通过纯文件解析（release / VERSION 文件、目录名）识别本地 SDK 版本号。
/// 全部方法稳健处理缺失/无权限/异常输入，绝不抛出，也绝不运行外部命令。
/// </summary>
public static partial class SdkVersionResolver
{
    /// <summary>
    /// 无法解析时统一返回的占位版本号。
    /// </summary>
    public const string UnknownVersion = "unknown";

    // NOTE: 匹配 JDK release 文件中的 JAVA_VERSION 行，捕获引号内（或无引号）值。
    //       取值允许数字、点、下划线（1.8.0_442）、加号、连字符（构建元数据）。
    [GeneratedRegex("""^\s*JAVA_VERSION\s*=\s*"?(?<v>[0-9][0-9._+\-]*)"?\s*$""", RegexOptions.Multiline)]
    private static partial Regex JavaReleaseRegex();

    // NOTE: 目录名中“jdk”后紧跟的版本号，优先级高于发行版前缀（zulu17... 应取 jdk 后的 17.0.11）。
    [GeneratedRegex("""jdk[-_]?(?<v>[0-9]+(?:\.[0-9]+)*(?:_[0-9]+)?)""", RegexOptions.IgnoreCase)]
    private static partial Regex JdkInNameRegex();

    // NOTE: 通用版本 token：形如 1.8.0_442、3.9.16、22.11.0，可被前缀 v 修饰（node-v22.11.0）。
    [GeneratedRegex("""(?<![0-9])v?(?<v>[0-9]+(?:\.[0-9]+)+(?:_[0-9]+)?)""")]
    private static partial Regex GenericVersionRegex();

    /// <summary>
    /// 按 SDK 类型与根目录解析版本号。
    /// </summary>
    /// <param name="type">SDK 类型。</param>
    /// <param name="rootPath">SDK 根目录路径。</param>
    /// <returns>解析出的版本号；任何失败路径返回 <see cref="UnknownVersion"/>。</returns>
    public static string ResolveVersion(SdkType type, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return UnknownVersion;
        }

        var version = type switch
        {
            SdkType.Java => ResolveJava(rootPath),
            SdkType.Go => ResolveGo(rootPath),
            SdkType.Maven => ParseVersionFromDirectoryName(SdkType.Maven, GetLeafName(rootPath)),
            SdkType.Node => ParseVersionFromDirectoryName(SdkType.Node, GetLeafName(rootPath)),
            SdkType.Rust => ParseVersionFromDirectoryName(SdkType.Rust, GetLeafName(rootPath)),
            _ => null,
        };

        return string.IsNullOrEmpty(version) ? UnknownVersion : version;
    }

    /// <summary>
    /// 解析 Java 版本：优先 release 文件的 JAVA_VERSION，缺失则回退目录名。
    /// </summary>
    private static string? ResolveJava(string rootPath)
    {
        var releaseContent = TryReadFile(Path.Combine(rootPath, "release"));
        if (releaseContent is not null)
        {
            var fromRelease = ParseJavaReleaseVersion(releaseContent);
            if (!string.IsNullOrEmpty(fromRelease))
            {
                return fromRelease;
            }
        }

        // 回退：release 缺失或无有效字段时，从目录名提取版本。
        return ParseVersionFromDirectoryName(SdkType.Java, GetLeafName(rootPath));
    }

    /// <summary>
    /// 解析 Go 版本：优先 VERSION 文件首行，缺失则回退目录名。
    /// </summary>
    private static string? ResolveGo(string rootPath)
    {
        var versionContent = TryReadFile(Path.Combine(rootPath, "VERSION"));
        if (versionContent is not null)
        {
            var fromFile = ParseGoVersion(versionContent);
            if (!string.IsNullOrEmpty(fromFile))
            {
                return fromFile;
            }
        }

        return ParseVersionFromDirectoryName(SdkType.Go, GetLeafName(rootPath));
    }

    /// <summary>
    /// 从 JDK release 文件文本中解析 JAVA_VERSION 的值（支持带/不带引号）。
    /// </summary>
    /// <param name="content">release 文件全文。</param>
    /// <returns>版本号；未匹配或为空时返回 null。</returns>
    public static string? ParseJavaReleaseVersion(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return null;
        }

        var match = JavaReleaseRegex().Match(content);
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups["v"].Value;
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// 解析 Go VERSION 文件文本：取首个非空行，去掉前缀 go（go1.23.0 → 1.23.0）。
    /// </summary>
    /// <param name="content">VERSION 文件全文。</param>
    /// <returns>版本号；无有效首行时返回 null。</returns>
    public static string? ParseGoVersion(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return null;
        }

        // 取首个非空行，避免文件以空行或 BOM 开头时误判。
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            // 去掉 go 前缀（大小写不敏感）。
            if (line.StartsWith("go", StringComparison.OrdinalIgnoreCase))
            {
                line = line[2..];
            }

            return line.Length == 0 ? null : line;
        }

        return null;
    }

    /// <summary>
    /// 从目录名解析版本号，规则按 SDK 类型区分。
    /// </summary>
    /// <param name="type">SDK 类型。</param>
    /// <param name="directoryName">目录叶子名（非完整路径）。</param>
    /// <returns>版本号；无法解析时返回 null。</returns>
    public static string? ParseVersionFromDirectoryName(SdkType type, string? directoryName)
    {
        if (string.IsNullOrWhiteSpace(directoryName))
        {
            return null;
        }

        // Java 优先匹配 jdk 后的版本，规避 zulu17.50.19 这类发行版内部编号干扰。
        if (type == SdkType.Java)
        {
            var jdkMatch = JdkInNameRegex().Match(directoryName);
            if (jdkMatch.Success)
            {
                return jdkMatch.Groups["v"].Value;
            }
        }

        // Go 目录名形如 go1.23.0：先尝试去 go 前缀再匹配通用版本。
        // 其余类型（Maven apache-maven-X.Y.Z、Node node-vX.Y.Z）直接取首个通用版本 token。
        var generic = GenericVersionRegex().Match(directoryName);
        return generic.Success ? generic.Groups["v"].Value : null;
    }

    /// <summary>
    /// 读取文件全文；文件不存在、无权限或任何 IO 异常时返回 null，不抛出。
    /// </summary>
    private static string? TryReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // NOTE: 解析器对外承诺“绝不抛”，IO 类异常一律降级为不可解析。
            return null;
        }
    }

    /// <summary>
    /// 取路径的叶子目录名，去除尾部分隔符。
    /// </summary>
    private static string GetLeafName(string rootPath)
    {
        try
        {
            return Path.GetFileName(Path.TrimEndingDirectorySeparator(rootPath)) ?? string.Empty;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }
}
