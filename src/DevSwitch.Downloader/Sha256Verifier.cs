// 文件用途：对下载完成的文件做流式 SHA256 计算与期望值比对。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.Security.Cryptography、System.IO、System.Buffers（ArrayPool，BCL 自带）
// NOTE: 合法授权学习使用，仅限本地环境。流式计算，杜绝大文件一次性读入内存导致 OOM。

using System.Buffers;
using System.Security.Cryptography;

namespace DevSwitch.Downloader;

/// <summary>
/// SHA256 校验结果。
/// </summary>
/// <param name="IsMatch">实际值是否与期望值一致。</param>
/// <param name="ActualSha256">实际计算出的 SHA256（十六进制小写）。</param>
/// <param name="ExpectedSha256">传入的期望 SHA256（已规范化为十六进制小写）。</param>
public readonly record struct Sha256VerificationResult(bool IsMatch, string ActualSha256, string ExpectedSha256);

/// <summary>
/// 流式 SHA256 校验器。对最终文件计算哈希并与期望值比对，避免一次性读入内存。
/// </summary>
public static class Sha256Verifier
{
    /// <summary>
    /// 流式计算文件的 SHA256（十六进制小写）。
    /// </summary>
    /// <param name="filePath">目标文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>十六进制小写的 SHA256 字符串。</returns>
    public static async Task<string> ComputeHexAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path is required.", nameof(filePath));
        }

        // NOTE: 使用异步顺序读取，IncrementalHash 逐块吃数据，内存占用恒定（一个缓冲区）。
        var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            // 从共享池租借缓冲区，避免每次校验都新分配 80KB 数组、减轻 GC 压力。
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                }
            }
            finally
            {
                // 归还缓冲区，不清零（哈希中间数据非敏感且立即覆盖），减少分配。
                ArrayPool<byte>.Shared.Return(buffer);
            }

            var digest = hash.GetHashAndReset();
            return Convert.ToHexString(digest).ToLowerInvariant();
        }
    }

    /// <summary>
    /// 计算文件 SHA256 并与期望值比对。
    /// </summary>
    /// <param name="filePath">目标文件路径。</param>
    /// <param name="expectedSha256">期望 SHA256，大小写与空白不敏感。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>校验结果，含实际值与是否匹配。</returns>
    public static async Task<Sha256VerificationResult> VerifyAsync(
        string filePath,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            throw new ArgumentException("Expected SHA256 is required.", nameof(expectedSha256));
        }

        // 规范化期望值：去掉空白并统一为小写十六进制，避免来源大小写不一致导致误判。
        var normalizedExpected = expectedSha256.Trim().ToLowerInvariant();
        var actual = await ComputeHexAsync(filePath, cancellationToken).ConfigureAwait(false);

        // 使用顺序无关、长度安全的比较；哈希为定长十六进制，普通比较已足够且不泄露时序敏感信息。
        var isMatch = string.Equals(actual, normalizedExpected, StringComparison.Ordinal);
        return new Sha256VerificationResult(isMatch, actual, normalizedExpected);
    }
}
