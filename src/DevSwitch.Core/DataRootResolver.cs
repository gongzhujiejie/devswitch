// 文件用途：解析 DevSwitch 数据根目录，封装默认目录与便携目录选择规则。
// 创建日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.IO

namespace DevSwitch.Core;

/// <summary>
/// 提供 DevSwitch 数据根目录解析能力。
/// </summary>
public static class DataRootResolver
{
    /// <summary>
    /// 解析数据根目录。
    /// </summary>
    /// <param name="appDirectory">DevSwitch 可执行文件所在目录。</param>
    /// <param name="localAppData">当前用户 LOCALAPPDATA 目录。</param>
    /// <returns>应使用的数据根目录。</returns>
    /// <exception cref="ArgumentException">路径参数为空时抛出。</exception>
    public static string Resolve(string appDirectory, string localAppData)
    {
        // NOTE: M0 先实现默认本地数据目录行为；便携 data 目录规则将在下一条测试驱动下补充。
        if (string.IsNullOrWhiteSpace(appDirectory))
        {
            throw new ArgumentException("App directory is required.", nameof(appDirectory));
        }

        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new ArgumentException("LOCALAPPDATA directory is required.", nameof(localAppData));
        }

        var portableDataDirectory = Path.Combine(appDirectory, "data");
        if (Directory.Exists(portableDataDirectory))
        {
            return portableDataDirectory;
        }

        return Path.Combine(localAppData, "DevSwitch");
    }

    /// <summary>
    /// 解析“生效”的数据根目录，作为 App 启动主入口。
    /// </summary>
    /// <remarks>
    /// 解析优先级：
    /// 1. appDirectory 下 dataroot.txt 记录了有效自定义路径 → 用自定义路径；
    /// 2. 否则默认用 appDirectory\data（绿色便携，默认选择，即使尚未创建也返回该路径）；
    /// 3. 仅当 appDirectory 不可写时，回退 localAppData\DevSwitch（C 盘兜底）。
    /// 与旧的 <see cref="Resolve"/> 行为解耦，避免破坏既有断言。
    /// </remarks>
    /// <param name="appDirectory">DevSwitch 可执行文件所在目录。</param>
    /// <param name="localAppData">当前用户 LOCALAPPDATA 目录。</param>
    /// <returns>应使用的数据根目录。</returns>
    /// <exception cref="ArgumentException">路径参数为空时抛出。</exception>
    public static string ResolveEffective(string appDirectory, string localAppData)
    {
        if (string.IsNullOrWhiteSpace(appDirectory))
        {
            throw new ArgumentException("App directory is required.", nameof(appDirectory));
        }

        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new ArgumentException("LOCALAPPDATA directory is required.", nameof(localAppData));
        }

        // 统一委托给按模式解析：读取引导配置后按所选模式落地。
        // 旧行为等价性：默认便携 / Custom 自定义路径 / 便携不可写回退，均由 ResolveByMode 覆盖。
        return ResolveByMode(DataRootBootstrap.ReadConfig(appDirectory), appDirectory, localAppData);
    }

    /// <summary>
    /// 按用户选择的「数据位置模式」解析数据根目录。
    /// </summary>
    /// <remarks>
    /// 各模式规则：
    /// <list type="bullet">
    /// <item>Portable → appDirectory\data；appDirectory 不可写时回退 localAppData\DevSwitch。</item>
    /// <item>LocalAppData → 固定 localAppData\DevSwitch（与 appDirectory 是否可写无关）。</item>
    /// <item>Custom → config.CustomPath；路径为空时回退 Portable 规则。</item>
    /// </list>
    /// </remarks>
    /// <param name="config">数据位置配置（模式 + 可选自定义路径）。</param>
    /// <param name="appDirectory">DevSwitch 可执行文件所在目录。</param>
    /// <param name="localAppData">当前用户 LOCALAPPDATA 目录。</param>
    /// <returns>应使用的数据根目录。</returns>
    /// <exception cref="ArgumentException">路径参数为空时抛出。</exception>
    public static string ResolveByMode(DataLocationConfig config, string appDirectory, string localAppData)
    {
        if (string.IsNullOrWhiteSpace(appDirectory))
        {
            throw new ArgumentException("App directory is required.", nameof(appDirectory));
        }

        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new ArgumentException("LOCALAPPDATA directory is required.", nameof(localAppData));
        }

        switch (config.Mode)
        {
            case DataLocationMode.LocalAppData:
                // 固定模式：始终 C 盘，移动工具目录不影响环境有效性。
                return Path.Combine(localAppData, "DevSwitch");

            case DataLocationMode.Custom when !string.IsNullOrWhiteSpace(config.CustomPath):
                // 自定义模式：直接使用用户指定的绝对路径。
                return config.CustomPath;

            case DataLocationMode.Custom:
            case DataLocationMode.Portable:
            default:
                // 便携模式（及 Custom 缺路径的安全回退）：appDirectory 可写用 data，否则回退 C 盘。
                return ResolvePortable(appDirectory, localAppData);
        }
    }

    /// <summary>
    /// 便携规则解析：appDirectory 可写返回同目录 data，否则回退 localAppData\DevSwitch。
    /// </summary>
    private static string ResolvePortable(string appDirectory, string localAppData)
    {
        if (IsDirectoryWritable(appDirectory))
        {
            return Path.Combine(appDirectory, "data");
        }

        return Path.Combine(localAppData, "DevSwitch");
    }

    /// <summary>
    /// 通过“创建并立即删除临时探测文件”判断目录是否可写。
    /// </summary>
    /// <param name="directory">待探测的目录。</param>
    /// <returns>可写返回 true，否则 false。</returns>
    private static bool IsDirectoryWritable(string directory)
    {
        // NOTE: 目录不存在也视为不可写，避免后续在不可控位置创建数据根。
        if (!Directory.Exists(directory))
        {
            return false;
        }

        // 用随机文件名降低与现有文件冲突概率。
        var probePath = Path.Combine(directory, $".devswitch-write-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            // 创建并立即关闭/删除，整个过程任何 IO/权限异常都判定为不可写。
            using (var stream = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }

            File.Delete(probePath);
            return true;
        }
        catch (Exception)
        {
            // 探测失败：尽量清理可能残留的探测文件，再判定为不可写。
            try
            {
                if (File.Exists(probePath))
                {
                    File.Delete(probePath);
                }
            }
            catch
            {
                // 清理失败可忽略，不影响“不可写”结论。
            }

            return false;
        }
    }
}
