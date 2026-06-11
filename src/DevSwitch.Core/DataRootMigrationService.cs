// 文件用途：将旧数据根目录整体迁移（复制）到新数据根目录，支持绿色便携迁移与自定义目录切换。
// 创建/修改日期：2026-06-10
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.IO / System.Threading（仅 BCL）

namespace DevSwitch.Core;

/// <summary>
/// 数据根迁移结果。
/// </summary>
/// <param name="Success">迁移是否成功（含“无需迁移/无数据可迁”等良性场景）。</param>
/// <param name="SourceRoot">源数据根目录。</param>
/// <param name="TargetRoot">目标数据根目录。</param>
/// <param name="CopiedFiles">实际复制的文件数量。</param>
/// <param name="ErrorCode">失败错误码（成功时为 null）。</param>
/// <param name="Message">面向上层的可读信息。</param>
public sealed record DataRootMigrationResult(
    bool Success,
    string SourceRoot,
    string TargetRoot,
    int CopiedFiles,
    string? ErrorCode,
    string Message);

/// <summary>
/// 数据根迁移服务：把 sourceRoot 下全部内容递归复制到 targetRoot，绝不删除源。
/// </summary>
public sealed class DataRootMigrationService
{
    /// <summary>
    /// 异步迁移数据根。
    /// </summary>
    /// <param name="sourceRoot">源数据根目录。</param>
    /// <param name="targetRoot">目标数据根目录。</param>
    /// <param name="overwrite">目标已存在同名文件时是否覆盖，默认 true。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>迁移结果。</returns>
    public async Task<DataRootMigrationResult> MigrateAsync(
        string sourceRoot,
        string targetRoot,
        bool overwrite = true,
        CancellationToken cancellationToken = default)
    {
        // 入参校验：空路径视为参数错误，返回失败而非抛出，便于上层统一处理。
        if (string.IsNullOrWhiteSpace(sourceRoot))
        {
            return new DataRootMigrationResult(false, sourceRoot ?? string.Empty, targetRoot ?? string.Empty, 0,
                "INVALID_SOURCE", "Source root is required.");
        }

        if (string.IsNullOrWhiteSpace(targetRoot))
        {
            return new DataRootMigrationResult(false, sourceRoot, targetRoot ?? string.Empty, 0,
                "INVALID_TARGET", "Target root is required.");
        }

        try
        {
            // 规范化为绝对全路径，便于比较 source 与 target 是否同一目录。
            var normalizedSource = NormalizeFullPath(sourceRoot);
            var normalizedTarget = NormalizeFullPath(targetRoot);

            // source==target：无需迁移，返回良性成功结果。
            if (string.Equals(normalizedSource, normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                return new DataRootMigrationResult(true, sourceRoot, targetRoot, 0, null,
                    "Source and target are the same directory; nothing to migrate.");
            }

            // 源不存在：约定为“无数据可迁”，返回成功且 0 文件。
            if (!Directory.Exists(normalizedSource))
            {
                return new DataRootMigrationResult(true, sourceRoot, targetRoot, 0, null,
                    "Source root does not exist; nothing to migrate.");
            }

            // 递归复制整棵目录树，统计复制文件数。
            var copied = await CopyDirectoryAsync(normalizedSource, normalizedTarget, overwrite, cancellationToken)
                .ConfigureAwait(false);

            return new DataRootMigrationResult(true, sourceRoot, targetRoot, copied, null,
                $"Migrated {copied} file(s) from source to target. Source kept intact.");
        }
        catch (OperationCanceledException)
        {
            // 取消属于良性中断，明确返回失败并标注 CANCELLED，不向上抛。
            return new DataRootMigrationResult(false, sourceRoot, targetRoot, 0,
                "CANCELLED", "Migration was cancelled.");
        }
        catch (Exception ex)
        {
            // NOTE: 不抛未捕获异常；将失败信息收敛到结果中由上层提示用户。
            return new DataRootMigrationResult(false, sourceRoot, targetRoot, 0,
                "MIGRATION_FAILED", $"Migration failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 递归复制目录树，流式复制文件以适配大文件，返回复制的文件总数。
    /// </summary>
    private static async Task<int> CopyDirectoryAsync(
        string sourceDir,
        string targetDir,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        // 确保目标目录存在。
        Directory.CreateDirectory(targetDir);
        var copiedFiles = 0;

        // 1) 复制当前层文件。
        foreach (var sourceFile in Directory.EnumerateFiles(sourceDir))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(sourceFile);
            var targetFile = Path.Combine(targetDir, fileName);

            // overwrite=false 且目标已存在时跳过该文件，避免覆盖用户既有数据。
            if (!overwrite && File.Exists(targetFile))
            {
                continue;
            }

            await CopyFileStreamAsync(sourceFile, targetFile, cancellationToken).ConfigureAwait(false);
            copiedFiles++;
        }

        // 2) 递归处理子目录。
        foreach (var subDir in Directory.EnumerateDirectories(sourceDir))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var subName = Path.GetFileName(subDir);
            var targetSubDir = Path.Combine(targetDir, subName);
            copiedFiles += await CopyDirectoryAsync(subDir, targetSubDir, overwrite, cancellationToken)
                .ConfigureAwait(false);
        }

        return copiedFiles;
    }

    /// <summary>
    /// 以流式方式复制单个文件，适配大文件，避免一次性读入内存。
    /// </summary>
    private static async Task CopyFileStreamAsync(string sourceFile, string targetFile, CancellationToken cancellationToken)
    {
        // 使用异步 FileStream，开启 useAsync 以获得真正的异步 IO。
        const int bufferSize = 81920; // 80KB，与 BCL Stream.CopyTo 默认缓冲一致。
        await using var source = new FileStream(
            sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
        await using var target = new FileStream(
            targetFile, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);

        await source.CopyToAsync(target, bufferSize, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 规范化全路径并去除末尾分隔符，便于目录相等性比较。
    /// </summary>
    private static string NormalizeFullPath(string path)
    {
        var full = Path.GetFullPath(path);
        // 去掉尾部分隔符，使 "C:\data" 与 "C:\data\" 视为同一目录。
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
