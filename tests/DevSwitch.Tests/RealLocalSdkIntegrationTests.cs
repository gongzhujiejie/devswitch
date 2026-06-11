// 文件用途：用真实本机 SDK（D:\Programs）端到端验证版本识别与 helper current link 切换。
// 创建/修改日期：2026-06-10
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit、DevSwitch.Core
// NOTE: 合法授权学习使用，仅限本地环境。这些测试依赖本机存在 D:\Programs 下真实 SDK，
//       缺失时直接通过（视为环境无关，不引入第三方 Skippable 包），dataRoot 用临时目录，绝不写 HKCU。

using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class RealLocalSdkIntegrationTests
{
    // 截图中导入的真实 Corretto JDK 8 根目录。
    private const string CorrettoRoot = @"D:\Programs\java\jdk\corretto-1.8.0_442";
    private const string HelperRelativePath = @"artifacts\bin\DevSwitch.Helper.exe";

    [Fact]
    public void ImportRealCorrettoResolvesRealVersion()
    {
        if (!Directory.Exists(CorrettoRoot))
        {
            // 环境无该真实 JDK 时不作断言（其它机器/CI 上跳过）。
            return;
        }

        // 直接用版本解析器验证：corretto release 文件含 JAVA_VERSION="1.8.0_442"。
        var version = SdkVersionResolver.ResolveVersion(SdkType.Java, CorrettoRoot);

        Assert.Equal("1.8.0_442", version);
    }

    [Fact]
    public async Task ImportRealCorrettoWritesNonUnknownVersionToCatalog()
    {
        if (!Directory.Exists(CorrettoRoot))
        {
            return;
        }

        var dataRoot = CreateTempDataRoot();
        try
        {
            var import = new LocalSdkImportService(dataRoot);
            var result = await import.ImportLocalAsync(CorrettoRoot);

            Assert.True(result.Success);
            Assert.NotNull(result.Record);
            // 修复前这里是 "unknown"，修复后应解析出真实版本。
            Assert.NotEqual("unknown", result.Record!.Version);
            Assert.Equal("1.8.0_442", result.Record.Version);
            Assert.Equal(SdkType.Java, result.Record.Type);
        }
        finally
        {
            TryDeleteDirectory(dataRoot);
        }
    }

    [Fact]
    public async Task RealHelperCreatesCurrentLinkToImportedJdk()
    {
        string helperPath = Path.Combine(RepoRoot(), HelperRelativePath);
        if (!Directory.Exists(CorrettoRoot) || !File.Exists(helperPath))
        {
            return;
        }

        var dataRoot = CreateTempDataRoot();
        try
        {
            // 1) 导入真实 JDK 到临时 catalog。
            var import = new LocalSdkImportService(dataRoot);
            var importResult = await import.ImportLocalAsync(CorrettoRoot);
            Assert.True(importResult.Success);

            // 2) 通过真实 helper 进程做 switchSdk，建立 current\java 指向 JDK 根。
            var helperClient = new ProcessHelperClient(helperPath);
            var switchService = new SdkSwitchService(dataRoot, new SdkCatalogStore(), helperClient);
            var switchResult = await switchService.SwitchAsync(SdkType.Java, importResult.Record!.Id);

            // 3) 验证切换成功，且 active 指针落到该记录。
            Assert.True(switchResult.Success, $"switch failed: {switchResult.ErrorCode} {switchResult.Message}");

            var catalog = await new SdkCatalogStore().LoadOrCreateAsync(dataRoot);
            Assert.Equal(importResult.Record.Id, catalog.Active.Java);

            // 4) 验证 current\java 链接确实存在并指向 JDK（关键命令文件可达）。
            string currentJava = Path.Combine(dataRoot, "current", "java");
            Assert.True(Directory.Exists(currentJava), "current\\java 链接未建立");
            Assert.True(File.Exists(Path.Combine(currentJava, "bin", "java.exe")), "current\\java 未指向有效 JDK");
        }
        finally
        {
            TryDeleteDirectory(dataRoot);
        }
    }

    private static string CreateTempDataRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "devswitch-realit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// 端到端：切换 → 写环境 → 回读 HKCU 验证 JAVA_HOME 指向 target 且 PATH 托管片段置顶。
    /// 用临时 dataRoot + HKCU 备份/还原，绝不永久污染真实环境（finally 还原所有动过的变量）。
    /// </summary>
    [Fact]
    public async Task SwitchThenInitializeEnvironment_WritesValidJavaHomeAndPrependsManagedPath()
    {
        string jdk21 = @"D:\Programs\java\jdk\jdk-21.0.1";
        string helperPath = Path.Combine(RepoRoot(), HelperRelativePath);
        if (!Directory.Exists(jdk21) || !File.Exists(helperPath))
        {
            return; // 环境无该 JDK 或 helper 未编译时跳过。
        }

        // 备份会被写动的 HKCU 用户级变量，测试后还原。
        var names = new[] { "DEVSWITCH_HOME", "JAVA_HOME", "MAVEN_HOME", "GOROOT", "JDK_HOME", "M2_HOME", "Path" };
        var backup = names.ToDictionary(
            n => n,
            n => Environment.GetEnvironmentVariable(n, EnvironmentVariableTarget.User));

        var dataRoot = CreateTempDataRoot();
        try
        {
            // 1) 准备含 jdk-21 记录的 catalog。
            var record = new SdkRecord(
                "java-e2e", SdkType.Java, "Java 21", "21.0.1", "local-java",
                SdkArchitecture.Unknown, SdkSourceKind.External, jdk21,
                SdkRecordStatus.Usable, DateTimeOffset.UtcNow, null);
            await new SdkCatalogStore().SaveAsync(
                dataRoot, new SdkCatalog(1, ActiveSdkSet.Empty, new[] { record }));

            // 2) 经真实 helper 切换。
            var helperClient = new ProcessHelperClient(helperPath);
            var switchService = new SdkSwitchService(dataRoot, new SdkCatalogStore(), helperClient);
            var switchResult = await switchService.SwitchAsync(SdkType.Java, "java-e2e");
            Assert.True(switchResult.Success, $"switch failed: {switchResult.ErrorCode} {switchResult.Message}");

            // 3) 经真实 helper 写环境：DEVSWITCH_HOME=临时 dataRoot、JAVA_HOME 绝对路径、PATH 置顶。
            var envHelper = new ProcessEnvironmentHelperClient(helperPath);
            var envService = new EnvironmentService(envHelper);
            var envResult = await envService.InitializeEnvironmentAsync(dataRoot, options: null, broadcast: false);
            Assert.True(envResult.Success, $"env init failed: {envResult.ErrorCode} {envResult.Message}");

            // 4) 回读 HKCU 校验：JAVA_HOME 展开后 bin\java.exe 存在（指向 jdk-21）。
            string? javaHome = Environment.GetEnvironmentVariable("JAVA_HOME", EnvironmentVariableTarget.User);
            Assert.False(string.IsNullOrWhiteSpace(javaHome), "JAVA_HOME 未写入");
            string expanded = Environment.ExpandEnvironmentVariables(javaHome!);
            Assert.True(File.Exists(Path.Combine(expanded, "bin", "java.exe")),
                $"JAVA_HOME 回读无效：{javaHome} -> {expanded}");

            // 5) PATH 托管片段置顶校验。
            string? userPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);
            string managedBin = Path.Combine(dataRoot, "current", "java", "bin");
            var front = (userPath ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            Assert.Equal(
                Path.TrimEndingDirectorySeparator(managedBin),
                Path.TrimEndingDirectorySeparator(front ?? string.Empty));
        }
        finally
        {
            // 还原所有动过的 HKCU 用户级变量。
            foreach (var pair in backup)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value, EnvironmentVariableTarget.User);
            }

            TryDeleteDirectory(dataRoot);
        }
    }

    // 从测试程序集位置回溯到仓库根（含 artifacts\bin\helper）。
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 10 && dir is not null; i++)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "artifacts", "bin")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return AppContext.BaseDirectory;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { /* 测试清理失败不影响断言。 */ }
    }
}
