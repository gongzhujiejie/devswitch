// 文件用途：定义 DevSwitch settings.json 的配置模型，并提供默认初始化、保存与读回能力。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.Text.Json

using System.Text.Json;

namespace DevSwitch.Core;

/// <summary>
/// DevSwitch settings.json 的公开配置模型。
/// </summary>
/// <param name="SchemaVersion">配置 schema 版本；当前架构文档规定首版为 1。</param>
/// <param name="DataRoot">DevSwitch 数据根目录，写入后用于确认配置归属。</param>
/// <param name="Language">界面语言，例如 auto、zh-CN 或 en-US。</param>
/// <param name="Download">下载器相关设置，例如并发数和是否保留安装包。</param>
/// <param name="Compatibility">兼容变量设置，例如是否额外写入 JDK_HOME / M2_HOME。</param>
/// <param name="Update">更新源设置；首版只在手动检查更新时使用。</param>
public sealed record DevSwitchSettings(
    int SchemaVersion,
    string DataRoot,
    string Language,
    DownloadSettings Download,
    CompatibilitySettings Compatibility,
    UpdateSettings Update);

/// <summary>
/// 下载器设置。
/// </summary>
/// <param name="Parallelism">下载并发数，产品文档要求默认 4，设置页可调 1-8。</param>
/// <param name="KeepArchives">下载完成并登记后是否保留安装包。</param>
/// <param name="PreferredMirror">用户偏好的镜像源标识；为空时使用默认源策略。</param>
public sealed record DownloadSettings(int Parallelism, bool KeepArchives, string? PreferredMirror);

/// <summary>
/// 兼容性环境变量设置。
/// </summary>
/// <param name="SetJdkHome">切换 Java 时是否额外写入 JDK_HOME。</param>
/// <param name="SetM2Home">切换 Maven 时是否额外写入 M2_HOME。</param>
public sealed record CompatibilitySettings(bool SetJdkHome, bool SetM2Home);

/// <summary>
/// 更新源设置。
/// </summary>
/// <param name="Source">主更新源标识，默认 github-releases。</param>
/// <param name="FallbackSource">备用更新源标识；为空表示不启用备用源。</param>
/// <param name="Repository">GitHub 仓库标识（owner/repo），用于自更新下载；为空表示未配置，回退到 Source。</param>
public sealed record UpdateSettings(string Source, string? FallbackSource, string? Repository = null);

/// <summary>
/// 负责将 DevSwitch 设置保存到数据根目录下的 config/settings.json，并从该文件读回。
/// </summary>
public static class DevSwitchSettingsStore
{
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        // NOTE: settings.json 是跨进程/跨版本存储格式，使用文档约定的 camelCase 字段名。
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    /// <summary>
    /// 创建当前版本的默认设置。
    /// </summary>
    /// <param name="dataRoot">DevSwitch 数据根目录。</param>
    /// <returns>可直接保存到 settings.json 的默认设置。</returns>
    /// <exception cref="ArgumentException">dataRoot 为空时抛出。</exception>
    public static DevSwitchSettings CreateDefault(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("Data root is required.", nameof(dataRoot));
        }

        // NOTE: 默认值严格对应 docs/architecture/design.md 中 settings.json 示例。
        return new DevSwitchSettings(
            SchemaVersion: CurrentSchemaVersion,
            DataRoot: dataRoot,
            Language: "auto",
            Download: new DownloadSettings(Parallelism: 4, KeepArchives: false, PreferredMirror: null),
            Compatibility: new CompatibilitySettings(SetJdkHome: false, SetM2Home: false),
            Update: new UpdateSettings(Source: "github-releases", FallbackSource: null, Repository: "gongzhujiejie/devswitch"));
    }

    /// <summary>
    /// 读取 settings.json；如果文件不存在，则创建并返回默认设置。
    /// </summary>
    /// <param name="dataRoot">DevSwitch 数据根目录。</param>
    /// <returns>已有或新创建的设置。</returns>
    public static async Task<DevSwitchSettings> LoadOrCreateAsync(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("Data root is required.", nameof(dataRoot));
        }

        var settingsFile = Path.Combine(dataRoot, "config", "settings.json");
        if (File.Exists(settingsFile))
        {
            // NOTE: 库代码统一 ConfigureAwait(false)，不捕获调用方同步上下文。
            return await LoadAsync(dataRoot).ConfigureAwait(false);
        }

        var defaultSettings = CreateDefault(dataRoot);
        await SaveAsync(dataRoot, defaultSettings).ConfigureAwait(false);
        return defaultSettings;
    }

    /// <summary>
    /// 保存设置到指定数据根目录的 config/settings.json。
    /// </summary>
    /// <param name="dataRoot">DevSwitch 数据根目录，测试和应用均显式传入以避免依赖真实用户配置。</param>
    /// <param name="settings">要持久化的设置对象。</param>
    /// <returns>异步写入任务。</returns>
    /// <exception cref="ArgumentException">dataRoot 为空时抛出。</exception>
    /// <exception cref="ArgumentNullException">settings 为空时抛出。</exception>
    public static async Task SaveAsync(string dataRoot, DevSwitchSettings settings)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("Data root is required.", nameof(dataRoot));
        }

        ArgumentNullException.ThrowIfNull(settings);

        // NOTE: 只创建本行为需要的 config 目录；不触碰 sdks/downloads/sources 等其他配置文件。
        var configDirectory = Path.Combine(dataRoot, "config");
        Directory.CreateDirectory(configDirectory);

        var settingsFile = Path.Combine(configDirectory, "settings.json");
        var temporaryFile = Path.Combine(configDirectory, "settings.json.tmp");

        // NOTE: 原子写入策略：先把完整 JSON 写入同目录临时文件，确保替换操作发生在同一文件系统内。
        // 写入成功并刷新后再覆盖正式 settings.json；成功替换后 settings.json.tmp 会被移动走，不应残留。
        // 流式序列化直接写入 FileStream，避免先序列化成大字符串再落盘；FileOptions.Asynchronous 启用异步 IO。
        var streamOptions = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous,
        };
        await using (var stream = new FileStream(temporaryFile, streamOptions))
        {
            await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        File.Move(temporaryFile, settingsFile, overwrite: true);
    }

    /// <summary>
    /// 从指定数据根目录的 config/settings.json 读回设置。
    /// </summary>
    /// <param name="dataRoot">DevSwitch 数据根目录。</param>
    /// <returns>文件中保存的设置对象。</returns>
    /// <exception cref="ArgumentException">dataRoot 为空时抛出。</exception>
    /// <exception cref="InvalidDataException">settings.json 内容无法反序列化或 schema 不受支持时抛出。</exception>
    public static async Task<DevSwitchSettings> LoadAsync(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("Data root is required.", nameof(dataRoot));
        }

        var settingsFile = Path.Combine(dataRoot, "config", "settings.json");

        // NOTE: schemaVersion 校验依赖先读出完整文本再 JsonDocument.Parse（见 EnsureSupportedSchema），
        //       属于行为契约，必须在反序列化前完成；因此保留 string 读取，仅补 ConfigureAwait(false)。
        var json = await File.ReadAllTextAsync(settingsFile).ConfigureAwait(false);

        EnsureSupportedSchema(json);

        var settings = JsonSerializer.Deserialize<DevSwitchSettings>(json, SerializerOptions);

        // NOTE: 这里仅保护读回行为的可诊断性，真实迁移框架会在后续纵切补齐。
        return settings ?? throw new InvalidDataException("settings.json is empty or invalid.");
    }

    private static void EnsureSupportedSchema(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("schemaVersion", out var schemaVersionElement))
        {
            throw new InvalidDataException("settings.json is missing schemaVersion.");
        }

        var schemaVersion = schemaVersionElement.GetInt32();
        if (schemaVersion > CurrentSchemaVersion)
        {
            throw new InvalidDataException($"settings.json schemaVersion {schemaVersion} is newer than supported version {CurrentSchemaVersion}.");
        }
    }
}
