// 文件用途：为下载完成的托管 SDK 构造经过结构校验与自动验证的 SdkRecord。
// 创建/修改日期：2026-06-12
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：DevSwitch.Core 导入验证服务与 BCL
// NOTE: 合法授权学习使用，仅限本地环境。结构不匹配会抛异常，避免污染 catalog。

namespace DevSwitch.Core;

/// <summary>
/// 下载托管 SDK 记录构造工厂。
/// </summary>
public static class ManagedSdkRecordFactory
{
    /// <summary>
    /// 结构校验并自动命令验证下载完成的托管 SDK，返回可写入 catalog 的记录。
    /// </summary>
    public static async Task<SdkRecord> CreateVerifiedRecordAsync(
        SdkType sdkType,
        string declaredVersion,
        string distribution,
        SdkArchitecture architecture,
        string installDirectory,
        SdkImportVerificationService verifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verifier);

        var detection = SdkRootDetector.Detect(installDirectory);
        if (detection.Status != SdkStatus.Usable || detection.Type == SdkType.Unknown)
        {
            throw new InvalidDataException($"Downloaded SDK directory is not a supported SDK root: {installDirectory}");
        }

        if (detection.Type != sdkType)
        {
            throw new InvalidDataException($"Downloaded SDK type mismatch. Expected {sdkType}, detected {detection.Type}: {installDirectory}");
        }

        var resolvedVersion = SdkVersionResolver.ResolveVersion(detection.Type, detection.RootPath);
        var initialVersion = string.Equals(resolvedVersion, SdkVersionResolver.UnknownVersion, StringComparison.Ordinal)
            ? declaredVersion
            : resolvedVersion;

        var initial = new SdkRecord(
            Id: $"{TypeSlug(sdkType)}-{Guid.NewGuid():N}",
            Type: sdkType,
            Name: BuildName(distribution, initialVersion, architecture),
            Version: initialVersion,
            Distribution: distribution,
            Architecture: architecture,
            Source: SdkSourceKind.Managed,
            Path: detection.RootPath,
            Status: SdkRecordStatus.Usable,
            CreatedAt: DateTimeOffset.UtcNow,
            LastVerifiedAt: null);

        var verified = await verifier.VerifyAsync(initial, cancellationToken).ConfigureAwait(false);
        return verified.Record with
        {
            Name = BuildName(distribution, verified.Record.Version, architecture),
        };
    }

    private static string BuildName(string distribution, string version, SdkArchitecture architecture)
    {
        string arch = architecture switch
        {
            SdkArchitecture.X64 => " (x64)",
            SdkArchitecture.Arm64 => " (arm64)",
            _ => string.Empty,
        };
        return $"{distribution} {version}{arch}";
    }

    private static string TypeSlug(SdkType type) => type switch
    {
        SdkType.Java => "java",
        SdkType.Maven => "maven",
        SdkType.Node => "node",
        SdkType.Go => "go",
        SdkType.Rust => "rust",
        _ => "sdk",
    };
}
