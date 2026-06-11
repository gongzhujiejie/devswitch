// 文件用途：应用级界面语言运行时管理器。保存当前语言、按 key 取当前语言文案，
//           并在语言变更时触发事件，供 UI 重新拉取文案实现运行时即时热切换。
// 创建日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：DevSwitch.Core.Localization、System.Globalization

using System;
using System.Globalization;
using DevSwitch.Core.Localization;

namespace DevSwitch.App.Localization;

/// <summary>
/// 界面语言运行时管理器（应用级单例）。
/// 纯 C#，不依赖任何 UI 类型，可被 MainWindow 等界面层引用。
/// 语言变更后触发 <see cref="LanguageChanged"/>，界面据此重新调用索引器/Get 刷新文本。
/// </summary>
public sealed class LocalizationManager
{
    // 懒加载单例：Lazy<T> 保证线程安全的延迟初始化。
    private static readonly Lazy<LocalizationManager> LazyInstance =
        new(() => new LocalizationManager());

    /// <summary>全局唯一实例。</summary>
    public static LocalizationManager Instance => LazyInstance.Value;

    /// <summary>当前生效的界面语言。默认中文（本项目主语言）。</summary>
    public AppLanguage CurrentLanguage { get; private set; }

    /// <summary>语言变更后触发，供 UI 重新拉取文案刷新界面。</summary>
    public event EventHandler? LanguageChanged;

    // 私有构造：禁止外部 new，强制走单例。默认初始化为中文。
    private LocalizationManager()
    {
        CurrentLanguage = AppLanguage.ChineseSimplified;
    }

    /// <summary>
    /// 索引器：按 key 取当前语言文案。等价于 <see cref="Get(string)"/>。
    /// 便于 XAML/代码以 <c>LocalizationManager.Instance["nav.home"]</c> 风格使用。
    /// </summary>
    /// <param name="key">点分命名的文案 key。</param>
    /// <returns>当前语言下的文案；未命中返回 key 本身。</returns>
    public string this[string key] => LocalizationStrings.Get(CurrentLanguage, key);

    /// <summary>
    /// 按 key 取当前语言文案。
    /// </summary>
    /// <param name="key">点分命名的文案 key。</param>
    /// <returns>当前语言下的文案；未命中返回 key 本身。</returns>
    public string Get(string key) => LocalizationStrings.Get(CurrentLanguage, key);

    /// <summary>
    /// 设置当前语言。仅当与现值不同才更新并触发 <see cref="LanguageChanged"/>。
    /// </summary>
    /// <param name="language">目标语言。</param>
    /// <returns>是否发生了变更（同值返回 false 且不触发事件）。</returns>
    public bool SetLanguage(AppLanguage language)
    {
        // 同值短路：避免无意义的事件风暴与界面无谓刷新。
        if (CurrentLanguage == language)
        {
            return false;
        }

        CurrentLanguage = language;

        // 触发变更事件，订阅者（UI）据此重新拉取所有文案。
        LanguageChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// 便捷方法：用设置中的语言标记 + 当前系统区域，解析后应用语言。
    /// 系统区域取 <see cref="CultureInfo.CurrentUICulture"/> 的 Name（如 "zh-CN" / "en-US" / "zh-Hans-CN"）。
    /// </summary>
    /// <param name="languageTag">设置里的语言标记（auto / zh-CN / en-US，容错大小写与空格）。</param>
    /// <returns>是否发生了语言变更。</returns>
    public bool ApplyFromSettings(string? languageTag)
    {
        // 读取系统 UI 区域名作为 auto 的判定依据。
        var systemCulture = CultureInfo.CurrentUICulture.Name;

        // 委托纯逻辑解析器得到具体语言，再走统一的 SetLanguage 通道（保证事件语义一致）。
        var resolved = LanguageResolver.Resolve(languageTag, systemCulture);
        return SetLanguage(resolved);
    }
}
