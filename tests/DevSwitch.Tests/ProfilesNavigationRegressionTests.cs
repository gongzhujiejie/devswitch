// 文件用途：配置档案页首访崩溃回归测试，约束 WinUI x:Load=False 首次导航与异步加载时序。
// 创建/修改日期：2026-06-13
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit
// NOTE: 合法授权学习使用，仅限本地环境。本测试只读取 XAML/C# 源码，不启动 WinUI 窗口。

using Xunit;

namespace DevSwitch.Tests;

public sealed class ProfilesNavigationRegressionTests
{
    [Fact]
    public void ProfilesNavigationAttachesDeferredViewBeforeInitializingDataLoad()
    {
        // 根因守护：ProfilesContent 由 x:Load=False 延迟实例化，首次进入时必须先接入可见内容树，
        // 再启动数据加载；否则异常提示 ContentDialog 可能拿不到 XamlRoot，导致点击配置档案时未捕获异常退出。
        string source = ReadRepoFile("src", "DevSwitch.App", "MainWindow.xaml.cs");
        string methodBody = ExtractMethodBody(source, "private void ShowProfilesContent");

        int activateIndex = methodBody.IndexOf("SetActiveContent(profilesContent)", StringComparison.Ordinal);
        int initializeIndex = methodBody.IndexOf("profilesContent.Initialize(dataRoot)", StringComparison.Ordinal);

        Assert.True(activateIndex >= 0, "ShowProfilesContent should attach ProfilesContent to the active content tree.");
        Assert.True(initializeIndex >= 0, "ShowProfilesContent should initialize ProfilesContent on first navigation.");
        Assert.True(
            activateIndex < initializeIndex,
            "ProfilesContent must become active before Initialize starts async profile loading, so ContentDialog has a valid XamlRoot on failures.");
    }

    [Fact]
    public void ProfilesViewInitializeDoesNotStartRefreshBeforeLoaded()
    {
        // 根因守护：Initialize 只能注入 dataRoot 并尝试安全启动；不能直接 fire-and-forget RefreshAsync。
        // 直接启动会把磁盘错误路径提前到控件未 Loaded / XamlRoot 未就绪阶段，异常弹窗会再次抛错。
        string source = ReadRepoFile("src", "DevSwitch.App", "Views", "ProfilesView.xaml.cs");
        string methodBody = ExtractMethodBody(source, "public void Initialize");

        Assert.DoesNotContain("_ = RefreshAsync();", methodBody);
        Assert.Contains("StartInitialRefresh", methodBody);
    }

    [Fact]
    public void ProfilesViewQueuesUiUpdatesAfterAsyncProfileLoad()
    {
        // 根因守护：SdkProfileStore 内部使用 ConfigureAwait(false)，RefreshAsync 在 await 后不能直接改 WinUI 控件或 ObservableCollection。
        // 所有列表与空状态刷新必须经 DispatcherQueue.TryEnqueue 回到 UI 线程，避免 Release/R2R 首次点击 Profiles 崩溃。
        string source = ReadRepoFile("src", "DevSwitch.App", "Views", "ProfilesView.xaml.cs");
        string refreshBody = ExtractMethodBody(source, "private async Task RefreshAsync");

        Assert.Contains("DispatcherQueue.GetForCurrentThread", source);
        Assert.Contains("dispatcherQueue.TryEnqueue", refreshBody);
        Assert.True(
            refreshBody.IndexOf("dispatcherQueue.TryEnqueue", StringComparison.Ordinal) < refreshBody.IndexOf("_items.Clear()", StringComparison.Ordinal),
            "ProfilesView must enqueue UI collection updates before touching ObservableCollection after async IO.");
    }

    private static string ReadRepoFile(params string[] parts)
    {
        // 测试程序集位于 tests/DevSwitch.Tests/bin/<config>/<tfm>/，向上 5 层回到仓库根。
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return File.ReadAllText(Path.Combine(new[] { repoRoot }.Concat(parts).ToArray()));
    }

    private static string ExtractMethodBody(string source, string methodName)
    {
        // 使用简化的大括号匹配定位方法体；方法签名在源码中唯一，适合本类源码级守护测试。
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
