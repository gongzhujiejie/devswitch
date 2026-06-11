// 文件用途：提供配置文件备份能力，把待迁移的旧配置复制到 dataRoot\backups\config\ 目录。
// 创建/修改日期：2026-06-10
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.IO
// NOTE: 合法授权学习使用，仅限本地环境。备份只做复制，永不删除或改写源文件。

namespace DevSwitch.Core;

/// <summary>
/// 负责把待迁移的配置文件复制到备份目录，保证迁移失败时旧配置可恢复。
/// </summary>
/// <remarks>
/// 备份命名规则：<c>&lt;filename&gt;.&lt;oldVersion&gt;.&lt;timestamp&gt;.bak</c>，
/// 例如 <c>settings.json.1.20260610T091500123Z.bak</c>。
/// 时间戳保证同一文件多次备份不互相覆盖。
/// </remarks>
public sealed class ConfigBackupService
{
    // NOTE: 备份统一落在 dataRoot\backups\config，符合 design.md 第5节「复制到 backups/config/」。
    private const string BackupRelativeDirectory = "backups";
    private const string BackupConfigSubDirectory = "config";

    /// <summary>
    /// 纯函数：根据数据根目录、源文件路径与旧版本号计算备份文件的目标绝对路径。
    /// </summary>
    /// <param name="dataRoot">DevSwitch 数据根目录。</param>
    /// <param name="sourceFilePath">待备份的源配置文件路径。</param>
    /// <param name="oldVersion">源文件当前的 schemaVersion，用于嵌入备份文件名。</param>
    /// <param name="timestampUtc">备份时间戳（UTC），由调用方传入以便可测。</param>
    /// <returns>备份文件的绝对路径，不产生任何 IO 副作用。</returns>
    /// <exception cref="ArgumentException">dataRoot 或 sourceFilePath 为空时抛出。</exception>
    public static string BuildBackupPath(string dataRoot, string sourceFilePath, int oldVersion, DateTimeOffset timestampUtc)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("Data root is required.", nameof(dataRoot));
        }

        if (string.IsNullOrWhiteSpace(sourceFilePath))
        {
            throw new ArgumentException("Source file path is required.", nameof(sourceFilePath));
        }

        // NOTE: 只取文件名，避免源路径层级污染备份目录结构。
        var fileName = Path.GetFileName(sourceFilePath);

        // NOTE: 时间戳采用 UTC 紧凑格式（含毫秒 + Z），既可排序又能避免文件名非法字符。
        var stamp = timestampUtc.ToUniversalTime().ToString("yyyyMMddTHHmmssfffZ");

        var backupDirectory = GetBackupDirectory(dataRoot);
        var backupFileName = $"{fileName}.{oldVersion}.{stamp}.bak";
        return Path.Combine(backupDirectory, backupFileName);
    }

    /// <summary>
    /// 计算备份目录绝对路径（dataRoot\backups\config）。
    /// </summary>
    public static string GetBackupDirectory(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("Data root is required.", nameof(dataRoot));
        }

        return Path.Combine(dataRoot, BackupRelativeDirectory, BackupConfigSubDirectory);
    }

    /// <summary>
    /// 把源配置文件复制到备份目录；复制后源文件保持不变。
    /// </summary>
    /// <param name="dataRoot">DevSwitch 数据根目录。</param>
    /// <param name="sourceFilePath">待备份的源配置文件路径，必须存在。</param>
    /// <param name="oldVersion">源文件当前 schemaVersion。</param>
    /// <param name="timestampUtc">备份时间戳。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>备份文件的绝对路径。</returns>
    /// <exception cref="FileNotFoundException">源文件不存在时抛出。</exception>
    public async Task<string> BackupAsync(
        string dataRoot,
        string sourceFilePath,
        int oldVersion,
        DateTimeOffset timestampUtc,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourceFilePath))
        {
            throw new FileNotFoundException("Backup source file does not exist.", sourceFilePath);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var backupDirectory = GetBackupDirectory(dataRoot);
        Directory.CreateDirectory(backupDirectory);

        var backupPath = BuildBackupPath(dataRoot, sourceFilePath, oldVersion, timestampUtc);

        // NOTE: 以流式 CopyTo 复制，确保备份内容与源文件完全一致；不用 File.Copy 以便走异步 IO 并支持取消。
        // 相比先 ReadAllBytes 成大字节数组再 WriteAllBytes，流式拷贝避免整文件驻留内存的中转开销。
        // FileOptions.Asynchronous 启用真正的异步 IO；备份只读源、只写目标，源文件保持不变。
        var readOptions = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };
        var writeOptions = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous,
        };
        await using (var source = new FileStream(sourceFilePath, readOptions))
        await using (var destination = new FileStream(backupPath, writeOptions))
        {
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return backupPath;
    }
}
