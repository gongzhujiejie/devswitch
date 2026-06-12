// 文件用途：构造 SDK 目录在 Windows 资源管理器中的定位参数。
// 创建/修改日期：2026-06-12
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.IO
// NOTE: 合法授权学习使用，仅限本地环境。本类只构造参数，不启动 explorer.exe，便于单元测试。

namespace DevSwitch.Core;

/// <summary>
/// SDK 位置定位辅助逻辑。
/// </summary>
public static class SdkLocationExplorer
{
    /// <summary>
    /// 尝试把 SDK 路径转换为 explorer.exe 的 /select 参数。
    /// </summary>
    public static bool TryCreateSelectArguments(
        string? sdkPath,
        out string? fullPath,
        out string? arguments,
        out string? errorMessage)
    {
        fullPath = null;
        arguments = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(sdkPath))
        {
            errorMessage = "SDK path is empty.";
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(sdkPath.Trim().Trim('"')));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            errorMessage = ex.Message;
            return false;
        }

        if (!Directory.Exists(fullPath))
        {
            errorMessage = "SDK path does not exist.";
            return false;
        }

        arguments = $"/select,\"{fullPath}\"";
        return true;
    }
}
