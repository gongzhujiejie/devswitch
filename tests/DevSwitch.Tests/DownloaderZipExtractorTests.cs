// 文件用途：验证 ZipArchiveExtractor 流式解压与 zip-slip 路径穿越防护。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：xUnit、System.IO.Compression
// NOTE: 合法授权学习使用，仅限本地环境。构造内存 zip 验证正常解压与恶意条目拦截。

using System.IO.Compression;
using System.Text;
using DevSwitch.Downloader;
using Xunit;

namespace DevSwitch.Tests;

public sealed class DownloaderZipExtractorTests
{
    [Fact]
    public async Task ExtractWritesEntriesIntoDestination()
    {
        // 正常 zip：条目应被解压到目标目录，内容完整。
        var workspace = CreateWorkspace();
        var zipPath = Path.Combine(workspace, "archive.zip");
        var destDir = Path.Combine(workspace, "out");

        CreateZip(zipPath, new Dictionary<string, string>
        {
            ["bin/java.exe"] = "fake-java",
            ["release"] = "JAVA_VERSION=17",
        });

        var extractor = new ZipArchiveExtractor();
        await extractor.ExtractAsync(zipPath, destDir);

        Assert.Equal("fake-java", await File.ReadAllTextAsync(Path.Combine(destDir, "bin", "java.exe")));
        Assert.Equal("JAVA_VERSION=17", await File.ReadAllTextAsync(Path.Combine(destDir, "release")));
    }

    [Fact]
    public async Task ExtractRejectsZipSlipEntry()
    {
        // 恶意条目使用 ../ 试图逃逸目标目录，必须被拒绝且不写出目标目录之外。
        var workspace = CreateWorkspace();
        var zipPath = Path.Combine(workspace, "evil.zip");
        var destDir = Path.Combine(workspace, "out");

        // 用原始 entry 名称写入穿越路径，绕过 CreateEntryFromFile 的规范化。
        using (var fileStream = new FileStream(zipPath, FileMode.Create))
        using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../../escaped.txt");
            await using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            await writer.WriteAsync("pwned");
        }

        var extractor = new ZipArchiveExtractor();

        await Assert.ThrowsAsync<IOException>(() => extractor.ExtractAsync(zipPath, destDir));

        // 确认逃逸文件没有被写到目标目录的父级。
        var escapedPath = Path.GetFullPath(Path.Combine(destDir, "..", "..", "escaped.txt"));
        Assert.False(File.Exists(escapedPath));
    }

    private static void CreateZip(string zipPath, IReadOnlyDictionary<string, string> entries)
    {
        using var fileStream = new FileStream(zipPath, FileMode.Create);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(content);
        }
    }

    private static string CreateWorkspace()
    {
        var path = Path.Combine(Path.GetTempPath(), "DevSwitch.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
