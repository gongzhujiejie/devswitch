// 文件用途：提供 Rust 官方 rustup-init.exe 下载源，供 DevSwitch 下载对话框列出 Rust stable 安装入口。
// 创建/修改日期：2026-06-12
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：DevSwitch.Core
// NOTE: 合法授权学习使用，仅限本地环境。本源仅生成官方静态下载元数据，不主动联网。

using DevSwitch.Core;

namespace DevSwitch.Sources;

/// <summary>
/// Rust 官方 rustup 下载源。
/// </summary>
/// <remarks>
/// Rust Windows 首选安装入口是 rustup-init.exe。DevSwitch 在这里仅列出稳定 channel 的 x64/arm64 MSVC installer；
/// 实际安装与登记由 Downloader/App 层 completion pipeline 执行，避免 Source 层承担副作用。
/// </remarks>
public sealed class RustupSource : ISdkVersionSource
{
    private const string BaseUrl = "https://static.rust-lang.org/rustup/dist";

    /// <inheritdoc />
    public SdkType SdkType => SdkType.Rust;

    /// <inheritdoc />
    public string Distribution => "rustup";

    /// <inheritdoc />
    public Task<IReadOnlyList<SdkSourceVersion>> ListVersionsAsync(
        SdkArchitecture architecture,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // SdkArchitecture.Any 用 x64 作为 Windows 默认入口；其它未知架构暂不展示，避免生成无效下载项。
        var effectiveArchitecture = architecture == SdkArchitecture.Any ? SdkArchitecture.X64 : architecture;
        var triple = ToWindowsMsvcTriple(effectiveArchitecture);
        if (triple is null)
        {
            return Task.FromResult<IReadOnlyList<SdkSourceVersion>>(Array.Empty<SdkSourceVersion>());
        }

        var url = $"{BaseUrl}/{triple}/rustup-init.exe";
        var displayArchitecture = effectiveArchitecture == SdkArchitecture.Arm64 ? "arm64" : "x64";
        var version = new SdkSourceVersion(
            SdkType: SdkType.Rust,
            Version: "stable",
            Distribution: Distribution,
            Architecture: effectiveArchitecture,
            DownloadUrl: url,
            Sha256: null,
            ChecksumUrl: url + ".sha256",
            ReleaseDate: null,
            DisplayName: $"Rust stable ({displayArchitecture})");

        return Task.FromResult<IReadOnlyList<SdkSourceVersion>>(new[] { version });
    }

    private static string? ToWindowsMsvcTriple(SdkArchitecture architecture)
    {
        return architecture switch
        {
            SdkArchitecture.X64 => "x86_64-pc-windows-msvc",
            SdkArchitecture.Arm64 => "aarch64-pc-windows-msvc",
            _ => null,
        };
    }
}
