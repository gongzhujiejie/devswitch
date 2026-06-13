// 文件用途：用源码级守护测试约束 WinUI 主窗口启动路径，防止非首屏业务 IO 回退到启动期执行。
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
    public void NonHomeCrashSensitiveContentIsPreCreatedButCollapsed(string elementName)
    {
        // 这四个一级导航页可被用户直接点击。v0.2.11/v0.2.12 证明 x:Load=False + FindName 首访加载会在
        // 部分环境下返回 null 并触发 WinUI 未处理异常；因此控件树预创建，重 IO 仍放在 Loaded/导航后异步执行。
        string xaml = ReadRepoFile("src", "DevSwitch.App", "MainWindow.xaml");

        string pattern = $"<[^>]+x:Name=\\\"{Regex.Escape(elementName)}\\\"[^>]+Visibility=\\\"Collapsed\\\"";
        string element = Regex.Match(xaml, pattern, RegexOptions.Singleline).Value;

        Assert.False(string.IsNullOrWhiteSpace(element), $"{elementName} should remain present and collapsed in MainWindow.xaml.");
        Assert.DoesNotContain("x:Load=\"False\"", element);
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
