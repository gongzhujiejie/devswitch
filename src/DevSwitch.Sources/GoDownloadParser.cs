// 文件用途：解析 go.dev/dl/?mode=json 为统一版本模型。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.Text.Json、DevSwitch.Core
// NOTE: 合法授权学习使用，仅限本地环境。

using System.Text.Json;
using DevSwitch.Core;

namespace DevSwitch.Sources;

/// <summary>
/// Go 官方下载索引（?mode=json）解析器。
/// </summary>
/// <remarks>
/// 输入格式为数组，每项形如：
/// <c>{ "version": "go1.22.5", "stable": true, "files": [ { "filename": "...", "os": "windows",
/// "arch": "amd64", "kind": "archive", "sha256": "..." } ] }</c>。
/// 仅产出 Windows archive（zip）安装包；amd64 映射 x64、arm64 映射 Arm64。
/// 下载地址按官方约定：<c>https://go.dev/dl/{filename}</c>。
/// </remarks>
public static class GoDownloadParser
{
    private const string DistributionId = "go";
    private const string DownloadBase = "https://go.dev/dl/";

    /// <summary>
    /// 解析 Go 下载索引文本。
    /// </summary>
    /// <param name="json">?mode=json 响应文本。</param>
    /// <param name="architecture">期望架构；<see cref="SdkArchitecture.Any"/> 返回全部 Windows archive。</param>
    /// <returns>解析出的版本列表，未排序；非法条目被跳过。</returns>
    public static IReadOnlyList<SdkSourceVersion> Parse(string json, SdkArchitecture architecture = SdkArchitecture.Any)
    {
        var result = new List<SdkSourceVersion>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return result;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                ParseRelease(release, architecture, result);
            }
        }

        return result;
    }

    /// <summary>
    /// 解析单个发布对象，遍历其 files 数组提取 Windows archive。
    /// </summary>
    private static void ParseRelease(JsonElement release, SdkArchitecture architecture, List<SdkSourceVersion> result)
    {
        if (!release.TryGetProperty("version", out var versionElement)
            || versionElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var rawVersion = versionElement.GetString();
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return;
        }

        // 规范化：去掉前导 "go"，保留 1.22.5 形式。
        var version = rawVersion.StartsWith("go", StringComparison.OrdinalIgnoreCase)
            ? rawVersion[2..]
            : rawVersion;

        if (!release.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            TryAddFile(file, version, architecture, result);
        }
    }

    /// <summary>
    /// 判断单个 file 是否为目标 Windows archive，并追加到结果。
    /// </summary>
    private static void TryAddFile(
        JsonElement file,
        string version,
        SdkArchitecture architecture,
        List<SdkSourceVersion> result)
    {
        // 只接受 windows + archive（zip）；installer/source 等忽略。
        if (!StringEquals(file, "os", "windows") || !StringEquals(file, "kind", "archive"))
        {
            return;
        }

        // arch 映射：amd64->X64、arm64->Arm64；其它（386 等）忽略。
        var rawArch = ReadString(file, "arch");
        var arch = rawArch switch
        {
            "amd64" => SdkArchitecture.X64,
            "arm64" => SdkArchitecture.Arm64,
            _ => SdkArchitecture.Unknown,
        };

        if (arch == SdkArchitecture.Unknown)
        {
            return;
        }

        // 架构过滤。
        if (architecture != SdkArchitecture.Any && architecture != arch)
        {
            return;
        }

        var filename = ReadString(file, "filename");
        if (string.IsNullOrWhiteSpace(filename))
        {
            return;
        }

        // sha256 由官方内联提供（可能缺失）。
        var sha = ReadString(file, "sha256");
        var archTag = arch == SdkArchitecture.Arm64 ? "arm64" : "x64";

        result.Add(new SdkSourceVersion(
            SdkType: SdkType.Go,
            Version: version,
            Distribution: DistributionId,
            Architecture: arch,
            DownloadUrl: DownloadBase + filename,
            Sha256: string.IsNullOrWhiteSpace(sha) ? null : sha,
            ChecksumUrl: null,
            ReleaseDate: null,
            DisplayName: $"Go {version} ({archTag})"));
    }

    /// <summary>
    /// 读取对象的字符串属性，缺失返回 null。
    /// </summary>
    private static string? ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    /// <summary>
    /// 比较对象字符串属性是否等于期望值（忽略大小写）。
    /// </summary>
    private static bool StringEquals(JsonElement element, string name, string expected)
    {
        var value = ReadString(element, name);
        return value is not null && value.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}
