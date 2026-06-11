// 文件用途：验证 DiagnosticSanitizer 脱敏与 DiagnosticBundleExporter 构造/写盘的公开行为。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit、System.IO.Compression、System.Text.Json
// NOTE: 合法授权学习使用，仅限本地环境。

using System.IO.Compression;
using System.Text.Json;
using DevSwitch.Core;
using Xunit;

namespace DevSwitch.Tests;

public sealed class DiagnosticBundleTests
{
    [Fact]
    public void SanitizeHidesWindowsUserName()
    {
        var text = @"dataRoot=C:\Users\alice\AppData\Local\DevSwitch";

        var sanitized = DiagnosticSanitizer.Sanitize(text);

        Assert.DoesNotContain("alice", sanitized);
        Assert.Contains(@"C:\Users\<user>", sanitized);
    }

    [Theory]
    [InlineData("token=abcdef123456")]
    [InlineData("\"password\": \"hunter2value\"")]
    [InlineData("api_key = sk-secretkeyvalue")]
    public void SanitizeMasksSecretKeyValues(string text)
    {
        var sanitized = DiagnosticSanitizer.Sanitize(text);

        // 值被打码为 ***，但保留 key 名以便排错。
        Assert.Contains("***", sanitized);
        Assert.DoesNotContain("hunter2value", sanitized);
        Assert.DoesNotContain("secretkeyvalue", sanitized);
        Assert.DoesNotContain("abcdef123456", sanitized);
    }

    [Fact]
    public void SanitizeMasksBearerCredential()
    {
        var text = "Authorization: Bearer eyJhbGciOiJIUzI1Ni9999";

        var sanitized = DiagnosticSanitizer.Sanitize(text);

        Assert.DoesNotContain("eyJhbGciOiJIUzI1Ni9999", sanitized);
        Assert.Contains("Bearer ***", sanitized);
    }

    [Fact]
    public void SanitizeReturnsEmptyForNull()
    {
        Assert.Equal(string.Empty, DiagnosticSanitizer.Sanitize(null));
    }

    [Fact]
    public void FilterDevSwitchPathEntriesKeepsOnlyDevSwitchEntries()
    {
        var pathEntries = new[]
        {
            @"C:\Windows\System32",
            @"C:\Users\bob\AppData\Local\DevSwitch\current\java\bin",
            @"C:\Program Files\nodejs",
        };

        var filtered = DiagnosticSanitizer.FilterDevSwitchPathEntries(pathEntries);

        var entry = Assert.Single(filtered);
        Assert.Contains("DevSwitch", entry);
        // 完整 PATH 中的非 DevSwitch 条目被丢弃。
        Assert.DoesNotContain(filtered, e => e.Contains("System32"));
        Assert.DoesNotContain(filtered, e => e.Contains("nodejs"));
        // 用户名被脱敏。
        Assert.DoesNotContain("bob", entry);
    }

    [Fact]
    public void BuildContentProducesReportConfigAndLogEntries()
    {
        var report = CreateSampleReport();
        var settings = DevSwitchSettingsStore.CreateDefault(@"C:\Users\carol\AppData\Local\DevSwitch");
        var logExcerpt = "INFO switch ok; token=supersecretvalue";

        var content = DiagnosticBundleExporter.BuildContent(report, settings, logExcerpt);

        Assert.Contains(content.Entries, e => e.RelativePath == "report.json");
        Assert.Contains(content.Entries, e => e.RelativePath == "config-summary.json");
        Assert.Contains(content.Entries, e => e.RelativePath == "logs-summary.txt");

        // 日志摘要必须脱敏。
        var logEntry = content.Entries.Single(e => e.RelativePath == "logs-summary.txt");
        Assert.DoesNotContain("supersecretvalue", logEntry.Content);

        // 配置摘要不得泄露完整用户名。
        var configEntry = content.Entries.Single(e => e.RelativePath == "config-summary.json");
        Assert.DoesNotContain("carol", configEntry.Content);
    }

    [Fact]
    public void BuildContentDoesNotIncludeFullPathOrEnvironment()
    {
        var report = CreateSampleReport();
        var settings = DevSwitchSettingsStore.CreateDefault(@"C:\data\DevSwitch");
        var fullPath = new[]
        {
            @"C:\Windows\System32",
            @"C:\Users\dan\AppData\Local\DevSwitch\current\java\bin",
        };

        // 调用方按约定只传过滤后的 DevSwitch 条目。
        var content = DiagnosticBundleExporter.BuildContent(
            report, settings, logExcerpt: null,
            devSwitchPathEntries: DiagnosticSanitizer.FilterDevSwitchPathEntries(fullPath));

        var configEntry = content.Entries.Single(e => e.RelativePath == "config-summary.json");
        Assert.DoesNotContain("System32", configEntry.Content);
    }

    [Fact]
    public async Task WriteZipAsyncWritesAllEntries()
    {
        var report = CreateSampleReport();
        var settings = DevSwitchSettingsStore.CreateDefault(@"C:\data\DevSwitch");
        var content = DiagnosticBundleExporter.BuildContent(report, settings, "log line");
        var zipPath = Path.Combine(CreateTempDir(), "bundle.zip");

        await DiagnosticBundleExporter.WriteZipAsync(content, zipPath);

        Assert.True(File.Exists(zipPath));
        using var archive = ZipFile.OpenRead(zipPath);
        Assert.Contains(archive.Entries, e => e.FullName == "report.json");
        Assert.Contains(archive.Entries, e => e.FullName == "config-summary.json");
        Assert.Contains(archive.Entries, e => e.FullName == "logs-summary.txt");
    }

    [Fact]
    public async Task WriteDirectoryAsyncWritesAllEntries()
    {
        var report = CreateSampleReport();
        var settings = DevSwitchSettingsStore.CreateDefault(@"C:\data\DevSwitch");
        var content = DiagnosticBundleExporter.BuildContent(report, settings, "log line");
        var dir = Path.Combine(CreateTempDir(), "bundle");

        await DiagnosticBundleExporter.WriteDirectoryAsync(content, dir);

        Assert.True(File.Exists(Path.Combine(dir, "report.json")));
        Assert.True(File.Exists(Path.Combine(dir, "config-summary.json")));
        Assert.True(File.Exists(Path.Combine(dir, "logs-summary.txt")));
    }

    [Fact]
    public void ReportJsonContainsOverallSeverityAndCounts()
    {
        var report = CreateSampleReport();
        var settings = DevSwitchSettingsStore.CreateDefault(@"C:\data\DevSwitch");

        var content = DiagnosticBundleExporter.BuildContent(report, settings, null);
        var reportJson = content.Entries.Single(e => e.RelativePath == "report.json").Content;

        using var document = JsonDocument.Parse(reportJson);
        Assert.True(document.RootElement.TryGetProperty("overallSeverity", out _));
        Assert.True(document.RootElement.TryGetProperty("counts", out _));
        Assert.True(document.RootElement.TryGetProperty("results", out _));
    }

    private static DoctorReport CreateSampleReport()
    {
        var results = new[]
        {
            DiagnosticResult.Pass("data-root-writable", "数据根目录可写性", "数据根目录可写。"),
            new DiagnosticResult("path-conflict", "PATH 前序冲突", DiagnosticSeverity.Warning, "javapath 可能遮蔽 java", "请手动调整 PATH。"),
        };
        return new DoctorReport(results, DateTimeOffset.Parse("2026-06-09T00:00:00Z"));
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
