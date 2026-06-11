// 文件用途：定义 App 层读取真实 SDK catalog 的轻量抽象。
// 创建/修改日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：DevSwitch.Core

using DevSwitch.Core;

namespace DevSwitch.App.Services;

/// <summary>
/// 为 ViewModel 提供真实 SDK 目录读取能力。
/// </summary>
public interface ISdkCatalogProvider
{
    /// <summary>
    /// 加载或创建真实 sdks.json catalog。
    /// </summary>
    /// <param name="cancellationToken">窗口关闭时用于取消后续 UI 更新的令牌。</param>
    /// <returns>当前 SDK 目录。</returns>
    Task<SdkCatalog> LoadOrCreateAsync(CancellationToken cancellationToken = default);
}
