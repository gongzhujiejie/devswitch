// 文件用途：为 helper 进程协议测试提供统一启动、输入和 JSON 解析支撑。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit、System.Diagnostics、System.Text.Json
// NOTE: 合法授权学习使用，仅限本地环境。本文件只启动仓库构建产物，不访问外部系统。

using System.Diagnostics;
using System.Text.Json;
using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

internal sealed record HelperProcessResult(bool Exited, int ExitCode, string Output, string Error)
{
    /// <summary>
    /// 将 helper stdout 解析为 JsonDocument。
    /// 调用方负责释放返回的文档，避免测试长期持有非托管 JSON 缓冲区。
    /// </summary>
    public JsonDocument ParseOutputJson()
    {
        return JsonDocument.Parse(Output);
    }

    /// <summary>
    /// 将 helper stdout 反序列化为核心协议响应。
    /// </summary>
    public HelperResponse DeserializeResponse()
    {
        var response = JsonSerializer.Deserialize<HelperResponse>(Output, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });

        return response ?? throw new InvalidOperationException($"Helper returned invalid response: {Output}");
    }
}

internal static class HelperProcessTestSupport
{
    /// <summary>
    /// 通过真实 helper 进程处理一个协议请求。
    /// </summary>
    public static Task<HelperProcessResult> InvokeHelperAsync(HelperRequest request)
    {
        return InvokeHelperRawAsync(JsonSerializer.Serialize(request));
    }

    /// <summary>
    /// 通过真实 helper 进程处理原始 JSON 字符串，用于非法 JSON 等协议边界测试。
    /// </summary>
    public static async Task<HelperProcessResult> InvokeHelperRawAsync(string inputJson)
    {
        using var process = StartHelperProcess(LocateHelperExecutable());

        await process.StandardInput.WriteAsync(inputJson);
        await process.StandardInput.FlushAsync();
        process.StandardInput.Close();

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        var exited = process.WaitForExit(5000);

        return new HelperProcessResult(exited, process.ExitCode, output, error);
    }

    /// <summary>
    /// 在测试根目录下创建临时目录。
    /// </summary>
    public static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static Process StartHelperProcess(string helperPath)
    {
        // NOTE: CreateNoWindow 确保测试启动 helper 时不会弹出 cmd 窗口，贴近 GUI 调用方式。
        var startInfo = new ProcessStartInfo(helperPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start helper process.");
    }

    private static string LocateHelperExecutable()
    {
        // NOTE: 测试约定 helper 构建产物位于 artifacts/bin，scripts/build.ps1 会生成该文件。
        var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        var executableName = OperatingSystem.IsWindows() ? "DevSwitch.Helper.exe" : "DevSwitch.Helper";
        var helperPath = Path.Combine(repositoryRoot, "artifacts", "bin", executableName);

        Assert.True(File.Exists(helperPath), $"helper executable should exist at {helperPath}. Run scripts/build.ps1 first.");
        return helperPath;
    }

    private static string FindRepositoryRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DevSwitch.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root containing DevSwitch.sln.");
    }
}
