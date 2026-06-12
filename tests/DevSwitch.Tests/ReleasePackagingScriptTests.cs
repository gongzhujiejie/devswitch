// 文件用途：验证发布打包脚本包含启动性能相关的发布参数。
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
        var script = ReadPackagingScript();

        Assert.Contains("$restoreArgs = @(", script);
        Assert.Contains("'-r', 'win10-x64'", script);
        Assert.Contains("'-p:PublishReadyToRun=true'", script);
        Assert.Contains("'-p:UseRidGraph=true'", script);
    }

    [Fact]
    public void CiBuildPackagePublishesReadyToRunWithoutImplicitRestore()
    {
        // publish 阶段复用上一步 assets，避免隐式 restore 丢失 RID/R2R 参数导致本地和 CI 行为漂移。
        var script = ReadPackagingScript();

        Assert.Contains("'--no-restore'", script);
        Assert.Contains("'-p:PublishReadyToRun=true'", script);
        Assert.Contains("'--runtime', 'win10-x64'", script);
    }

    private static string ReadPackagingScript()
    {
        var scriptPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "scripts", "ci-build-package.ps1"));
        return File.ReadAllText(scriptPath);
    }
}
