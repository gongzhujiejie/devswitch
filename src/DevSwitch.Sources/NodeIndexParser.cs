// 文件用途：解析 nodejs.org/dist/index.json 为统一版本模型。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.Text.Json、DevSwitch.Core
// NOTE: 合法授权学习使用，仅限本地环境。

using System.Text.Json;
using DevSwitch.Core;

namespace DevSwitch.Sources;

/// <summary>
/// Node.js 官方分发索引（index.json）解析器。
/// </summary>
/// <remarks>
/// 输入格式为数组，每项形如：
/// <c>{ "version": "v22.11.0", "date": "2024-10-29", "files": ["win-x64-zip", "win-arm64-zip"], "lts": "Jod" }</c>。
/// 仅产出 Windows zip 安装包；下载地址按官方约定拼接：
/// <c>https://nodejs.org/dist/v{ver}/node-v{ver}-win-{arch}.zip</c>。
/// </remarks>
public static class NodeIndexParser
{
    private const string DistributionId = "nodejs";

    /// <summary>
    /// 解析 Node.js index.json 文本。
    /// </summary>
    /// <param name="json">index.json 文本。</param>
    /// <param name="architecture">期望架构；<see cref="SdkArchitecture.Any"/> 返回 x64 与 arm64 全部。</param>
    /// <returns>解析出的 Windows zip 版本列表，未排序；非法条目被跳过。</returns>
    public static IReadOnlyList<SdkSourceVersion> Parse(string json, SdkArchitecture architecture = SdkArchitecture.Any)
    {
        var result = new List<SdkSourceVersion>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        // 用 JsonDocument 一次性解析整个数组；条目级别用 try 包裹，保证单条损坏不影响整体。
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            // 整体非法 JSON：返回空列表而非抛出。
            return result;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                ParseEntry(item, architecture, result);
            }
        }

        return result;
    }

    /// <summary>
    /// 解析单个版本对象，向结果追加匹配架构的 zip 版本。
    /// </summary>
    private static void ParseEntry(JsonElement item, SdkArchitecture architecture, List<SdkSourceVersion> result)
    {
        // version 缺失或非字符串则跳过。
        if (!item.TryGetProperty("version", out var versionElement)
            || versionElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var rawVersion = versionElement.GetString();
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return;
        }

        // 规范化：去掉前导 'v'，保留 22.11.0 形式。
        var version = rawVersion.TrimStart('v', 'V');

        // files 数组缺失则无可下载产物。
        if (!item.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        // 收集 files 中包含的 Windows zip 标签，判断各架构是否提供。
        var hasWinX64 = false;
        var hasWinArm64 = false;
        foreach (var file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var tag = file.GetString();
            if (tag is null)
            {
                continue;
            }

            // 官方标签：win-x64-zip / win-arm64-zip。
            if (tag.Equals("win-x64-zip", StringComparison.OrdinalIgnoreCase))
            {
                hasWinX64 = true;
            }
            else if (tag.Equals("win-arm64-zip", StringComparison.OrdinalIgnoreCase))
            {
                hasWinArm64 = true;
            }
        }

        // 可选发布日期与 LTS 代号，仅用于展示名。
        var releaseDate = ReadDate(item);
        var ltsName = ReadLts(item);

        // 按目标架构产出对应条目。
        if ((architecture is SdkArchitecture.Any or SdkArchitecture.X64) && hasWinX64)
        {
            result.Add(BuildVersion(version, SdkArchitecture.X64, releaseDate, ltsName));
        }

        if ((architecture is SdkArchitecture.Any or SdkArchitecture.Arm64) && hasWinArm64)
        {
            result.Add(BuildVersion(version, SdkArchitecture.Arm64, releaseDate, ltsName));
        }
    }

    /// <summary>
    /// 按官方命名约定拼接下载地址并构造版本模型。
    /// </summary>
    private static SdkSourceVersion BuildVersion(
        string version,
        SdkArchitecture arch,
        DateTimeOffset? releaseDate,
        string? ltsName)
    {
        // arch 标签：x64 / arm64。
        var archTag = arch == SdkArchitecture.Arm64 ? "arm64" : "x64";
        var url = $"https://nodejs.org/dist/v{version}/node-v{version}-win-{archTag}.zip";

        // LTS 版本在显示名上追加代号，便于用户识别。
        var display = ltsName is null
            ? $"Node.js {version} ({archTag})"
            : $"Node.js {version} LTS {ltsName} ({archTag})";

        return new SdkSourceVersion(
            SdkType: SdkType.Node,
            Version: version,
            Distribution: DistributionId,
            Architecture: arch,
            DownloadUrl: url,
            Sha256: null,
            // Node 官方提供单独的 SHASUMS256.txt 校验文件。
            ChecksumUrl: $"https://nodejs.org/dist/v{version}/SHASUMS256.txt",
            ReleaseDate: releaseDate,
            DisplayName: display);
    }

    /// <summary>
    /// 读取可选的 date 字段（yyyy-MM-dd）。
    /// </summary>
    private static DateTimeOffset? ReadDate(JsonElement item)
    {
        if (item.TryGetProperty("date", out var dateElement)
            && dateElement.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(dateElement.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    /// <summary>
    /// 读取可选 lts 字段；可能为 false（非 LTS）或字符串代号。
    /// </summary>
    private static string? ReadLts(JsonElement item)
    {
        if (!item.TryGetProperty("lts", out var lts))
        {
            return null;
        }

        // 字符串代号即 LTS；布尔 false 表示非 LTS。
        return lts.ValueKind == JsonValueKind.String ? lts.GetString() : null;
    }
}
