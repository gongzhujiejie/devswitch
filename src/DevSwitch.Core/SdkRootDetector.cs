// 文件用途：识别用户选择的 SDK 根目录类型与可用状态。
// 创建日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.IO

namespace DevSwitch.Core;

/// <summary>
/// SDK 类型。
/// </summary>
public enum SdkType
{
    /// <summary>
    /// 未能识别出受支持的 SDK 类型。
    /// </summary>
    Unknown,

    /// <summary>
    /// Java 开发工具链，首个纵切只识别 JDK。
    /// </summary>
    Java,

    /// <summary>
    /// Apache Maven 构建工具链，通过 Windows 命令入口识别。
    /// </summary>
    Maven,

    /// <summary>
    /// Node.js 开发工具链，当前纵切识别 Windows zip 根目录。
    /// </summary>
    Node,

    /// <summary>
    /// Go 开发工具链，当前纵切识别 Windows Go 根目录。
    /// </summary>
    Go,
}

/// <summary>
/// SDK 根目录识别状态。
/// </summary>
public enum SdkStatus
{
    /// <summary>
    /// 根目录结构满足当前类型的最小可用条件。
    /// </summary>
    Usable,

    /// <summary>
    /// 根目录未匹配任何受支持 SDK 形态，但调用本身有效。
    /// </summary>
    Unavailable,
}

/// <summary>
/// SDK 根目录识别结果。
/// </summary>
/// <param name="Type">识别出的 SDK 类型。</param>
/// <param name="Status">识别出的可用状态。</param>
/// <param name="RootPath">用户选择的 SDK 根目录。</param>
public sealed record SdkDetectionResult(SdkType Type, SdkStatus Status, string RootPath);

/// <summary>
/// 提供 SDK 根目录识别能力。
/// </summary>
public static class SdkRootDetector
{
    /// <summary>
    /// 识别用户导入的 SDK 根目录。
    /// </summary>
    /// <param name="rootPath">用户选择的 SDK 根目录路径。</param>
    /// <returns>识别出的 SDK 类型、状态与根目录路径。</returns>
    /// <exception cref="ArgumentException">根目录路径为空时抛出。</exception>
    public static SdkDetectionResult Detect(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("SDK root path is required.", nameof(rootPath));
        }

        // NOTE: 当前 TDD 纵切保留 JDK 的最小根目录识别：release + java.exe + javac.exe。
        // 若用户误选 JDK 的 bin 目录，则先回退到父目录再判定，保持“只接受 SDK 根目录”的公开行为。
        var javaRootPath = GetJavaRootCandidate(rootPath);
        var releaseFile = Path.Combine(javaRootPath, "release");
        var javaExecutable = Path.Combine(javaRootPath, "bin", "java.exe");
        var javacExecutable = Path.Combine(javaRootPath, "bin", "javac.exe");
        var mavenCommand = Path.Combine(rootPath, "bin", "mvn.cmd");
        var nodeExecutable = Path.Combine(rootPath, "node.exe");
        var npmCommand = Path.Combine(rootPath, "npm.cmd");
        var npxCommand = Path.Combine(rootPath, "npx.cmd");

        if (File.Exists(releaseFile) && File.Exists(javaExecutable) && File.Exists(javacExecutable))
        {
            return new SdkDetectionResult(SdkType.Java, SdkStatus.Usable, javaRootPath);
        }

        if (File.Exists(mavenCommand))
        {
            return new SdkDetectionResult(SdkType.Maven, SdkStatus.Usable, rootPath);
        }

        // NOTE: Node.js Windows zip 解压后的根目录直接包含 node.exe、npm.cmd、npx.cmd。
        // 该判定只识别用户选择的 Node 根目录，不尝试兼容 Unix 风格 bin 子目录。
        if (File.Exists(nodeExecutable) && File.Exists(npmCommand) && File.Exists(npxCommand))
        {
            return new SdkDetectionResult(SdkType.Node, SdkStatus.Usable, rootPath);
        }

        // NOTE: Go Windows 发行包的 SDK 根目录通过 bin/go.exe 和 bin/gofmt.exe 识别。
        // 该最小判定覆盖用户导入 Go 根目录的公开行为，不递归探测子目录。
        var goExecutable = Path.Combine(rootPath, "bin", "go.exe");
        var gofmtExecutable = Path.Combine(rootPath, "bin", "gofmt.exe");

        if (File.Exists(goExecutable) && File.Exists(gofmtExecutable))
        {
            return new SdkDetectionResult(SdkType.Go, SdkStatus.Usable, rootPath);
        }

        // NOTE: 路径参数有效但目录结构不匹配时返回不可用状态，避免导入流程被异常打断。
        // 调用方可据此展示“不是受支持 SDK 根目录”的提示，同时仍保留用户选择的原始路径。
        return new SdkDetectionResult(SdkType.Unknown, SdkStatus.Unavailable, rootPath);
    }

    private static string GetJavaRootCandidate(string rootPath)
    {
        // NOTE: Windows JDK 的可执行文件位于 bin 子目录；误选 bin 时父目录才是 SDK 根目录。
        // 仅在目录名精确为 bin 且存在父目录时回退，避免影响 Maven、Node、Go 等既有根目录识别。
        var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(rootPath));
        var parentDirectory = Directory.GetParent(rootPath);

        if (string.Equals(directoryName, "bin", StringComparison.OrdinalIgnoreCase) && parentDirectory is not null)
        {
            return parentDirectory.FullName;
        }

        return rootPath;
    }
}
