// 文件用途：拼接调用外部 DevSwitch.Updater.exe 的命令行参数字符串。
// 创建/修改日期：2026-06-10
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：仅 BCL（无第三方包）
// NOTE: 合法授权学习使用，仅限本地环境。本类为纯逻辑，仅做字符串拼接与参数校验，不启动任何进程。

using System.Text;

namespace DevSwitch.Core;

/// <summary>
/// updater 命令行参数构建器。
/// 生成形如：
/// <c>--source "&lt;sourceDir&gt;" --target "&lt;targetDir&gt;" --exe "&lt;exePath&gt;" --pid &lt;pid&gt; [--log "&lt;logPath&gt;"]</c>
/// 的参数串，由编排层交给 Process.Start 拉起独立更新进程。
/// </summary>
public static class UpdaterArgumentsBuilder
{
    /// <summary>
    /// 构建 updater 参数字符串。
    /// </summary>
    /// <param name="sourceDir">解压后的新版本源目录（待覆盖来源）。</param>
    /// <param name="targetDir">当前安装目录（覆盖目标）。</param>
    /// <param name="exePath">更新完成后需要重启的主程序路径。</param>
    /// <param name="pid">主程序进程 ID；updater 需等待其退出后再覆盖。必须为正数。</param>
    /// <param name="logPath">可选的 updater 日志输出路径；为空/空白则不附加 --log。</param>
    /// <returns>拼接好的命令行参数串；含空格的路径以双引号包裹。</returns>
    /// <exception cref="ArgumentException">sourceDir / targetDir / exePath 任一为空白时抛出。</exception>
    /// <exception cref="ArgumentOutOfRangeException">pid &lt;= 0 时抛出。</exception>
    public static string Build(
        string sourceDir,
        string targetDir,
        string exePath,
        int pid,
        string? logPath = null)
    {
        // 三个路径参数为硬性必填，任一空白即视为非法调用。
        if (string.IsNullOrWhiteSpace(sourceDir))
        {
            throw new ArgumentException("Source directory is required.", nameof(sourceDir));
        }

        if (string.IsNullOrWhiteSpace(targetDir))
        {
            throw new ArgumentException("Target directory is required.", nameof(targetDir));
        }

        if (string.IsNullOrWhiteSpace(exePath))
        {
            throw new ArgumentException("Executable path is required.", nameof(exePath));
        }

        // pid 必须为正整数（updater 据此等待主进程退出）。
        if (pid <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pid), pid, "Process id must be positive.");
        }

        var builder = new StringBuilder();

        // 按固定顺序拼接：--source / --target / --exe / --pid / [--log]。
        builder.Append("--source ").Append(Quote(sourceDir));
        builder.Append(" --target ").Append(Quote(targetDir));
        builder.Append(" --exe ").Append(Quote(exePath));
        builder.Append(" --pid ").Append(pid);

        // 仅在提供了非空日志路径时追加 --log。
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            builder.Append(" --log ").Append(Quote(logPath));
        }

        return builder.ToString();
    }

    // 为路径加引号：始终用双引号包裹，确保含空格的路径被命令行正确识别为单个参数。
    // NOTE: 统一加引号比「仅含空格才加」更稳妥，可避免漏判及跨壳差异。
    private static string Quote(string value) => $"\"{value}\"";
}
