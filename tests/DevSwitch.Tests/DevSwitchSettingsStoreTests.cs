// 文件用途：验证 DevSwitch settings.json 的公开保存、读回与默认初始化行为。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit
// NOTE: 合法授权学习使用，仅限本地环境。本测试只写入临时目录，不读取真实用户配置。

using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class DevSwitchSettingsStoreTests
{
    [Fact]
    public async Task LoadOrCreateAsyncCreatesCompleteDefaultSettingsWhenMissing()
    {
        // NOTE: 首次启动是用户最重要的公开行为：没有 settings.json 时应自动得到完整默认配置。
        // 测试只通过 Store 的公开接口验证，不关心内部目录创建或 JSON 写入步骤。
        var dataRoot = CreateTemporaryDirectory();

        var settings = await DevSwitchSettingsStore.LoadOrCreateAsync(dataRoot);

        Assert.Equal(1, settings.SchemaVersion);
        Assert.Equal(dataRoot, settings.DataRoot);
        Assert.Equal("auto", settings.Language);
        Assert.Equal(4, settings.Download.Parallelism);
        Assert.False(settings.Download.KeepArchives);
        Assert.Null(settings.Download.PreferredMirror);
        Assert.False(settings.Compatibility.SetJdkHome);
        Assert.False(settings.Compatibility.SetM2Home);
        Assert.Equal("github-releases", settings.Update.Source);
        Assert.Null(settings.Update.FallbackSource);
        Assert.True(File.Exists(Path.Combine(dataRoot, "config", "settings.json")));
    }

    [Fact]
    public async Task SaveAsyncThenLoadAsyncRoundTripsSettingsFromConfigDirectory()
    {
        // NOTE: 通过公开 Settings Store 接口验证唯一用户行为：保存到指定数据根后可读回同样设置。
        // 临时目录隔离真实用户配置，避免测试受当前机器状态影响。
        var dataRoot = CreateTemporaryDirectory();
        var settings = new DevSwitchSettings(
            SchemaVersion: 1,
            DataRoot: dataRoot,
            Language: "zh-CN",
            Download: new DownloadSettings(Parallelism: 6, KeepArchives: true, PreferredMirror: "mirror-a"),
            Compatibility: new CompatibilitySettings(SetJdkHome: true, SetM2Home: true),
            Update: new UpdateSettings(Source: "github-releases", FallbackSource: "mirror-updates"));

        var settingsFile = Path.Combine(dataRoot, "config", "settings.json");
        var temporaryFile = Path.Combine(dataRoot, "config", "settings.json.tmp");

        // NOTE: 预置一个旧临时文件，用公开行为约束 SaveAsync 成功后不能遗留 settings.json.tmp。
        // 这样既不窥探私有实现，也能驱动“临时文件 + 替换正式文件”的原子写入策略。
        Directory.CreateDirectory(Path.GetDirectoryName(temporaryFile)!);
        await File.WriteAllTextAsync(temporaryFile, "stale temporary content");

        await DevSwitchSettingsStore.SaveAsync(dataRoot, settings);
        var loaded = await DevSwitchSettingsStore.LoadAsync(dataRoot);

        Assert.Equal(settings, loaded);
        Assert.True(File.Exists(settingsFile));
        Assert.False(File.Exists(temporaryFile));
    }

    [Fact]
    public async Task LoadAsyncRejectsFutureSchemaVersionWithoutOverwritingFile()
    {
        // NOTE: 未来 schema 代表当前版本无法安全理解的配置，不能静默按旧模型继续使用。
        // 这里直接写入最小 JSON，验证公开读取行为会给出可诊断异常，并保留原文件。
        var dataRoot = CreateTemporaryDirectory();
        var configDirectory = Path.Combine(dataRoot, "config");
        Directory.CreateDirectory(configDirectory);
        var settingsFile = Path.Combine(configDirectory, "settings.json");
        await File.WriteAllTextAsync(settingsFile, "{\"schemaVersion\":999,\"dataRoot\":\"" + dataRoot.Replace("\\", "\\\\") + "\",\"language\":\"auto\"}");

        await Assert.ThrowsAsync<InvalidDataException>(() => DevSwitchSettingsStore.LoadAsync(dataRoot));

        Assert.True(File.Exists(settingsFile));
    }

    [Fact]
    public async Task LoadOrCreateAsyncDefaultsAccentColorToAzure()
    {
        // NOTE: 默认强调色应为 azure，确保未配置时与历史视觉一致。
        var dataRoot = CreateTemporaryDirectory();

        var settings = await DevSwitchSettingsStore.LoadOrCreateAsync(dataRoot);

        Assert.Equal(AccentPalette.DefaultKey, settings.AccentColor);
        Assert.Equal("azure", settings.AccentColor);
    }

    [Fact]
    public async Task SaveAsyncThenLoadAsyncRoundTripsAccentColor()
    {
        // NOTE: 用户在设置页选定的强调色必须能持久化并读回。
        var dataRoot = CreateTemporaryDirectory();
        var defaults = DevSwitchSettingsStore.CreateDefault(dataRoot);
        var settings = defaults with { AccentColor = "violet" };

        await DevSwitchSettingsStore.SaveAsync(dataRoot, settings);
        var loaded = await DevSwitchSettingsStore.LoadAsync(dataRoot);

        Assert.Equal("violet", loaded.AccentColor);
    }

    [Fact]
    public async Task LoadAsyncFallsBackWhenAccentColorFieldMissingFromLegacyJson()
    {
        // NOTE: 旧 settings.json 没有 accentColor 字段，System.Text.Json 会注入 null。
        //       Load 不能崩，且经 AccentPalette.Resolve 容错后应回退默认 azure。
        var dataRoot = CreateTemporaryDirectory();
        var configDirectory = Path.Combine(dataRoot, "config");
        Directory.CreateDirectory(configDirectory);
        var settingsFile = Path.Combine(configDirectory, "settings.json");

        // 故意写入不含 accentColor 的最小合法 JSON（含必需的 schemaVersion）。
        var legacyJson = "{\"schemaVersion\":1,\"dataRoot\":\"" + dataRoot.Replace("\\", "\\\\") + "\","
            + "\"language\":\"auto\","
            + "\"download\":{\"parallelism\":4,\"keepArchives\":false,\"preferredMirror\":null},"
            + "\"compatibility\":{\"setJdkHome\":false,\"setM2Home\":false},"
            + "\"update\":{\"source\":\"github-releases\",\"fallbackSource\":null,\"repository\":null}}";
        await File.WriteAllTextAsync(settingsFile, legacyJson);

        var loaded = await DevSwitchSettingsStore.LoadAsync(dataRoot);

        // 缺失字段反序列化为 null；Resolve 容错回退默认 azure。
        Assert.Equal("azure", AccentPalette.Resolve(loaded.AccentColor).Key);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
