// 文件用途：提供 DevSwitch sdks.json 的异步保存、读回与首次初始化能力。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.Text.Json

using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevSwitch.Core;

/// <summary>
/// 负责读写数据根目录 config/sdks.json。
/// </summary>
public sealed class SdkCatalogStore
{
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    /// <summary>
    /// 如果 sdks.json 存在则读取，否则创建空 SDK 目录并保存。
    /// </summary>
    /// <param name="dataRoot">DevSwitch 数据根目录。</param>
    /// <returns>已存在或新创建的 SDK 目录。</returns>
    public async Task<SdkCatalog> LoadOrCreateAsync(string dataRoot)
    {
        EnsureDataRoot(dataRoot);

        var catalogFile = GetCatalogFile(dataRoot);
        if (!File.Exists(catalogFile))
        {
            var emptyCatalog = SdkCatalog.CreateEmpty();
            // NOTE: 库代码统一 ConfigureAwait(false)，不捕获调用方同步上下文，减少上下文切换。
            await SaveAsync(dataRoot, emptyCatalog).ConfigureAwait(false);
            return emptyCatalog;
        }

        // NOTE: schemaVersion 校验依赖先读出完整文本再 JsonDocument.Parse（见 EnsureSupportedSchema），
        //       属于行为契约，必须在反序列化前完成；因此此处保留 string 读取，仅补 ConfigureAwait(false)。
        var json = await File.ReadAllTextAsync(catalogFile).ConfigureAwait(false);
        EnsureSupportedSchema(json);

        var catalog = JsonSerializer.Deserialize<SdkCatalog>(json, SerializerOptions);
        return catalog ?? throw new InvalidDataException("sdks.json is empty or invalid.");
    }

    /// <summary>
    /// 保存 SDK 目录到 config/sdks.json。
    /// </summary>
    /// <param name="dataRoot">DevSwitch 数据根目录。</param>
    /// <param name="catalog">需要持久化的 SDK 目录。</param>
    public async Task SaveAsync(string dataRoot, SdkCatalog catalog)
    {
        EnsureDataRoot(dataRoot);
        ArgumentNullException.ThrowIfNull(catalog);

        var configDirectory = Path.Combine(dataRoot, "config");
        Directory.CreateDirectory(configDirectory);

        var catalogFile = GetCatalogFile(dataRoot);
        var temporaryFile = Path.Combine(configDirectory, "sdks.json.tmp");

        // NOTE: 与 settings.json 一致，使用同目录临时文件降低写入中断破坏正式文件的概率。
        // 流式序列化直接写入 FileStream，避免先序列化成大字符串再写盘的中转开销；
        // FileOptions.Asynchronous 启用真正的异步 IO，减少线程阻塞。
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

        File.Move(temporaryFile, catalogFile, overwrite: true);
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };

        // NOTE: sdks.json 是人工可读配置，枚举用 camelCase 字符串而不是数字，便于迁移和排错。
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static string GetCatalogFile(string dataRoot)
    {
        return Path.Combine(dataRoot, "config", "sdks.json");
    }

    private static void EnsureDataRoot(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("Data root is required.", nameof(dataRoot));
        }
    }

    private static void EnsureSupportedSchema(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("schemaVersion", out var schemaVersionElement))
        {
            throw new InvalidDataException("sdks.json is missing schemaVersion.");
        }

        var schemaVersion = schemaVersionElement.GetInt32();
        if (schemaVersion > CurrentSchemaVersion)
        {
            throw new InvalidDataException($"sdks.json schemaVersion {schemaVersion} is newer than supported version {CurrentSchemaVersion}.");
        }
    }
}
