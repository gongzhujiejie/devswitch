// 文件用途：提供 DevSwitch profiles.json 的异步读写与配置档案增删改能力。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.Text.Json
// NOTE: 合法授权学习使用，仅限本地环境。

using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevSwitch.Core;

/// <summary>
/// 负责读写数据根目录 config/profiles.json，并提供配置档案的增删改操作。
/// 持久化范式与 <see cref="SdkCatalogStore"/> 完全一致：camelCase、缩进、枚举字符串、临时文件原子替换。
/// </summary>
public sealed class SdkProfileStore
{
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    /// <summary>
    /// 如果 profiles.json 存在则读取，否则创建空档案集合并保存。
    /// </summary>
    /// <param name="dataRoot">DevSwitch 数据根目录。</param>
    /// <returns>已存在或新创建的配置档案集合。</returns>
    public async Task<SdkProfileCatalog> LoadOrCreateAsync(string dataRoot)
    {
        EnsureDataRoot(dataRoot);

        var catalogFile = GetCatalogFile(dataRoot);
        if (!File.Exists(catalogFile))
        {
            // NOTE: 首次访问没有 profiles.json，建立空集合并立即落盘，保证后续读取一致。
            var emptyCatalog = SdkProfileCatalog.CreateEmpty();
            await SaveAsync(dataRoot, emptyCatalog).ConfigureAwait(false);
            return emptyCatalog;
        }

        // NOTE: schemaVersion 校验需在反序列化前完成，因此先读完整文本再解析，与 SdkCatalogStore 一致。
        var json = await File.ReadAllTextAsync(catalogFile).ConfigureAwait(false);
        EnsureSupportedSchema(json);

        var catalog = JsonSerializer.Deserialize<SdkProfileCatalog>(json, SerializerOptions);
        return catalog ?? throw new InvalidDataException("profiles.json is empty or invalid.");
    }

    /// <summary>
    /// 保存配置档案集合到 config/profiles.json（临时文件 + 原子替换）。
    /// </summary>
    /// <param name="dataRoot">DevSwitch 数据根目录。</param>
    /// <param name="catalog">需要持久化的配置档案集合。</param>
    public async Task SaveAsync(string dataRoot, SdkProfileCatalog catalog)
    {
        EnsureDataRoot(dataRoot);
        ArgumentNullException.ThrowIfNull(catalog);

        var configDirectory = Path.Combine(dataRoot, "config");
        Directory.CreateDirectory(configDirectory);

        var catalogFile = GetCatalogFile(dataRoot);
        var temporaryFile = Path.Combine(configDirectory, "profiles.json.tmp");

        // NOTE: 同目录临时文件降低写入中断破坏正式文件的概率；流式序列化 + 异步 IO，避免大字符串中转。
        var streamOptions = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous,
        };
        await using (var stream = new FileStream(temporaryFile, streamOptions))
        {
            await JsonSerializer.SerializeAsync(stream, catalog, SerializerOptions).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        // NOTE: File.Move overwrite 在同卷上是原子替换，确保读取方永远看到完整文件。
        File.Move(temporaryFile, catalogFile, overwrite: true);
    }

    /// <summary>
    /// 新增一个配置档案：生成 Guid id、CreatedAt=UpdatedAt=now，追加并落盘。
    /// </summary>
    /// <param name="dataRoot">DevSwitch 数据根目录。</param>
    /// <param name="name">用户可读名称，空白将抛出异常。</param>
    /// <param name="entries">该档案包含的 SDK 选择项集合。</param>
    /// <returns>新建的配置档案。</returns>
    /// <exception cref="ArgumentException">name 为空白时抛出。</exception>
    public async Task<SdkProfile> AddAsync(string dataRoot, string name, IReadOnlyList<SdkProfileEntry> entries)
    {
        EnsureDataRoot(dataRoot);
        EnsureName(name);
        // NOTE: entries 允许为空集合（用户可先建空档案再补），但不允许 null。
        ArgumentNullException.ThrowIfNull(entries);

        var catalog = await LoadOrCreateAsync(dataRoot).ConfigureAwait(false);

        // NOTE: 创建与更新时间统一取同一个 now，保证新档案 CreatedAt==UpdatedAt。
        var now = DateTimeOffset.Now;
        var profile = new SdkProfile(
            Id: Guid.NewGuid().ToString("N"),
            Name: name,
            Entries: entries,
            CreatedAt: now,
            UpdatedAt: now);

        // NOTE: 不可变 record + 新列表，避免就地修改只读集合带来的副作用。
        var updatedProfiles = new List<SdkProfile>(catalog.Profiles) { profile };
        await SaveAsync(dataRoot, catalog with { Profiles = updatedProfiles }).ConfigureAwait(false);

        return profile;
    }

    /// <summary>
    /// 按 id 移除配置档案并落盘；id 不存在则无操作，不抛异常。
    /// </summary>
    /// <param name="dataRoot">DevSwitch 数据根目录。</param>
    /// <param name="profileId">要移除的档案 id。</param>
    public async Task RemoveAsync(string dataRoot, string profileId)
    {
        EnsureDataRoot(dataRoot);

        var catalog = await LoadOrCreateAsync(dataRoot).ConfigureAwait(false);

        // NOTE: 用 RemoveAll 过滤目标 id；若没有命中则列表不变，落盘也是幂等的，满足"不存在不抛"。
        var updatedProfiles = new List<SdkProfile>(catalog.Profiles);
        var removed = updatedProfiles.RemoveAll(profile => profile.Id == profileId);
        if (removed == 0)
        {
            return;
        }

        await SaveAsync(dataRoot, catalog with { Profiles = updatedProfiles }).ConfigureAwait(false);
    }

    /// <summary>
    /// 按 id 重命名配置档案，同时刷新 UpdatedAt 并落盘。
    /// </summary>
    /// <param name="dataRoot">DevSwitch 数据根目录。</param>
    /// <param name="profileId">要重命名的档案 id。</param>
    /// <param name="newName">新名称，空白将抛出异常。</param>
    /// <returns>更新后的档案；id 不存在时返回 null。</returns>
    /// <exception cref="ArgumentException">newName 为空白时抛出。</exception>
    public async Task<SdkProfile?> RenameAsync(string dataRoot, string profileId, string newName)
    {
        EnsureDataRoot(dataRoot);
        EnsureName(newName);

        var catalog = await LoadOrCreateAsync(dataRoot).ConfigureAwait(false);

        var updatedProfiles = new List<SdkProfile>(catalog.Profiles);
        var index = updatedProfiles.FindIndex(profile => profile.Id == profileId);
        if (index < 0)
        {
            // NOTE: 找不到目标 id 视为无操作，返回 null 让调用方决定如何提示，不抛异常。
            return null;
        }

        // NOTE: 改名属于"更新"，刷新 UpdatedAt；CreatedAt 保持不变以保留创建历史。
        var renamed = updatedProfiles[index] with { Name = newName, UpdatedAt = DateTimeOffset.Now };
        updatedProfiles[index] = renamed;

        await SaveAsync(dataRoot, catalog with { Profiles = updatedProfiles }).ConfigureAwait(false);
        return renamed;
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };

        // NOTE: profiles.json 是人工可读配置，枚举用 camelCase 字符串而非数字，便于迁移和排错。
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static string GetCatalogFile(string dataRoot)
    {
        return Path.Combine(dataRoot, "config", "profiles.json");
    }

    private static void EnsureDataRoot(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("Data root is required.", nameof(dataRoot));
        }
    }

    private static void EnsureName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Profile name is required.", nameof(name));
        }
    }

    private static void EnsureSupportedSchema(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("schemaVersion", out var schemaVersionElement))
        {
            throw new InvalidDataException("profiles.json is missing schemaVersion.");
        }

        var schemaVersion = schemaVersionElement.GetInt32();
        if (schemaVersion > CurrentSchemaVersion)
        {
            throw new InvalidDataException($"profiles.json schemaVersion {schemaVersion} is newer than supported version {CurrentSchemaVersion}.");
        }
    }
}
