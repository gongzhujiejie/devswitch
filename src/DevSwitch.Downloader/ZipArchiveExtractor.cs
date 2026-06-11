// 文件用途：定义解压抽象接口与基于 System.IO.Compression 的流式 zip 解压实现。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.IO.Compression、System.IO
// NOTE: 合法授权学习使用，仅限本地环境。解压前做 zip-slip 路径穿越防护，流式写出避免 OOM。

using System.IO.Compression;

namespace DevSwitch.Downloader;

/// <summary>
/// 压缩包解压抽象。下载完成流程在校验通过后调用它解压到目标目录。
/// 抽象出接口以便流程编排可被替换实现（例如未来支持 tar.gz）和单元测试注入假实现。
/// </summary>
public interface IArchiveExtractor
{
    /// <summary>
    /// 把压缩包解压到目标目录。
    /// </summary>
    /// <param name="archivePath">压缩包路径。</param>
    /// <param name="destinationDirectory">解压目标目录，不存在则创建。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task ExtractAsync(string archivePath, string destinationDirectory, CancellationToken cancellationToken = default);
}

/// <summary>
/// 基于 System.IO.Compression.ZipArchive 的流式 zip 解压器。
/// 逐条目流式写出，并在写出前校验目标路径必须落在目标目录内，防止 zip-slip 路径穿越攻击。
/// </summary>
public sealed class ZipArchiveExtractor : IArchiveExtractor
{
    /// <inheritdoc />
    public async Task ExtractAsync(
        string archivePath,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            throw new ArgumentException("Archive path is required.", nameof(archivePath));
        }

        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw new ArgumentException("Destination directory is required.", nameof(destinationDirectory));
        }

        Directory.CreateDirectory(destinationDirectory);

        // 计算目标根目录的规范全路径并以分隔符结尾，作为 zip-slip 边界判断基准。
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        var destinationPrefix = EnsureTrailingSeparator(destinationRoot);

        // NOTE: 以只读异步流方式打开 zip，避免把整个压缩包读入内存。
        var archiveStream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (archiveStream.ConfigureAwait(false))
        {
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 把条目相对路径解析为目标全路径，再校验是否仍在目标根目录内。
                var targetPath = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName));

                // 目录条目（FullName 以 / 结尾，Name 为空）单独处理，确保空目录也能创建。
                var isDirectoryEntry = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');

                // zip-slip 防护：解析后的路径必须等于根目录或位于根目录之下，否则拒绝整包解压。
                var withinRoot = string.Equals(
                                     EnsureTrailingSeparator(targetPath),
                                     destinationPrefix,
                                     StringComparison.OrdinalIgnoreCase)
                                 || targetPath.StartsWith(destinationPrefix, StringComparison.OrdinalIgnoreCase);

                if (!withinRoot)
                {
                    throw new IOException(
                        $"Zip entry '{entry.FullName}' escapes the destination directory and was rejected.");
                }

                if (isDirectoryEntry)
                {
                    Directory.CreateDirectory(targetPath);
                    continue;
                }

                // 普通文件条目：确保父目录存在，然后流式拷贝条目内容到目标文件。
                var parentDirectory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(parentDirectory))
                {
                    Directory.CreateDirectory(parentDirectory);
                }

                var entryStream = entry.Open();
                await using (entryStream.ConfigureAwait(false))
                {
                    var outputStream = new FileStream(
                        targetPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 81920,
                        options: FileOptions.Asynchronous);
                    await using (outputStream.ConfigureAwait(false))
                    {
                        // 流式拷贝，固定缓冲区，避免大文件占用过多内存。
                        // CopyToAsync 内部已用 ArrayPool 租借缓冲区，无需额外手工池化。
                        await entryStream.CopyToAsync(outputStream, 81920, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
    }

    /// <summary>
    /// 确保路径以目录分隔符结尾，便于前缀匹配判断包含关系。
    /// </summary>
    private static string EnsureTrailingSeparator(string path)
    {
        if (path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar))
        {
            return path;
        }

        return path + Path.DirectorySeparatorChar;
    }
}
