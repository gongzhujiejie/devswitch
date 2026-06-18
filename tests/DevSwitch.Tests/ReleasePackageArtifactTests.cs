// 文件用途：验证正式 Release zip 产物包含 .NET 自包含运行时与校验文件。
// 创建/修改日期：2026-06-13
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit、System.IO.Compression
// NOTE: 合法授权学习使用，仅限本地环境。本测试只读取本地构建产物，不联网、不启动应用。

using System.IO.Compression;
using System.Security.Cryptography;
using Xunit;

namespace DevSwitch.Tests;

public sealed class ReleasePackageArtifactTests
{
    private const string RequireArtifactsEnvironmentVariable = "DEVSWITCH_REQUIRE_RELEASE_ARTIFACTS";

    [Fact]
    public void ReleaseZipContainsSelfContainedRuntimeFilesWhenArtifactVerificationEnabled()
    {
        // 产物级验证成本比普通单测高：默认不要求 release zip 已存在；发布前由环境变量显式开启，
        // 这样普通 dotnet test 不会因为尚未打包而失败，同时发布验证能强制抓出非自包含包。
        if (!ShouldRequireReleaseArtifacts())
        {
            return;
        }

        var zipPath = GetReleaseZipPath();
        Assert.True(File.Exists(zipPath), $"发布包不存在：{zipPath}");

        using var archive = ZipFile.OpenRead(zipPath);
        var entries = archive.Entries.Select(e => e.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // .NET self-contained 多文件发布的核心运行时文件；缺失任意一个都会回退为依赖本机 runtime。
        Assert.Contains("coreclr.dll", entries);
        Assert.Contains("hostfxr.dll", entries);
        Assert.Contains("hostpolicy.dll", entries);

        // 应用与三个原生辅助程序必须仍随包存在，防止 self-contained 改造破坏原有目录布局。
        Assert.Contains("DevSwitch.App.exe", entries);
        Assert.Contains("DevSwitch.App.dll", entries);
        Assert.Contains("DevSwitch.App.deps.json", entries);
        Assert.Contains("DevSwitch.App.runtimeconfig.json", entries);
        Assert.Contains("DevSwitch.Helper.exe", entries);
        Assert.Contains("DevSwitch.Shim.exe", entries);
        Assert.Contains("DevSwitch.Updater.exe", entries);
    }

    [Fact]
    public void ReleaseZipSha256MatchesSidecarWhenArtifactVerificationEnabled()
    {
        // 校验 .sha256 可防止发布包重新压缩后旁路校验文件未同步，保证 Release 上传物一致。
        if (!ShouldRequireReleaseArtifacts())
        {
            return;
        }

        var zipPath = GetReleaseZipPath();
        var shaPath = zipPath + ".sha256";
        Assert.True(File.Exists(zipPath), $"发布包不存在：{zipPath}");
        Assert.True(File.Exists(shaPath), $"校验文件不存在：{shaPath}");

        var expected = File.ReadAllText(shaPath).Trim().ToLowerInvariant();
        using var stream = File.OpenRead(zipPath);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

        Assert.Equal(expected, actual);
    }

    private static bool ShouldRequireReleaseArtifacts()
    {
        var value = Environment.GetEnvironmentVariable(RequireArtifactsEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetReleaseZipPath()
    {
        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "artifacts", "release", "DevSwitch-win10-x64.zip"));
    }
}
