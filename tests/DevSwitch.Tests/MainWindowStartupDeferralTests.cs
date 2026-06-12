// 文件用途：用源码级守护测试约束 WinUI 主窗口启动路径，防止非首屏重控件回退到启动期创建。
// 创建/修改日期：2026-06-13
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit、System.Text.RegularExpressions
// NOTE: 合法授权学习使用，仅限本地环境。本测试只读取 XAML/C# 源码，不启动 WinUI 窗口。

using System.Text.RegularExpressions;
using Xunit;

namespace DevSwitch.Tests;

public sealed class MainWindowStartupDeferralTests
{
    [Theory]
    [InlineData("SettingsContent")]
    [InlineData("DoctorContent")]
    [InlineData("ProfilesContent")]
    [InlineData("LogsContent")]
    public void NonHomeHeavyContentUsesXLoadFalse(string elementName)
    {
        // 首屏只需要 HomeContent；其它页面若仅 Visibility=Collapsed，WinUI 仍会在 InitializeComponent 创建控件树。
        // x:Load=False 才能把设置页、诊断页、独立 UserControl 等成本推迟到首次导航时。
        string xaml = ReadRepoFile("src", "DevSwitch.App", "MainWindow.xaml");

        string pattern = $"<[^>]+x:Name=\\\"{Regex.Escape(elementName)}\\\"[^>]+x:Load=\\\"False\\\"";

        Assert.Matches(pattern, xaml);
    }

    [Fact]
    public void RootGridLoadedDoesNotAwaitEnvironmentDriftCorrection()
    {
        // 漂移校正可能创建 helper 进程并广播环境变量；它必须在首帧后后台运行，不能串行卡住 Loaded 初始化。
        string source = ReadRepoFile("src", "DevSwitch.App", "MainWindow.xaml.cs");
        string methodBody = ExtractMethodBody(source, "private async void OnRootGridLoaded");

        Assert.Contains("CorrectEnvironmentDriftAsync", methodBody);
        Assert.DoesNotContain("await CorrectEnvironmentDriftAsync", methodBody);
    }

    private static string ReadRepoFile(params string[] parts)
    {
        // 测试程序集位于 tests/DevSwitch.Tests/bin/<config>/<tfm>/，向上 5 层回到仓库根。
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return File.ReadAllText(Path.Combine(new[] { repoRoot }.Concat(parts).ToArray()));
    }

    private static string ExtractMethodBody(string source, string methodName)
    {
        // 使用简化的大括号匹配定位方法体；源码结构稳定，足以作为轻量守护测试。
        int methodIndex = source.IndexOf(methodName, StringComparison.Ordinal);
        Assert.True(methodIndex >= 0, $"Method '{methodName}' should exist.");

        int bodyStart = source.IndexOf('{', methodIndex);
        Assert.True(bodyStart >= 0, $"Method '{methodName}' should have a body.");

        int depth = 0;
        for (int i = bodyStart; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(bodyStart, i - bodyStart + 1);
                }
            }
        }

        throw new InvalidOperationException($"Method '{methodName}' body was not closed.");
    }
}
