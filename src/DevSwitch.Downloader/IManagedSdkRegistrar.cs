// 文件用途：定义下载完成后把托管 SDK 登记进 Core 的对接点接口。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：DevSwitch.Core
// NOTE: 合法授权学习使用，仅限本地环境。
//       下载器不直接依赖 sdks.json 写入实现，仅通过该接口把登记动作交回 Core，保持模块边界清晰。

using DevSwitch.Core;

namespace DevSwitch.Downloader;

/// <summary>
/// 已成功下载并解压的托管 SDK 的登记请求。
/// </summary>
/// <param name="TaskId">来源下载任务 ID。</param>
/// <param name="SdkType">SDK 类型。</param>
/// <param name="Version">声明版本（真实版本识别由 Core 完成）。</param>
/// <param name="Distribution">发行版标识。</param>
/// <param name="Arch">架构。</param>
/// <param name="InstallDirectory">解压后的 SDK 根目录。</param>
public sealed record ManagedSdkRegistration(
    string TaskId,
    SdkType SdkType,
    string Version,
    string Distribution,
    SdkArchitecture Arch,
    string InstallDirectory);

/// <summary>
/// 托管 SDK 登记对接点。下载完成流程在解压成功后调用，把实际写入 sdks.json
/// 与真实版本识别留给 Core 层实现。这里只定义骨架，供编排与测试注入假实现。
/// </summary>
public interface IManagedSdkRegistrar
{
    /// <summary>
    /// 登记一个新下载的托管 SDK。
    /// </summary>
    /// <param name="registration">登记请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task RegisterAsync(ManagedSdkRegistration registration, CancellationToken cancellationToken = default);
}
