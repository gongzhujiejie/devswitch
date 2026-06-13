// 文件用途：守护 DevSwitch 非首屏导航页，防止 x:Load 延迟实例化导致点击闪退回归。
// 创建/修改日期：2026-06-13
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit
// NOTE: 合法授权学习使用，仅限本地环境。本测试只读取 XAML/C# 源码，不启动 WinUI 窗口。

using System.Text.RegularExpressions;
using Xunit;

namespace DevSwitch.Tests;

public sealed class DeferredNavigationRegressionTests
{
    public static TheoryData<string> CrashSensitiveContentNames => new()
    {
        "SettingsContent",
        "DoctorContent",
        "ProfilesContent",
        "LogsContent",
    };

    public static TheoryData<string> CrashSensitiveClickHandlers => new()
    {
        "private void OnProfilesNavClick",
        "private async void OnDoctorNavClick",
        "private void OnLogsNavClick",
        "private void OnSettingsNavClick",
    };

    [Theory]
    [MemberData(nameof(CrashSensitiveContentNames))]
    public void CrashSensitiveNavigationPagesAreNotXLoadDeferred(string elementName)
    {
        // 根因守护：这四个页都可由用户直接点击。WinUI 首访 XAML 懒实例化异常会从 Click handler 冒泡，
        // 造成“卡一下然后退出”。它们的控件树应随主窗口创建；重 IO/业务刷新留在 Loaded/导航后异步执行。
        string xaml = ReadRepoFile("src", "DevSwitch.App", "MainWindow.xaml");
        string element = ExtractStartElement(xaml, elementName);

        Assert.DoesNotContain("x:Load=\"False\"", element);
    }

    [Fact]
    public void EnsureDeferredContentNoLongerDependsOnFindNameForCrashSensitivePages()
    {
        // 根因守护：四个直接点击页不应依赖 RootGrid.FindName 激活 x:Load=False。
        // 即使保留方法给未来低风险区域使用，也不能再把 FindName 当作这些页的首访加载机制。
        string source = ReadRepoFile("src", "DevSwitch.App", "MainWindow.xaml.cs")
            + "\n"
            + ReadRepoFile("src", "DevSwitch.App", "MainWindow.Operations.cs");

        foreach (var contentName in CrashSensitiveContentNames)
        {
            Assert.DoesNotContain($"EnsureDeferredContent<", ExtractShowMethodForContent(source, contentName));
        }
    }

    [Theory]
    [MemberData(nameof(CrashSensitiveClickHandlers))]
    public void CrashSensitiveClickHandlersUseSafeNavigationBoundary(string methodName)
    {
        // 根因守护：导航 click handler 是 UI 线程入口，必须捕获同步/异步异常并恢复可用页面，不能让异常冒泡到进程级。
        string source = ReadRepoFile("src", "DevSwitch.App", "MainWindow.xaml.cs");
        string body = ExtractMethodBody(source, methodName);

        Assert.Contains("RunNavigationAction", body);
    }

    private static string ExtractShowMethodForContent(string source, string contentName)
    {
        string methodName = contentName switch
        {
            "SettingsContent" => "private void ShowSettingsContent",
            "DoctorContent" => "private void ShowDoctorContent",
            "ProfilesContent" => "private void ShowProfilesContent",
            "LogsContent" => "private void ShowLogsContent",
            _ => throw new ArgumentOutOfRangeException(nameof(contentName), contentName, null),
        };

        return ExtractMethodBody(source, methodName);
    }

    private static string ExtractStartElement(string xaml, string elementName)
    {
        // 只提取含 x:Name 的起始标签，避免跨越整段子树造成误判。
        var match = Regex.Match(
            xaml,
            $"<[^>]*x:Name=\\\"{Regex.Escape(elementName)}\\\"[^>]*>",
            RegexOptions.Singleline);
        Assert.True(match.Success, $"Element '{elementName}' should exist in MainWindow.xaml.");
        return match.Value;
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
