// 文件用途：从一个 release 的资产列表里挑出「Windows x64 安装包 zip」及可选的 sha256 校验资产。
// 创建/修改日期：2026-06-10
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：仅 BCL（无第三方包）
// NOTE: 合法授权学习使用，仅限本地环境。本类为纯逻辑，不发起任何网络/磁盘副作用。

namespace DevSwitch.Core;

/// <summary>
/// 资产选择结果：选中的安装包及其可选 sha256 校验资产。
/// </summary>
/// <param name="Package">选中的 Windows x64 安装包资产。</param>
/// <param name="Checksum">配套的 sha256 校验资产；找不到时为空。</param>
public sealed record SelectedUpdateAsset(GitHubReleaseAsset Package, GitHubReleaseAsset? Checksum);

/// <summary>
/// 更新资产选择器。按平台/架构匹配度从资产列表中挑出最合适的 Windows x64 安装包，
/// 并尝试为其匹配 sha256 校验资产。全部为纯字符串判定，大小写不敏感。
/// </summary>
public static class UpdateAssetSelector
{
    /// <summary>
    /// 从资产列表挑选 Windows x64 安装包及其校验资产。
    /// </summary>
    /// <param name="assets">release 的全部资产。</param>
    /// <returns>
    /// 命中时返回 <see cref="SelectedUpdateAsset"/>；
    /// 列表为空、无 .zip、或无任何 Windows 包时返回 null。
    /// </returns>
    public static SelectedUpdateAsset? Select(IReadOnlyList<GitHubReleaseAsset> assets)
    {
        if (assets is null || assets.Count == 0)
        {
            return null;
        }

        GitHubReleaseAsset? best = null;
        var bestScore = int.MinValue;

        foreach (var asset in assets)
        {
            // 资产名可能为空，跳过无效项。
            if (asset is null || string.IsNullOrWhiteSpace(asset.Name))
            {
                continue;
            }

            var score = ScorePackage(asset.Name);

            // score < 0 表示不是合格的 Windows zip 包（不满足硬性条件），直接淘汰。
            if (score < 0)
            {
                continue;
            }

            // 取匹配度最高者；同分时保留先出现的（稳定、可预测）。
            if (score > bestScore)
            {
                best = asset;
                bestScore = score;
            }
        }

        if (best is null)
        {
            return null;
        }

        // 为选中的包寻找配套 sha256 校验资产。
        var checksum = FindChecksum(assets, best.Name);
        return new SelectedUpdateAsset(best, checksum);
    }

    // 给候选包打分：返回 < 0 表示不合格（不是 Windows zip）；否则分数越高匹配度越好。
    // 硬性条件：以 .zip 结尾 且 含 "win"/"windows"。
    // 加分项：含 "x64"（含 "win10-x64" 等组合）> 仅 win。
    private static int ScorePackage(string name)
    {
        // .sha256 等校验文件不能当作安装包。
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        var lower = name.ToLowerInvariant();

        // 必须是 Windows 平台包。
        var isWindows = lower.Contains("win"); // "win" 已覆盖 "windows"/"win10" 等
        if (!isWindows)
        {
            return -1;
        }

        var score = 0;

        // 基础分：满足「Windows zip」即给底分。
        score += 1;

        // 架构加分：x64 是目标架构，给高权重，确保优先于 arm64 等其他架构。
        if (lower.Contains("x64") || lower.Contains("amd64") || lower.Contains("x86_64"))
        {
            score += 10;
        }

        // 明确含 "windows" 全称略加分（更精确的平台标识）。
        if (lower.Contains("windows"))
        {
            score += 1;
        }

        return score;
    }

    // 为安装包寻找 sha256 校验资产。
    // 命中规则（大小写不敏感）：
    //  1) 名字 = 包名 + ".sha256"（如 app-win-x64.zip.sha256）
    //  2) 名字 = 去扩展名包名 + ".sha256"（如 app-win-x64.sha256）
    //  3) 名字含 "sha256" 且与包名共享去扩展名主干（处理 app-win-x64.zip.sha256sum 等变体）
    private static GitHubReleaseAsset? FindChecksum(IReadOnlyList<GitHubReleaseAsset> assets, string packageName)
    {
        var candidate1 = packageName + ".sha256";

        // 去掉包名的 .zip 扩展，得到主干名。
        var stem = packageName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? packageName[..^4]
            : packageName;
        var candidate2 = stem + ".sha256";

        foreach (var asset in assets)
        {
            if (asset is null || string.IsNullOrWhiteSpace(asset.Name))
            {
                continue;
            }

            var n = asset.Name;

            // 精确匹配 1 / 2。
            if (string.Equals(n, candidate1, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n, candidate2, StringComparison.OrdinalIgnoreCase))
            {
                return asset;
            }
        }

        // 退化匹配：名字含 "sha256" 且以包主干名开头（覆盖 .sha256sum / .sha256.txt 等变体）。
        foreach (var asset in assets)
        {
            if (asset is null || string.IsNullOrWhiteSpace(asset.Name))
            {
                continue;
            }

            var lower = asset.Name.ToLowerInvariant();
            if (lower.Contains("sha256") &&
                lower.StartsWith(stem.ToLowerInvariant(), StringComparison.Ordinal))
            {
                return asset;
            }
        }

        return null;
    }
}
