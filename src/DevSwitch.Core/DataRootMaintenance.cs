// 文件用途：跨项目复用的"目录清理"工具。当前主要用例是清空 dataRoot\updates 暂存目录。
// 创建/修改日期：2026-06-11
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.IO
// NOTE: 合法授权学习使用，仅限本地环境。

namespace DevSwitch.Core;

/// <summary>
/// 数据根目录的清理工具集。所有方法都是静态、纯文件系统操作，不依赖任何 WinUI / 业务服务。
/// </summary>
/// <remarks>
/// 设计目的：自更新流程在 dataRoot\updates\&lt;version&gt;\ 下落盘 zip + 解压结果，
/// 用于覆盖完成后由外部 updater 复制到安装目录。覆盖完成、主程序重启之后这些文件不再有用。
/// 把清理逻辑放在 Core，便于 App 层启动入口与自更新服务复用同一份代码、且能直接被 net8.0 测试覆盖。
/// </remarks>
public static class DataRootMaintenance
{
    /// <summary>
    /// updates 暂存目录名（位于 dataRoot 直接子目录）。
    /// </summary>
    public const string UpdatesDirectoryName = "updates";

    /// <summary>
    /// 清空 <paramref name="dataRoot"/>\updates 下所有历史更新暂存（保留 updates 目录本身）。
    /// </summary>
    /// <param name="dataRoot">DevSwitch 数据根目录绝对路径。空 / 空白 / 不存在时静默返回。</param>
    /// <remarks>
    /// 通常在应用启动入口调用：此时 updater 必然已退出（否则主程序也未启动），
    /// 历史所有版本的更新包都可一次性清掉，磁盘占用稳定为 0。
    ///
    /// 容错：目录不存在静默返回；单个文件/子目录占用失败跳过、不抛异常，
    /// 绝不因清理失败影响应用启动。
    /// </remarks>
    public static void PurgeUpdatesStagingDirectory(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            return;
        }

        string updatesRoot = Path.Combine(dataRoot, UpdatesDirectoryName);
        PurgeDirectoryContents(updatesRoot);
    }

    /// <summary>
    /// 清空指定目录下的所有内容（文件与子目录），但保留目录本身。
    /// </summary>
    /// <param name="directory">要清空的目录绝对路径。空 / 空白 / 不存在时静默返回。</param>
    /// <remarks>
    /// 容错：逐项删除，单项失败（占用 / 权限）只跳过该项、不抛异常，
    /// 确保清理尽力而为，不影响调用方主流程。
    /// </remarks>
    public static void PurgeDirectoryContents(string directory)
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
                // 单个文件删除失败（占用 / 权限）不影响整体清理，跳过即可。
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
