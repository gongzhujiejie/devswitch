// 文件用途：提供 DevSwitch downloads.json 的异步保存、读回与首次初始化能力。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.Text.Json
// NOTE: 合法授权学习使用，仅限本地环境。写入策略与 SdkCatalogStore 一致（tmp -> replace）。

using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevSwitch.Downloader;

/// <summary>
/// 负责读写数据根目录 config/downloads.json。
/// 采用同目录临时文件 + 原子替换，避免写入中断破坏正式文件。
/// </summary>
public sealed class DownloadCatalogStore
{
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    /// <summary>
    /// 如果 downloads.json 存在则读取，否则创建空下载目录并保存。
    /// </summary>
    /// <param name="dataRoot">DevSwitch 数据根目录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已存在或新创建的下载目录。</returns>
    public async Task<DownloadCatalog> LoadOrCreateAsync(string dataRoot, CancellationToken cancellationToken = default)
    {
        EnsureDataRoot(dataRoot);

        var catalogFile = GetCatalogFile(dataRoot);
        if (!File.Exists(catalogFile))
        {
            // NOTE: 首次启动没有 downloads.json 时返回空目录而不是抛异常，保证 UI 任务列表可直接绑定。
            var emptyCatalog = DownloadCatalog.CreateEmpty();
            await SaveAsync(dataRoot, emptyCatalog, cancellationToken).ConfigureAwait(false);
            return emptyCatalog;
        }

        var json = await File.ReadAllTextAsync(catalogFile, cancellationToken).ConfigureAwait(false);
        EnsureSupportedSchema(json);

        var catalog = JsonSerializer.Deserialize<DownloadCatalog>(json, SerializerOptions);
        return catalog ?? throw new InvalidDataException("downloads.json is empty or invalid.");
    }

    /// <summary>
    /// 保存下载目录到 config/downloads.json。
    /// </summary>
    /// <param name="dataRoot">DevSwitch 数据根目录。</param>
    /// <param name="catalog">需要持久化的下载目录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task SaveAsync(string dataRoot, DownloadCatalog catalog, CancellationToken cancellationToken = default)
    {
        EnsureDataRoot(dataRoot);
        ArgumentNullException.ThrowIfNull(catalog);

        var configDirectory = Path.Combine(dataRoot, "config");
        Directory.CreateDirectory(configDirectory);

        var catalogFile = GetCatalogFile(dataRoot);
        var temporaryFile = Path.Combine(configDirectory, "downloads.json.tmp");

        // NOTE: 先写入同目录 .tmp 并 flush，再原子替换正式文件；同盘 move 是原子操作。
        await using (var stream = File.Create(temporaryFile))
        {
            await JsonSerializer.SerializeAsync(stream, catalog, SerializerOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
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

        // NOTE: downloads.json 是人工可读配置，状态枚举用 camelCase 字符串而非数字，便于迁移与排错。
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static string GetCatalogFile(string dataRoot)
    {
        return Path.Combine(dataRoot, "config", "downloads.json");
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
            throw new InvalidDataException("downloads.json is missing schemaVersion.");
        }

        var schemaVersion = schemaVersionElement.GetInt32();
        if (schemaVersion > CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"downloads.json schemaVersion {schemaVersion} is newer than supported version {CurrentSchemaVersion}.");
        }
    }
}
