// 文件用途：解析 Adoptium API v3 assets 风格 JSON 为统一版本模型（Temurin/Zulu/Corretto 同构）。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.Text.Json、DevSwitch.Core
// NOTE: 合法授权学习使用，仅限本地环境。

using System.Text.Json;
using DevSwitch.Core;

namespace DevSwitch.Sources;

/// <summary>
/// Adoptium（Eclipse Temurin）API v3 assets 解析器。
/// </summary>
/// <remarks>
/// 输入格式为数组，每项形如：
/// <code>
/// {
///   "version_data": { "semver": "21.0.4+7" },
///   "binaries": [
///     {
///       "os": "windows",
///       "architecture": "x64",
///       "image_type": "jdk",
///       "package": {
///         "link": "https://.../OpenJDK21U-jdk_x64_windows_hotspot_21.0.4_7.zip",
///         "checksum": "abcdef...",
///         "name": "OpenJDK21U-jdk_x64_windows_hotspot_21.0.4_7.zip"
///       }
///     }
///   ]
/// }
/// </code>
/// 仅产出 os=windows 且 image_type=jdk 的二进制；x64->X64、aarch64/arm64->Arm64。
/// 由于结构与 Zulu/Corretto 的 Adoptium 兼容端点一致，<paramref name="distribution"/> 可定制。
/// </remarks>
public static class TemurinAssetsParser
{
    /// <summary>
    /// 默认发行版标识（Eclipse Temurin）。
    /// </summary>
    public const string DefaultDistribution = "temurin";

    /// <summary>
    /// 解析 Adoptium assets JSON 文本。
    /// </summary>
    /// <param name="json">assets 数组 JSON 文本。</param>
    /// <param name="architecture">期望架构；<see cref="SdkArchitecture.Any"/> 返回全部 Windows JDK。</param>
    /// <param name="distribution">发行版标识，默认 temurin，可传 zulu/corretto。</param>
    /// <returns>解析出的版本列表，未排序；非法条目被跳过。</returns>
    public static IReadOnlyList<SdkSourceVersion> Parse(
        string json,
        SdkArchitecture architecture = SdkArchitecture.Any,
        string distribution = DefaultDistribution)
    {
        var result = new List<SdkSourceVersion>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        var dist = string.IsNullOrWhiteSpace(distribution) ? DefaultDistribution : distribution;

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

            foreach (var asset in document.RootElement.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                ParseAsset(asset, architecture, dist, result);
            }
        }

        return result;
    }

    /// <summary>
    /// 解析单个 asset：取 semver，遍历 binaries 提取 Windows JDK。
    /// </summary>
    private static void ParseAsset(
        JsonElement asset,
        SdkArchitecture architecture,
        string distribution,
        List<SdkSourceVersion> result)
    {
        // version_data.semver 是规范版本号，缺失则跳过整个 asset。
        if (!asset.TryGetProperty("version_data", out var versionData)
            || versionData.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var semver = ReadString(versionData, "semver");
        if (string.IsNullOrWhiteSpace(semver))
        {
            return;
        }

        if (!asset.TryGetProperty("binaries", out var binaries) || binaries.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var binary in binaries.EnumerateArray())
        {
            if (binary.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            TryAddBinary(binary, semver!, architecture, distribution, result);
        }
    }

    /// <summary>
    /// 判断单个 binary 是否为目标 Windows JDK，并追加到结果。
    /// </summary>
    private static void TryAddBinary(
        JsonElement binary,
        string semver,
        SdkArchitecture architecture,
        string distribution,
        List<SdkSourceVersion> result)
    {
        // 只接受 os=windows 且 image_type=jdk。
        if (!StringEquals(binary, "os", "windows") || !StringEquals(binary, "image_type", "jdk"))
        {
            return;
        }

        // architecture 字段映射：x64->X64、aarch64/arm64->Arm64。
        var rawArch = ReadString(binary, "architecture");
        var arch = rawArch?.ToLowerInvariant() switch
        {
            "x64" or "x86_64" or "amd64" => SdkArchitecture.X64,
            "aarch64" or "arm64" => SdkArchitecture.Arm64,
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

        // package.link 为下载地址，缺失则该 binary 无效。
        if (!binary.TryGetProperty("package", out var package) || package.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var link = ReadString(package, "link");
        if (string.IsNullOrWhiteSpace(link))
        {
            return;
        }

        // checksum 内联提供 SHA256（可能缺失）。
        var checksum = ReadString(package, "checksum");
        var archTag = arch == SdkArchitecture.Arm64 ? "arm64" : "x64";

        result.Add(new SdkSourceVersion(
            SdkType: SdkType.Java,
            Version: semver,
            Distribution: distribution,
            Architecture: arch,
            DownloadUrl: link!,
            Sha256: string.IsNullOrWhiteSpace(checksum) ? null : checksum,
            ChecksumUrl: null,
            ReleaseDate: null,
            DisplayName: $"{Capitalize(distribution)} {semver} ({archTag})"));
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

    /// <summary>
    /// 首字母大写，用于发行版显示名。
    /// </summary>
    private static string Capitalize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return char.ToUpperInvariant(value[0]) + value[1..];
    }
}
