// 文件用途：定义 DevSwitch profiles.json 的「配置档案」数据模型。
//          配置档案 = 用户保存的一组「项目 SDK 组合」（例如 Java 21 + Maven 3.9 + Node 20），可一键应用切换。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.Collections.Generic、System
// NOTE: 合法授权学习使用，仅限本地环境。

namespace DevSwitch.Core;

/// <summary>
/// 单个 SDK 选择项：把某个 SDK 类型绑定到 sdks.json 中具体一条记录的 Id。
/// </summary>
/// <param name="Type">SDK 类型（Java / Maven / Node / Go 等）。</param>
/// <param name="RecordId">目标 SDK 记录 Id，对应 sdks.json 里 <see cref="SdkRecord.Id"/>。</param>
public sealed record SdkProfileEntry(SdkType Type, string RecordId);

/// <summary>
/// 一个配置档案：一组命名的 SDK 选择，可一键应用切换。
/// </summary>
/// <param name="Id">稳定唯一 id（Guid "N" 格式），用于增删改定位，不随改名变化。</param>
/// <param name="Name">用户可读名称。</param>
/// <param name="Entries">该档案包含的 SDK 选择项集合。</param>
/// <param name="CreatedAt">档案创建时间。</param>
/// <param name="UpdatedAt">档案最后更新时间（改名等操作会刷新）。</param>
public sealed record SdkProfile(
    string Id,
    string Name,
    IReadOnlyList<SdkProfileEntry> Entries,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// profiles.json 根模型，承载全部配置档案。
/// </summary>
/// <param name="SchemaVersion">配置档案 schema 版本，与 sdks.json 风格一致，便于后续迁移。</param>
/// <param name="Profiles">所有已保存的配置档案。</param>
public sealed record SdkProfileCatalog(int SchemaVersion, IReadOnlyList<SdkProfile> Profiles)
{
    /// <summary>
    /// 创建空配置档案集合。
    /// </summary>
    /// <returns>schemaVersion=1 且没有任何档案的集合。</returns>
    public static SdkProfileCatalog CreateEmpty()
    {
        // NOTE: 与 SdkCatalog.CreateEmpty 保持同样风格——schemaVersion 固定 1、空集合用 Array.Empty 避免额外分配。
        return new SdkProfileCatalog(SchemaVersion: 1, Profiles: Array.Empty<SdkProfile>());
    }
}
