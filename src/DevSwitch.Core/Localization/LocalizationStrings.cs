// 文件用途：DevSwitch 界面文案字符串表。为每种语言维护 key→文案的只读字典，
//           提供按语言查询能力；找不到 key 时返回 key 本身以便发现遗漏。
// 创建日期：2026-06-09
// 语言版本要求：C# 12 / .NET 8+
// 依赖库：System.Collections.Generic、System.Linq

using System;
using System.Collections.Generic;
using System.Linq;

namespace DevSwitch.Core.Localization;

/// <summary>
/// 集中管理界面文案。key 采用点分命名（如 "nav.home"），两种语言的 key 集合保持完全一致。
/// </summary>
public static class LocalizationStrings
{
    // 简体中文文案表。key 必须与英文表完全对齐（由单元测试强制校验）。
    private static readonly IReadOnlyDictionary<string, string> ChineseMap = new Dictionary<string, string>
    {
        // 导航
        ["nav.home"] = "首页",
        ["nav.sdk"] = "SDK 管理",
        ["nav.profiles"] = "配置档案",
        ["nav.doctor"] = "环境诊断",
        ["nav.logs"] = "日志",
        ["nav.settings"] = "设置",
        ["nav.section"] = "导航",

        // 通用按钮
        ["common.refresh"] = "刷新",
        ["common.close"] = "知道了",
        ["common.cancel"] = "取消",
        ["common.delete"] = "删除",
        ["common.apply"] = "应用",
        ["common.rename"] = "重命名",
        ["common.create"] = "新建",
        ["common.openFolder"] = "打开目录",

        // 设置页
        ["settings.title"] = "设置",
        ["settings.subtitle"] = "管理 DevSwitch 的界面语言、更新与反馈。",
        ["settings.language"] = "界面语言",
        ["settings.language.desc"] = "选择 DevSwitch 使用的界面语言，切换后立即生效。",
        ["settings.language.current"] = "当前语言",
        ["settings.dataDir"] = "数据目录",
        ["settings.download"] = "下载",
        ["settings.update"] = "更新",
        ["settings.feedback"] = "反馈",
        ["settings.checkUpdate"] = "检查更新",

        // 下载对话框
        ["download.injdk.button"] = "国内镜像 injdk.cn",
        ["download.injdk.tooltip"] = "Oracle/Adoptium 下载较慢时可前往 injdk.cn 手动下载，再用「添加本地 SDK」导入。",

        // 语言名
        ["language.auto"] = "跟随系统",
        ["language.zhCN"] = "简体中文",
        ["language.enUS"] = "English",

        // 配置档案页
        ["profiles.title"] = "配置档案",
        ["profiles.subtitle"] = "保存不同项目的 SDK 组合，一键应用切换。",
        ["profiles.empty"] = "还没有配置档案",

        // 日志页
        ["logs.title"] = "日志",
        ["logs.subtitle"] = "查看导入、切换与诊断记录。",
        ["logs.empty"] = "暂无日志记录",
        ["logs.allChannels"] = "全部",
        ["logs.prune"] = "清理过期"
    };

    // 英文文案表。key 必须与中文表完全对齐。
    private static readonly IReadOnlyDictionary<string, string> EnglishMap = new Dictionary<string, string>
    {
        // Navigation
        ["nav.home"] = "Home",
        ["nav.sdk"] = "SDK Management",
        ["nav.profiles"] = "Profiles",
        ["nav.doctor"] = "Diagnostics",
        ["nav.logs"] = "Logs",
        ["nav.settings"] = "Settings",
        ["nav.section"] = "Navigation",

        // Common buttons
        ["common.refresh"] = "Refresh",
        ["common.close"] = "Got it",
        ["common.cancel"] = "Cancel",
        ["common.delete"] = "Delete",
        ["common.apply"] = "Apply",
        ["common.rename"] = "Rename",
        ["common.create"] = "Create",
        ["common.openFolder"] = "Open Folder",

        // Settings page
        ["settings.title"] = "Settings",
        ["settings.subtitle"] = "Manage DevSwitch language, updates and feedback.",
        ["settings.language"] = "Language",
        ["settings.language.desc"] = "Choose the display language. Takes effect immediately.",
        ["settings.language.current"] = "Current language",
        ["settings.dataDir"] = "Data Directory",
        ["settings.download"] = "Download",
        ["settings.update"] = "Update",
        ["settings.feedback"] = "Feedback",
        ["settings.checkUpdate"] = "Check Update",

        // Download dialog
        ["download.injdk.button"] = "China mirror: injdk.cn",
        ["download.injdk.tooltip"] = "If Oracle/Adoptium downloads are slow, get the JDK from injdk.cn and use \"Add Local SDK\".",

        // Language names
        ["language.auto"] = "System Default",
        ["language.zhCN"] = "Simplified Chinese",
        ["language.enUS"] = "English",

        // Profiles page
        ["profiles.title"] = "Profiles",
        ["profiles.subtitle"] = "Save SDK combinations per project and apply with one click.",
        ["profiles.empty"] = "No profiles yet",

        // Logs page
        ["logs.title"] = "Logs",
        ["logs.subtitle"] = "View import, switch and diagnostic records.",
        ["logs.empty"] = "No log entries",
        ["logs.allChannels"] = "All",
        ["logs.prune"] = "Prune Expired"
    };

    // 全部 key 的稳定列表。以中文表为基准（两表 key 集合相等，由测试保证），按字典序排序使结果可预期。
    private static readonly IReadOnlyList<string> AllKeysCache =
        ChineseMap.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>
    /// 所有可用的文案 key（按字典序）。两种语言均保证拥有这些 key。
    /// </summary>
    public static IReadOnlyList<string> AllKeys => AllKeysCache;

    /// <summary>
    /// 按语言查询文案。
    /// </summary>
    /// <param name="lang">目标语言。</param>
    /// <param name="key">点分命名的文案 key。</param>
    /// <returns>命中则返回对应文案；未命中（含 null/空 key）则原样返回 key 本身，便于发现遗漏，不抛异常。</returns>
    public static string Get(AppLanguage lang, string key)
    {
        // 防御：key 为空时无从查找，直接返回（返回空字符串而非 null，避免下游 NRE）。
        if (string.IsNullOrEmpty(key))
        {
            return key ?? string.Empty;
        }

        // 依据语言选择对应文案表。英文之外（含中文与未来兜底）一律用中文表。
        var map = lang == AppLanguage.English ? EnglishMap : ChineseMap;

        // 命中返回文案，未命中返回 key 本身——这是“暴露缺失”的刻意设计。
        return map.TryGetValue(key, out var value) ? value : key;
    }
}
