// 文件用途：验证 Sha256Verifier 流式计算与期望值比对（含大小写规范化）。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit、System.Security.Cryptography
// NOTE: 合法授权学习使用，仅限本地环境。用临时文件作为校验目标。

using System.Security.Cryptography;
using DevSwitch.Downloader;
using Xunit;

namespace DevSwitch.Tests;

public sealed class DownloaderSha256VerifierTests
{
    [Fact]
    public async Task ComputeHexMatchesReferenceImplementation()
    {
        // 与一次性 SHA256.HashData 的结果对比，确认流式计算正确。
        var path = await WriteTempFileAsync(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
        var expected = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant();

        var actual = await Sha256Verifier.ComputeHexAsync(path);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task VerifyReturnsMatchForCorrectHashIgnoringCase()
    {
        // 期望值大写、带空白也应匹配（内部规范化为小写）。
        var bytes = System.Text.Encoding.UTF8.GetBytes("devswitch");
        var path = await WriteTempFileAsync(bytes);
        var expected = Convert.ToHexString(SHA256.HashData(bytes)); // 默认大写

        var result = await Sha256Verifier.VerifyAsync(path, "  " + expected + "  ");

        Assert.True(result.IsMatch);
        Assert.Equal(expected.ToLowerInvariant(), result.ActualSha256);
    }

    [Fact]
    public async Task VerifyReturnsMismatchForWrongHash()
    {
        // 哈希不一致时 IsMatch=false，由上层据此标记 failed。
        var path = await WriteTempFileAsync(new byte[] { 42 });

        var result = await Sha256Verifier.VerifyAsync(path, new string('0', 64));

        Assert.False(result.IsMatch);
    }

    private static async Task<string> WriteTempFileAsync(byte[] content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "payload.bin");
        await File.WriteAllBytesAsync(path, content);
        return path;
    }
}
