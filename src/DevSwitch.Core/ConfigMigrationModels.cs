// 文件用途：定义 DevSwitch 配置迁移框架的公开契约与结果模型。
//           面向「任意带 schemaVersion 的 JSON 配置文件」，提供逐版本迁移步骤抽象与迁移结果描述。
// 创建/修改日期：2026-06-10
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.Text.Json.Nodes（结构化 JSON 变换，最易测）
// NOTE: 合法授权学习使用，仅限本地环境。模型只描述数据，不做任何文件 IO。

using System.Text.Json.Nodes;

namespace DevSwitch.Core;

/// <summary>
/// 单步配置迁移规则：把配置从 <see cref="FromVersion"/> 升级到 <see cref="ToVersion"/>。
/// </summary>
/// <remarks>
/// 实现者只需关注结构化数据变换，不需要自行更新 schemaVersion，
/// 框架会在每步成功后统一把根节点 schemaVersion 提升到 <see cref="ToVersion"/>。
/// </remarks>
public interface IConfigMigrationStep
{
    /// <summary>当前步骤适用的起始 schema 版本。</summary>
    int FromVersion { get; }

    /// <summary>当前步骤迁移完成后达到的目标 schema 版本，必须大于 <see cref="FromVersion"/>。</summary>
    int ToVersion { get; }

    /// <summary>
    /// 对传入的 JSON 根节点执行结构化变换并返回结果节点。
    /// </summary>
    /// <param name="node">当前版本的配置根节点（通常是 <see cref="JsonObject"/>）。</param>
    /// <returns>迁移后的配置根节点；不得返回 null。</returns>
    JsonNode Apply(JsonNode node);
}

/// <summary>
/// 配置迁移的最终状态分类。
/// </summary>
public enum ConfigMigrationStatus
{
    /// <summary>文件 schemaVersion 等于目标版本，无需迁移也未备份。</summary>
    NotNeeded = 0,

    /// <summary>检测到低版本，已备份并成功逐版本迁移到目标版本。</summary>
    Migrated = 1,

    /// <summary>文件 schemaVersion 高于目标版本，当前程序无法安全理解，已拒绝且未改动文件。</summary>
    VersionTooHigh = 2,

    /// <summary>文件缺少 schemaVersion 字段，无法判断迁移路径，判为失败且未改动文件。</summary>
    MissingSchemaVersion = 3,

    /// <summary>迁移过程失败（链断裂或步骤抛异常等），原文件保持旧内容，备份保留待用户处理。</summary>
    Failed = 4,
}

/// <summary>
/// 配置迁移结果模型：描述是否迁移、版本区间、备份路径以及失败原因。
/// </summary>
/// <param name="Status">迁移最终状态分类。</param>
/// <param name="FromVersion">迁移起始版本；缺少 schemaVersion 时为 null。</param>
/// <param name="ToVersion">目标版本；缺少 schemaVersion 时为 null。</param>
/// <param name="BackupPath">备份文件绝对路径；未备份时为 null。</param>
/// <param name="ErrorMessage">失败原因（VersionTooHigh / MissingSchemaVersion / Failed 时给出），成功时为 null。</param>
public sealed record ConfigMigrationResult(
    ConfigMigrationStatus Status,
    int? FromVersion,
    int? ToVersion,
    string? BackupPath,
    string? ErrorMessage)
{
    /// <summary>是否真正执行并写入了一次迁移。</summary>
    public bool Migrated => Status == ConfigMigrationStatus.Migrated;

    /// <summary>是否需要用户介入处理（迁移失败、版本过高或缺少 schemaVersion）。</summary>
    public bool RequiresUserAttention =>
        Status is ConfigMigrationStatus.Failed
            or ConfigMigrationStatus.VersionTooHigh
            or ConfigMigrationStatus.MissingSchemaVersion;

    /// <summary>构造「无需迁移」结果。</summary>
    public static ConfigMigrationResult NotNeeded(int version) =>
        new(ConfigMigrationStatus.NotNeeded, version, version, BackupPath: null, ErrorMessage: null);

    /// <summary>构造「迁移成功」结果。</summary>
    public static ConfigMigrationResult Success(int fromVersion, int toVersion, string backupPath) =>
        new(ConfigMigrationStatus.Migrated, fromVersion, toVersion, backupPath, ErrorMessage: null);

    /// <summary>构造「版本过高」结果。</summary>
    public static ConfigMigrationResult VersionTooHigh(int fileVersion, int targetVersion) =>
        new(
            ConfigMigrationStatus.VersionTooHigh,
            fileVersion,
            targetVersion,
            BackupPath: null,
            ErrorMessage: $"配置 schemaVersion {fileVersion} 高于当前支持的目标版本 {targetVersion}，已拒绝处理。");

    /// <summary>构造「缺少 schemaVersion」结果。</summary>
    public static ConfigMigrationResult MissingSchemaVersion(int targetVersion) =>
        new(
            ConfigMigrationStatus.MissingSchemaVersion,
            FromVersion: null,
            ToVersion: targetVersion,
            BackupPath: null,
            ErrorMessage: "配置文件缺少 schemaVersion 字段，无法判断迁移路径。");

    /// <summary>构造「迁移失败」结果，保留备份路径供用户处理。</summary>
    public static ConfigMigrationResult Failure(int? fromVersion, int targetVersion, string? backupPath, string error) =>
        new(ConfigMigrationStatus.Failed, fromVersion, targetVersion, backupPath, error);
}

/// <summary>
/// 配置迁移过程中的领域异常：链断裂、步骤非法或步骤执行失败时抛出。
/// </summary>
public sealed class ConfigMigrationException : Exception
{
    public ConfigMigrationException(string message)
        : base(message)
    {
    }

    public ConfigMigrationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
