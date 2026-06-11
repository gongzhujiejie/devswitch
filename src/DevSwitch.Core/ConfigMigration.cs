// 文件用途：配置迁移框架核心。给定一组迁移步骤，对任意带 schemaVersion 的 JSON 配置文件执行
//           「备份 -> 逐版本迁移 -> 原子写入」流程，并对失败提供「不破坏原文件」保护。
// 创建/修改日期：2026-06-10
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.Text.Json、System.Text.Json.Nodes、System.IO
// NOTE: 合法授权学习使用，仅限本地环境。失败时原文件保持旧内容，备份保留待用户处理。

using System.Text.Json;
using System.Text.Json.Nodes;

namespace DevSwitch.Core;

/// <summary>
/// 通用配置迁移器：作用于任意「带 schemaVersion 的 JSON 配置文件」。
/// </summary>
/// <remarks>
/// 写入策略与现有 store 一致：<c>file.json.tmp -> flush -> File.Move(overwrite)</c>，
/// 保证替换发生在同一文件系统内，降低写入中断破坏正式文件的概率。
/// </remarks>
public sealed class ConfigMigrator
{
    private const string SchemaVersionPropertyName = "schemaVersion";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        // NOTE: 配置文件人工可读，沿用现有 store 的缩进 JSON 风格。
        WriteIndented = true,
    };

    private readonly ConfigBackupService _backupService;
    private readonly Func<DateTimeOffset> _timestampProvider;

    /// <summary>
    /// 创建迁移器。
    /// </summary>
    /// <param name="backupService">备份服务；为空时使用默认实现。</param>
    /// <param name="timestampProvider">时间戳来源；为空时使用 <see cref="DateTimeOffset.UtcNow"/>，注入便于测试。</param>
    public ConfigMigrator(ConfigBackupService? backupService = null, Func<DateTimeOffset>? timestampProvider = null)
    {
        _backupService = backupService ?? new ConfigBackupService();
        _timestampProvider = timestampProvider ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// 纯函数：从给定步骤集合中解析「从 fromVersion 链式迁移到 targetVersion」需要应用的有序步骤。
    /// </summary>
    /// <param name="steps">所有可用迁移步骤（无序）。</param>
    /// <param name="fromVersion">起始版本。</param>
    /// <param name="targetVersion">目标版本，必须大于等于 fromVersion。</param>
    /// <returns>按版本号递增排列、可连续衔接的迁移步骤序列；fromVersion == targetVersion 时为空序列。</returns>
    /// <exception cref="ConfigMigrationException">链中断（缺少某一版本的步骤）或出现非法步骤时抛出。</exception>
    public static IReadOnlyList<IConfigMigrationStep> ResolveMigrationChain(
        IEnumerable<IConfigMigrationStep> steps,
        int fromVersion,
        int targetVersion)
    {
        ArgumentNullException.ThrowIfNull(steps);

        if (targetVersion < fromVersion)
        {
            throw new ConfigMigrationException(
                $"目标版本 {targetVersion} 不能低于起始版本 {fromVersion}。");
        }

        // NOTE: 同版本无需迁移，返回空链是合法的「无操作」结果。
        if (fromVersion == targetVersion)
        {
            return Array.Empty<IConfigMigrationStep>();
        }

        // NOTE: 以 FromVersion 建索引，便于按当前版本 O(1) 查找下一步；重复定义视为歧义直接报错。
        var byFromVersion = new Dictionary<int, IConfigMigrationStep>();
        foreach (var step in steps)
        {
            ArgumentNullException.ThrowIfNull(step);
            if (step.ToVersion <= step.FromVersion)
            {
                throw new ConfigMigrationException(
                    $"非法迁移步骤：ToVersion {step.ToVersion} 必须大于 FromVersion {step.FromVersion}。");
            }

            if (!byFromVersion.TryAdd(step.FromVersion, step))
            {
                throw new ConfigMigrationException(
                    $"版本 {step.FromVersion} 存在多个迁移步骤，迁移路径歧义。");
            }
        }

        var chain = new List<IConfigMigrationStep>();
        var current = fromVersion;

        // NOTE: 从起始版本逐跳前进，直到抵达目标版本；任何一跳缺失即判链断裂。
        while (current < targetVersion)
        {
            if (!byFromVersion.TryGetValue(current, out var step))
            {
                throw new ConfigMigrationException(
                    $"缺少从版本 {current} 出发的迁移步骤，无法继续迁移到目标版本 {targetVersion}。");
            }

            if (step.ToVersion > targetVersion)
            {
                throw new ConfigMigrationException(
                    $"从版本 {current} 的迁移步骤会越过目标版本 {targetVersion}（到达 {step.ToVersion}）。");
            }

            chain.Add(step);
            current = step.ToVersion;
        }

        return chain;
    }

    /// <summary>
    /// 纯函数：对给定 JSON 根节点依次应用迁移链，并在每步后把 schemaVersion 提升到该步 ToVersion。
    /// </summary>
    /// <param name="root">起始配置根节点。</param>
    /// <param name="chain">已解析的有序迁移链。</param>
    /// <returns>迁移完成后的配置根节点。</returns>
    /// <exception cref="ConfigMigrationException">某步骤返回 null 或抛出异常时包装抛出。</exception>
    public static JsonNode ApplyChain(JsonNode root, IReadOnlyList<IConfigMigrationStep> chain)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(chain);

        var current = root;
        foreach (var step in chain)
        {
            JsonNode next;
            try
            {
                next = step.Apply(current);
            }
            catch (Exception ex)
            {
                // NOTE: 把步骤内部异常统一包装为领域异常，向上传递时便于失败保护逻辑识别。
                throw new ConfigMigrationException(
                    $"迁移步骤 {step.FromVersion}->{step.ToVersion} 执行失败：{ex.Message}", ex);
            }

            if (next is null)
            {
                throw new ConfigMigrationException(
                    $"迁移步骤 {step.FromVersion}->{step.ToVersion} 返回了 null 节点。");
            }

            // NOTE: 框架统一负责提升 schemaVersion，步骤实现无需关心版本字段，降低出错面。
            next[SchemaVersionPropertyName] = step.ToVersion;
            current = next;
        }

        return current;
    }

    /// <summary>
    /// 对单个配置文件执行完整迁移流程：读取 -> 判断版本 -> 备份 -> 逐版本迁移 -> 原子写入。
    /// </summary>
    /// <param name="dataRoot">DevSwitch 数据根目录，用于定位备份目录。</param>
    /// <param name="filePath">待迁移配置文件的绝对路径。</param>
    /// <param name="targetVersion">目标 schema 版本。</param>
    /// <param name="steps">可用迁移步骤集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>迁移结果模型。失败时原文件保持旧内容，备份保留。</returns>
    public async Task<ConfigMigrationResult> MigrateFileAsync(
        string dataRoot,
        string filePath,
        int targetVersion,
        IEnumerable<IConfigMigrationStep> steps,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("Data root is required.", nameof(dataRoot));
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path is required.", nameof(filePath));
        }

        ArgumentNullException.ThrowIfNull(steps);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Migration source file does not exist.", filePath);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // NOTE: 先把步骤物化为列表，既避免多次枚举副作用，也让链解析与执行复用同一份数据。
        var stepList = steps as IReadOnlyList<IConfigMigrationStep> ?? steps.ToList();

        // NOTE: 解析阶段任何失败都不应触碰原文件，因此先把完整文本读出再 JsonNode.Parse。
        //       此读取是 schema 校验与失败保护的前置契约，保留 string 读取；库代码补 ConfigureAwait(false)。
        var json = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);

        // NOTE: 解析阶段任何失败都不应触碰原文件，因此放在备份/写入之前。
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            return ConfigMigrationResult.Failure(
                fromVersion: null, targetVersion, backupPath: null,
                error: $"配置文件不是合法 JSON：{ex.Message}");
        }

        if (root is null)
        {
            return ConfigMigrationResult.Failure(
                fromVersion: null, targetVersion, backupPath: null,
                error: "配置文件内容为空，无法迁移。");
        }

        // 边界：缺少 schemaVersion -> 失败，不改文件。
        if (!TryReadSchemaVersion(root, out var currentVersion))
        {
            return ConfigMigrationResult.MissingSchemaVersion(targetVersion);
        }

        // 边界：版本过高 -> 拒绝，不改文件。
        if (currentVersion > targetVersion)
        {
            return ConfigMigrationResult.VersionTooHigh(currentVersion, targetVersion);
        }

        // 边界：等版本 -> 不迁移、不备份。
        if (currentVersion == targetVersion)
        {
            return ConfigMigrationResult.NotNeeded(currentVersion);
        }

        // 至此 currentVersion < targetVersion，需要迁移。先备份，再迁移。
        string backupPath;
        try
        {
            backupPath = await _backupService.BackupAsync(
                dataRoot, filePath, currentVersion, _timestampProvider(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // NOTE: 备份失败属于无法保证可回退的高风险场景，直接判失败且不改文件。
            return ConfigMigrationResult.Failure(
                currentVersion, targetVersion, backupPath: null,
                error: $"备份配置文件失败，已中止迁移：{ex.Message}");
        }

        // 解析迁移链并执行变换；任何失败都不写正式文件，保留备份供用户处理。
        JsonNode migrated;
        try
        {
            var chain = ResolveMigrationChain(stepList, currentVersion, targetVersion);
            migrated = ApplyChain(root, chain);
        }
        catch (ConfigMigrationException ex)
        {
            // NOTE: 失败保护核心——此分支尚未写过正式文件，原文件天然保持旧内容。
            return ConfigMigrationResult.Failure(currentVersion, targetVersion, backupPath, ex.Message);
        }

        // 原子写入：tmp -> flush -> File.Move(overwrite)。写入阶段失败同样不破坏原文件（tmp 与正式文件分离）。
        try
        {
            await WriteAtomicallyAsync(filePath, migrated, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ConfigMigrationResult.Failure(
                currentVersion, targetVersion, backupPath,
                error: $"写入迁移结果失败，原配置已保留：{ex.Message}");
        }

        return ConfigMigrationResult.Success(currentVersion, targetVersion, backupPath);
    }

    /// <summary>
    /// 读取根节点的 schemaVersion 整数值。
    /// </summary>
    private static bool TryReadSchemaVersion(JsonNode root, out int version)
    {
        version = 0;
        if (root is not JsonObject obj)
        {
            return false;
        }

        if (!obj.TryGetPropertyValue(SchemaVersionPropertyName, out var node) || node is null)
        {
            return false;
        }

        // NOTE: schemaVersion 必须是整数；非数字或浮点视为缺失/非法，交由上层判失败。
        try
        {
            version = node.GetValue<int>();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 原子写入：把节点序列化到同目录 .tmp 文件并 flush，再 File.Move 覆盖正式文件。
    /// </summary>
    private static async Task WriteAtomicallyAsync(string filePath, JsonNode node, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryFile = filePath + ".tmp";

        // NOTE: 与现有 store 一致，临时文件位于同目录，保证 File.Move 在同一卷内为原子替换。
        // FileOptions.Asynchronous 启用真正的异步 IO，写入顺序（写 tmp -> flush -> Move）保持不变。
        var streamOptions = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous,
        };
        await using (var stream = new FileStream(temporaryFile, streamOptions))
        {
            await using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            node.WriteTo(writer, WriteOptions);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryFile, filePath, overwrite: true);
    }
}
