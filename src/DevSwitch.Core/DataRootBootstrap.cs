// 文件用途：读写 DevSwitch 可执行文件同目录的引导配置文件 dataroot.txt，
//          记录用户选择的「数据位置模式」（便携/固定/自定义）及自定义路径。
//          向后兼容旧纯文本一行路径格式（绿色便携，不依赖数据根本身）。
// 创建/修改日期：2026-06-12
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.IO（仅 BCL）

namespace DevSwitch.Core;

/// <summary>
/// 引导配置：在 exe 同目录维护 dataroot.txt。
/// 新格式以 <c>mode=</c> 行声明数据位置模式；旧格式（纯路径）兼容为 Custom 模式。
/// </summary>
public static class DataRootBootstrap
{
    /// <summary>引导文件名，固定置于 appDirectory 下。</summary>
    public const string BootstrapFileName = "dataroot.txt";

    // 引导文件新格式键名约定（不区分大小写解析）。
    private const string ModeKeyPrefix = "mode=";
    private const string PathKeyPrefix = "path=";

    // 模式枚举对应的引导文件文本值（写入时使用，全小写）。
    private const string ModeValuePortable = "portable";
    private const string ModeValueLocalAppData = "localappdata";
    private const string ModeValueCustom = "custom";

    /// <summary>
    /// 读取数据位置配置。规则：
    /// 1. 文件缺失/读取异常 → 默认 <see cref="DataLocationConfig.Portable"/>；
    /// 2. 首行以 <c>mode=</c> 开头 → 按新格式解析模式（custom 时再读 path=）；
    /// 3. 首行不是 <c>mode=</c>（旧格式纯路径）→ 视为 Custom 模式 + 该路径。
    /// </summary>
    /// <param name="appDirectory">DevSwitch 可执行文件所在目录。</param>
    /// <returns>解析得到的配置；任何异常或无效内容回退便携默认。</returns>
    public static DataLocationConfig ReadConfig(string appDirectory)
    {
        // 入参非法时返回便携默认，保持读取稳健不抛。
        if (string.IsNullOrWhiteSpace(appDirectory))
        {
            return DataLocationConfig.Portable;
        }

        var bootstrapPath = Path.Combine(appDirectory, BootstrapFileName);
        try
        {
            if (!File.Exists(bootstrapPath))
            {
                return DataLocationConfig.Portable;
            }

            var raw = File.ReadAllText(bootstrapPath);
            return ParseConfig(raw);
        }
        catch (Exception)
        {
            // NOTE: 读取阶段任何 IO/解析异常都回退便携默认，避免启动期因配置损坏而崩溃。
            return DataLocationConfig.Portable;
        }
    }

    /// <summary>
    /// 写入数据位置配置：
    /// - Portable：删除引导文件（默认态，旧消费者也读到“无自定义”）；
    /// - LocalAppData：写 <c>mode=localappdata</c>；
    /// - Custom：写 <c>mode=custom</c> + <c>path=&lt;绝对路径&gt;</c>；路径为空时退化为 Portable（删除文件）。
    /// </summary>
    /// <param name="appDirectory">DevSwitch 可执行文件所在目录。</param>
    /// <param name="config">待持久化的数据位置配置。</param>
    /// <exception cref="ArgumentException">appDirectory 为空时抛出。</exception>
    /// <exception cref="IOException">写入或删除失败时抛出，供上层提示用户。</exception>
    public static void WriteConfig(string appDirectory, DataLocationConfig config)
    {
        if (string.IsNullOrWhiteSpace(appDirectory))
        {
            throw new ArgumentException("App directory is required.", nameof(appDirectory));
        }

        var bootstrapPath = Path.Combine(appDirectory, BootstrapFileName);

        // 规整 Custom 路径（去引号空白）；为空则等价于便携模式。
        var normalizedCustomPath = config.Mode == DataLocationMode.Custom
            ? NormalizePath(config.CustomPath)
            : null;

        // 计算落盘文本：Portable 或 Custom 无有效路径 → null 表示删除文件。
        string? content = config.Mode switch
        {
            DataLocationMode.LocalAppData => ModeKeyPrefix + ModeValueLocalAppData,
            DataLocationMode.Custom when normalizedCustomPath is not null =>
                ModeKeyPrefix + ModeValueCustom + "\n" + PathKeyPrefix + normalizedCustomPath,
            _ => null, // Portable，或 Custom 但路径无效，均回到默认态（删除文件）
        };

        try
        {
            if (content is null)
            {
                // 默认态：删除引导文件，使解析回到便携默认。
                if (File.Exists(bootstrapPath))
                {
                    File.Delete(bootstrapPath);
                }

                return;
            }

            // 防御性创建父目录（appDirectory 通常已存在）。
            Directory.CreateDirectory(appDirectory);
            File.WriteAllText(bootstrapPath, content);
        }
        catch (IOException)
        {
            // 直接向上抛 IOException，让上层给出明确的“无法保存数据目录配置”提示。
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            // 权限不足同样以 IOException 暴露，统一上层处理路径。
            throw new IOException($"Failed to write bootstrap file '{bootstrapPath}'.", ex);
        }
    }

    /// <summary>
    /// 读取自定义数据根（兼容旧接口）。仅当配置为 <see cref="DataLocationMode.Custom"/>
    /// 且路径有效时返回该路径；其它模式（含便携/固定/缺文件）返回 null（表示使用默认）。
    /// </summary>
    /// <param name="appDirectory">DevSwitch 可执行文件所在目录。</param>
    /// <returns>Custom 模式下清洗后的自定义路径；否则 null。</returns>
    public static string? ReadCustomDataRoot(string appDirectory)
    {
        // 复用 ReadConfig：保持“仅 Custom 模式返回路径”的对外语义不变。
        var config = ReadConfig(appDirectory);
        return config.Mode == DataLocationMode.Custom ? config.CustomPath : null;
    }

    /// <summary>
    /// 写入自定义数据根（兼容旧接口）。
    /// 传入非空路径等价于写入 Custom 模式；传入 null/空白等价于恢复 Portable 模式（删除 dataroot.txt）。
    /// </summary>
    /// <param name="appDirectory">DevSwitch 可执行文件所在目录。</param>
    /// <param name="customDataRoot">自定义数据根路径；null/空白表示清除并回到便携默认。</param>
    /// <exception cref="ArgumentException">appDirectory 为空时抛出。</exception>
    /// <exception cref="IOException">写入或删除失败时抛出，供上层提示用户。</exception>
    public static void WriteCustomDataRoot(string appDirectory, string? customDataRoot)
    {
        // 规整后为空 → Portable（删除文件）；否则 → Custom + 路径。委托给 WriteConfig 统一落盘。
        var normalized = NormalizePath(customDataRoot);
        var config = normalized is null
            ? DataLocationConfig.Portable
            : new DataLocationConfig(DataLocationMode.Custom, normalized);

        WriteConfig(appDirectory, config);
    }

    /// <summary>
    /// 解析引导文件原始文本为配置对象。
    /// </summary>
    /// <param name="raw">引导文件全文。</param>
    /// <returns>解析得到的配置；无法识别时回退便携默认。</returns>
    private static DataLocationConfig ParseConfig(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            // 空文件视为无配置 → 便携默认（保持旧 ReadCustomDataRoot 对空白返回 null 的等价语义）。
            return DataLocationConfig.Portable;
        }

        // 统一换行后逐行扫描；保留原始行用于读取首行判断与 path 取值。
        var lines = raw.Replace("\r\n", "\n").Split('\n');

        // 找到首个非空行，用于判断是新格式（mode=）还是旧格式（纯路径）。
        string? firstMeaningful = null;
        foreach (var line in lines)
        {
            if (line.Trim().Length > 0)
            {
                firstMeaningful = line.Trim();
                break;
            }
        }

        if (firstMeaningful is null)
        {
            return DataLocationConfig.Portable;
        }

        // 旧格式：首行不是 mode= 开头 → 当作 Custom 路径（向后兼容）。
        if (!firstMeaningful.StartsWith(ModeKeyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var legacyPath = NormalizePath(firstMeaningful);
            return legacyPath is null
                ? DataLocationConfig.Portable
                : new DataLocationConfig(DataLocationMode.Custom, legacyPath);
        }

        // 新格式：解析 mode 值。
        var modeValue = firstMeaningful[ModeKeyPrefix.Length..].Trim().ToLowerInvariant();
        switch (modeValue)
        {
            case ModeValueLocalAppData:
                return new DataLocationConfig(DataLocationMode.LocalAppData, null);

            case ModeValueCustom:
                // 在后续行中查找 path= 取值。
                var customPath = ExtractPathValue(lines);
                return customPath is null
                    ? DataLocationConfig.Portable // Custom 但缺路径 → 安全回退便携
                    : new DataLocationConfig(DataLocationMode.Custom, customPath);

            case ModeValuePortable:
            default:
                // portable 或未知模式值，均回退便携默认。
                return DataLocationConfig.Portable;
        }
    }

    /// <summary>
    /// 从引导文件各行中提取 <c>path=</c> 行的值并规整。
    /// </summary>
    /// <param name="lines">已按换行拆分的引导文件行。</param>
    /// <returns>规整后的路径；不存在或无效时返回 null。</returns>
    private static string? ExtractPathValue(string[] lines)
    {
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(PathKeyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return NormalizePath(trimmed[PathKeyPrefix.Length..]);
            }
        }

        return null;
    }

    /// <summary>
    /// 规范化路径文本：取首个非空行，去首尾空白并剥离成对/单边的引号。
    /// </summary>
    /// <param name="raw">原始文本（可能含换行、空白、引号）。</param>
    /// <returns>清洗后的路径；无有效内容时返回 null。</returns>
    private static string? NormalizePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // 仅取第一段有意义的行，忽略可能的多余行内容。
        var firstLine = raw.Replace("\r\n", "\n").Split('\n');
        string? candidate = null;
        foreach (var line in firstLine)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                candidate = trimmed;
                break;
            }
        }

        if (candidate is null)
        {
            return null;
        }

        // 去掉用户为带空格路径包裹的双引号或单引号。
        candidate = candidate.Trim('"', '\'').Trim();
        return candidate.Length == 0 ? null : candidate;
    }
}
