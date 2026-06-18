// 文件用途：验证发布打包脚本包含启动性能与自包含部署相关的发布参数。
// 创建/修改日期：2026-06-13
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit
// NOTE: 合法授权学习使用，仅限本地环境。本测试只读取仓库脚本文本，不执行打包或联网。

using Xunit;

namespace DevSwitch.Tests;

public sealed class ReleasePackagingScriptTests
{
    [Fact]
    public void CiBuildPackageRestoresReadyToRunForWin10X64BeforePublish()
    {
        // ReadyToRun 依赖 RID 专用 assets；restore 阶段必须带同一 RID 与 PublishReadyToRun，
        // 否则 publish 会在 ResolveReadyToRunCompilers 阶段报 NETSDK1094。
        var restoreArgs = ReadPowerShellArray("$restoreArgs");

        Assert.Contains("'-r', 'win10-x64'", restoreArgs);
        Assert.Contains("'-p:PublishReadyToRun=true'", restoreArgs);
        Assert.Contains("'-p:UseRidGraph=true'", restoreArgs);
    }

    [Fact]
    public void CiBuildPackageRestoresSelfContainedRuntimeAssetsBeforePublish()
    {
        // .NET 自包含发布会在 restore 阶段解析 RID 专用 runtime pack；由于 publish 使用 --no-restore，
        // restore 阶段也必须显式带 SelfContained 与非单文件布局参数，避免 CI 复用到错误 assets。
        var restoreArgs = ReadPowerShellArray("$restoreArgs");

        Assert.Contains("'-p:SelfContained=true'", restoreArgs);
        Assert.Contains("'-p:PublishSingleFile=false'", restoreArgs);
        Assert.Contains("'-r', 'win10-x64'", restoreArgs);
    }

    [Fact]
    public void CiBuildPackagePublishesReadyToRunWithoutImplicitRestore()
    {
        // publish 阶段复用上一步 assets，避免隐式 restore 丢失 RID/R2R 参数导致本地和 CI 行为漂移。
        var publishArgs = ReadPowerShellArray("$publishArgs");

        Assert.Contains("'--no-restore'", publishArgs);
        Assert.Contains("'-p:PublishReadyToRun=true'", publishArgs);
        Assert.Contains("'--runtime', 'win10-x64'", publishArgs);
    }

    [Fact]
    public void CiBuildPackagePublishesSelfContainedNonSingleFilePackage()
    {
        // WinUI 非打包应用依赖 XBF、PRI、native DLL 与 helper/shim/updater 等 loose files；
        // 发布包应自带 .NET runtime，但必须保持目录式多文件布局。
        var publishArgs = ReadPowerShellArray(ReadCiPackagingScript(), "$publishArgs");

        Assert.Contains("'-p:SelfContained=true'", publishArgs);
        Assert.Contains("'-p:PublishSingleFile=false'", publishArgs);
        Assert.Contains("'--runtime', 'win10-x64'", publishArgs);
        Assert.Contains("'--no-restore'", publishArgs);
    }

    [Theory]
    [InlineData("scripts", "ci-build-package.ps1")]
    [InlineData("artifacts", "build-release-package.ps1")]
    public void ZipPackagingScriptsValidateSelfContainedRuntimeFilesBeforeCompressing(string directory, string fileName)
    {
        // 任何会压缩发布 zip 的脚本，都必须在 Compress-Archive 前确认 runtime 三件套存在；
        // 否则人工本地打包可能绕过 CI 防线，重新发布出依赖本机 .NET Desktop Runtime 的包。
        var script = ReadRepositoryFile(directory, fileName);
        var compressIndex = script.IndexOf("Compress-Archive", StringComparison.Ordinal);

        Assert.True(compressIndex >= 0, $"{fileName} 应创建发布 zip");
        AssertContainsBeforeCompress(script, "coreclr.dll", compressIndex, fileName);
        AssertContainsBeforeCompress(script, "hostfxr.dll", compressIndex, fileName);
        AssertContainsBeforeCompress(script, "hostpolicy.dll", compressIndex, fileName);
    }

    private static string ReadPowerShellArray(string variableName)
    {
        return ReadPowerShellArray(ReadCiPackagingScript(), variableName);
    }

    private static string ReadPowerShellArray(string script, string variableName)
    {
        var declaration = variableName + " = @(";
        var start = script.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"脚本中应声明 {declaration}");

        // 仅解析当前脚本中简单的 PowerShell 数组字面量：按括号深度寻找匹配的闭合括号。
        // 这样未来数组内出现带右括号的字符串或注释时，不会被第一个 ')' 误截断。
        var depth = 0;
        for (var index = start; index < script.Length; index++)
        {
            if (script[index] == '(')
            {
                depth++;
            }
            else if (script[index] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return script[start..(index + 1)];
                }
            }
        }

        throw new InvalidOperationException($"脚本中 {variableName} 数组应有结束括号");
    }

    private static void AssertContainsBeforeCompress(string script, string expected, int compressIndex, string fileName)
    {
        var index = script.IndexOf(expected, StringComparison.Ordinal);
        Assert.True(index >= 0 && index < compressIndex, $"{fileName} 应在 Compress-Archive 前检查 {expected}");
    }

    private static string ReadCiPackagingScript()
    {
        return ReadRepositoryFile("scripts", "ci-build-package.ps1");
    }

    private static string ReadRepositoryFile(params string[] segments)
    {
        var pathSegments = new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }
            .Concat(segments)
            .ToArray();
        var scriptPath = Path.GetFullPath(Path.Combine(pathSegments));
        return File.ReadAllText(scriptPath);
    }
}
