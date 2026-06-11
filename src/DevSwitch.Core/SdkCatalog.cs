// 文件用途：定义 DevSwitch sdks.json 的公开 SDK 目录模型。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.Collections.Generic

namespace DevSwitch.Core;

/// <summary>
/// SDK CPU 架构。
/// </summary>
public enum SdkArchitecture
{
    /// <summary>
    /// 不区分架构，例如 Maven zip。
    /// </summary>
    Any,

    /// <summary>
    /// Windows x64 架构。
    /// </summary>
    X64,

    /// <summary>
    /// Windows arm64 架构。
    /// </summary>
    Arm64,

    /// <summary>
    /// 未识别或未声明架构。
    /// </summary>
    Unknown,
}

/// <summary>
/// SDK 来源类型。
/// </summary>
public enum SdkSourceKind
{
    /// <summary>
    /// 用户本地导入的外部 SDK，只登记路径，不复制、不删除实体目录。
    /// </summary>
    External,

    /// <summary>
    /// DevSwitch 下载并托管在数据根目录下的 SDK。
    /// </summary>
    Managed,
}

/// <summary>
/// SDK 记录状态。
/// </summary>
public enum SdkRecordStatus
{
    /// <summary>
    /// 当前正在使用。
    /// </summary>
    Active,

    /// <summary>
    /// 结构可用，可参与切换。
    /// </summary>
    Usable,

    /// <summary>
    /// 路径或结构不可用。
    /// </summary>
    Unavailable,

    /// <summary>
    /// 已登记但尚未执行手动验证。
    /// </summary>
    Unverified,
}

/// <summary>
/// 各 SDK 类型当前选中的记录 ID。
/// </summary>
/// <param name="Java">当前 Java SDK 记录 ID。</param>
/// <param name="Maven">当前 Maven SDK 记录 ID。</param>
/// <param name="Node">当前 Node.js SDK 记录 ID。</param>
/// <param name="Go">当前 Go SDK 记录 ID。</param>
public sealed record ActiveSdkSet(string? Java, string? Maven, string? Node, string? Go)
{
    /// <summary>
    /// 创建所有类型均未选中的 active 集合。
    /// </summary>
    public static ActiveSdkSet Empty { get; } = new(Java: null, Maven: null, Node: null, Go: null);
}

/// <summary>
/// 单个 SDK 记录。
/// </summary>
/// <param name="Id">稳定记录 ID，用于 active 指针和 UI 操作。</param>
/// <param name="Type">SDK 类型。</param>
/// <param name="Name">用户可见名称。</param>
/// <param name="Version">版本号；未知时可为 unknown。</param>
/// <param name="Distribution">发行版标识，例如 temurin、nodejs、go、apache-maven。</param>
/// <param name="Architecture">SDK 架构。</param>
/// <param name="Source">来源类型：外部导入或 DevSwitch 托管。</param>
/// <param name="Path">SDK 根目录路径。</param>
/// <param name="Status">当前可用状态。</param>
/// <param name="CreatedAt">记录创建时间。</param>
/// <param name="LastVerifiedAt">最后一次手动验证时间。</param>
public sealed record SdkRecord(
    string Id,
    SdkType Type,
    string Name,
    string Version,
    string Distribution,
    SdkArchitecture Architecture,
    SdkSourceKind Source,
    string Path,
    SdkRecordStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastVerifiedAt);

/// <summary>
/// sdks.json 根模型。
/// </summary>
/// <param name="SchemaVersion">SDK 目录 schema 版本。</param>
/// <param name="Active">各 SDK 类型当前选中记录。</param>
/// <param name="Items">所有已登记 SDK 记录。</param>
public sealed record SdkCatalog(int SchemaVersion, ActiveSdkSet Active, IReadOnlyList<SdkRecord> Items)
{
    /// <summary>
    /// 创建空 SDK 目录。
    /// </summary>
    /// <returns>schemaVersion=1 且没有任何 SDK 记录的目录。</returns>
    public static SdkCatalog CreateEmpty()
    {
        return new SdkCatalog(SchemaVersion: 1, Active: ActiveSdkSet.Empty, Items: Array.Empty<SdkRecord>());
    }
}
