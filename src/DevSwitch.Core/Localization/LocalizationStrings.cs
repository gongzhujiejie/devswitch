// 文件用途：DevSwitch 界面文案字符串表。为每种语言维护 key→文案的只读字典，
//           提供按语言查询能力；找不到 key 时返回 key 本身以便发现遗漏。
// 创建日期：2026-06-09
// 修改日期：2026-06-12（扩充主内容区 i18n key：首页 hero、SDK 管理、设置、诊断、配置档案、日志、占位）
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

        // 标题栏 & 通用 chrome
        ["titlebar.demoMode"] = "演示模式",
        ["titlebar.collapseNav"] = "折叠导航",
        ["titlebar.expandNav"] = "展开导航",

        // 首页 hero 区
        ["home.subtitle"] = "Windows App SDK · 开发环境切换器",
        ["home.description"] = "统一管理 JDK、Maven、Node.js、Go、Rust 等本地开发运行时。快速导入、检查路径，并安全切换环境。",
        ["home.openSdk"] = "打开 SDK 管理",
        ["home.runDoctor"] = "运行环境诊断",
        ["home.deco.active"] = "使用中",
        ["home.deco.currentRuntime"] = "当前运行时",
        ["home.shortcuts"] = "快捷操作",
        ["home.shortcut.sdk.desc"] = "导入并切换 SDK",
        ["home.shortcut.doctor.desc"] = "检查 PATH 与环境变量",
        ["home.shortcut.profiles.desc"] = "保存项目环境预设",
        ["home.shortcut.settings.desc"] = "语言、更新与反馈",

        // 设置页
        ["settings.title"] = "设置",
        ["settings.subtitle"] = "管理 DevSwitch 的界面语言、更新与反馈。",
        ["settings.language"] = "界面语言",
        ["settings.language.desc"] = "选择 DevSwitch 使用的界面语言，切换后立即生效。",
        ["settings.language.current"] = "当前语言",
        ["settings.language.label"] = "语言",
        ["settings.accent"] = "强调色",
        ["settings.accent.desc"] = "影响按钮、链接、选中态和高亮数字。",
        ["settings.dataDir"] = "数据目录",
        ["settings.dataDir.location"] = "数据存放位置",
        ["settings.dataDir.desc"] = "导入记录、配置与下载的 SDK 都保存在数据目录。",
        ["settings.dataDir.mode"] = "位置模式",
        ["settings.dataDir.modeHint"] = "便携：随应用目录走；固定到 C 盘：移动工具不影响数据与环境。",
        ["settings.dataDir.mode.portable"] = "便携（应用同目录）",
        ["settings.dataDir.mode.localappdata"] = "固定到 C 盘（推荐随意移动工具）",
        ["settings.dataDir.mode.custom"] = "自定义目录",
        ["settings.dataDir.current"] = "当前数据目录",
        ["settings.dataDir.changeMigrate"] = "自定义并迁移",
        ["settings.dataDir.hint"] = "切换模式或更改目录后需重启应用生效；可选择把已有数据迁移到新目录。",
        ["settings.download"] = "下载",
        ["settings.download.parallelism"] = "下载并发数",
        ["settings.download.parallelism.desc"] = "同时下载的分块数量，范围 1-8。数值越高速度可能越快，但更占用网络与磁盘。",
        ["settings.download.parallelism.current"] = "当前并发数",
        ["settings.update"] = "更新",
        ["settings.update.repo"] = "GitHub 仓库",
        ["settings.update.repo.desc"] = "填写发布仓库（owner/repo），用于检查与下载新版本。",
        ["settings.update.versionPrefix"] = "DevSwitch 当前版本",
        ["settings.update.desc"] = "检查是否有可用的新版本，可一键下载覆盖更新。",
        ["settings.checkUpdate"] = "检查更新",
        ["settings.update.download"] = "下载并更新",
        ["settings.feedback"] = "反馈",
        ["settings.feedback.send"] = "发送反馈",
        ["settings.feedback.desc"] = "报告问题或提出功能建议。",
        ["settings.feedback.button"] = "反馈",

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
        ["profiles.create"] = "新建档案",
        ["profiles.empty.hint"] = "点击右上角「新建档案」，把常用的 SDK 组合保存起来，下次一键切换。",

        // 日志页
        ["logs.title"] = "日志",
        ["logs.subtitle"] = "查看导入、切换与诊断记录。",
        ["logs.empty"] = "暂无日志记录",
        ["logs.allChannels"] = "全部",
        ["logs.prune"] = "清理过期",
        ["logs.channelLabel"] = "通道",
        ["logs.openFolder"] = "打开日志目录",
        ["logs.column.time"] = "时间",
        ["logs.column.channel"] = "通道",
        ["logs.column.message"] = "消息",
        ["logs.empty.hint"] = "操作 DevSwitch 后会在此显示导入、切换与诊断记录。",

        // SDK 管理表格列与路径交互
        ["sdk.column.path"] = "路径",
        ["sdk.path.tooltip"] = "单击复制完整路径",
        ["sdk.path.copied"] = "已复制路径到剪贴板",

        // SDK 总览/分类管理页
        ["sdk.overview.subtitle"] = "集中管理本机的 Java、Maven、Node.js、Go、Rust 运行时。点选下方分类卡片进入对应版本管理。",
        ["sdk.page.subtitle"] = "管理当前 SDK 类型的本地版本、来源路径和切换状态。",
        ["sdk.button.addLocal"] = "添加本地 SDK",
        ["sdk.button.download"] = "下载",
        ["sdk.button.detect"] = "检测当前",
        ["sdk.button.reset"] = "重置",
        ["sdk.statusFilter.placeholder"] = "状态",
        ["sdk.statusFilter.all"] = "全部",
        ["sdk.statusFilter.active"] = "使用中",
        ["sdk.statusFilter.usable"] = "可用",
        ["sdk.statusFilter.unavailable"] = "不可用",
        ["sdk.column.name"] = "名称",
        ["sdk.column.version"] = "版本",
        ["sdk.column.source"] = "来源",
        ["sdk.column.status"] = "状态",
        ["sdk.column.action"] = "操作",
        ["sdk.row.runtime"] = "SDK 运行时",
        ["sdk.row.moreActions"] = "更多操作",
        ["sdk.row.action.verify"] = "验证",
        ["sdk.row.action.openLocation"] = "打开所在位置",
        ["sdk.row.action.editName"] = "编辑名称",
        ["sdk.row.action.delete"] = "删除",
        ["sdk.openLocation.emptyTitle"] = "无法打开位置",
        ["sdk.openLocation.emptyMessage"] = "该 SDK 没有登记路径。",
        ["sdk.openLocation.missingTitle"] = "SDK 路径不存在",
        ["sdk.openLocation.missingMessage"] = "无法找到该 SDK 目录：\n{0}\n\n该 SDK 可能已被移动或删除，请刷新列表或编辑登记。",
        ["sdk.openLocation.failedTitle"] = "打开失败",
        ["sdk.empty.title"] = "暂无版本",

        // 环境诊断页
        ["doctor.subtitle"] = "检查数据目录、current 链接、环境变量、PATH、命令版本与 helper 可用性。",
        ["doctor.runAgain"] = "重新诊断",
        ["doctor.summary.idle"] = "点击「重新诊断」开始检查。",

        // 占位页
        ["placeholder.title"] = "即将推出",
        ["placeholder.desc"] = "该页面仍是预览占位。",
        ["placeholder.action"] = "了解更多"
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

        // Title bar & general chrome
        ["titlebar.demoMode"] = "Preview Mode",
        ["titlebar.collapseNav"] = "Collapse navigation",
        ["titlebar.expandNav"] = "Expand navigation",

        // Home hero section
        ["home.subtitle"] = "Windows App SDK · Development environment switcher",
        ["home.description"] = "Manage local JDK, Maven, Node.js, Go, Rust and other developer runtimes. Import, inspect paths, and switch environments safely.",
        ["home.openSdk"] = "Open SDK Management",
        ["home.runDoctor"] = "Run Diagnostics",
        ["home.deco.active"] = "Active",
        ["home.deco.currentRuntime"] = "Current Runtime",
        ["home.shortcuts"] = "Quick Actions",
        ["home.shortcut.sdk.desc"] = "Import and switch SDKs",
        ["home.shortcut.doctor.desc"] = "Check PATH and environment",
        ["home.shortcut.profiles.desc"] = "Save project presets",
        ["home.shortcut.settings.desc"] = "Language, updates, feedback",

        // Settings page
        ["settings.title"] = "Settings",
        ["settings.subtitle"] = "Manage DevSwitch language, updates and feedback.",
        ["settings.language"] = "Language",
        ["settings.language.desc"] = "Choose the display language. Takes effect immediately.",
        ["settings.language.current"] = "Current language",
        ["settings.language.label"] = "Language",
        ["settings.accent"] = "Accent color",
        ["settings.accent.desc"] = "Affects buttons, links, selected states and highlighted figures.",
        ["settings.dataDir"] = "Data Directory",
        ["settings.dataDir.location"] = "Data location",
        ["settings.dataDir.desc"] = "Imports, config and downloaded SDKs are stored in the data directory.",
        ["settings.dataDir.mode"] = "Location mode",
        ["settings.dataDir.modeHint"] = "Portable: lives next to the app. Pinned to C:: data and environment stay even if the app moves.",
        ["settings.dataDir.mode.portable"] = "Portable (next to app)",
        ["settings.dataDir.mode.localappdata"] = "Pinned to C: drive (recommended for portable apps)",
        ["settings.dataDir.mode.custom"] = "Custom folder",
        ["settings.dataDir.current"] = "Current data folder",
        ["settings.dataDir.changeMigrate"] = "Customize and migrate",
        ["settings.dataDir.hint"] = "Switching mode or path requires a restart; you can migrate existing data to the new folder.",
        ["settings.download"] = "Download",
        ["settings.download.parallelism"] = "Download concurrency",
        ["settings.download.parallelism.desc"] = "Number of parallel chunks (1-8). Higher values can be faster but use more network and disk.",
        ["settings.download.parallelism.current"] = "Current concurrency",
        ["settings.update"] = "Update",
        ["settings.update.repo"] = "GitHub repository",
        ["settings.update.repo.desc"] = "Release repository (owner/repo) used to check for and download new versions.",
        ["settings.update.versionPrefix"] = "DevSwitch current version",
        ["settings.update.desc"] = "Check for new versions and update with one click.",
        ["settings.checkUpdate"] = "Check for updates",
        ["settings.update.download"] = "Download and update",
        ["settings.feedback"] = "Feedback",
        ["settings.feedback.send"] = "Send feedback",
        ["settings.feedback.desc"] = "Report issues or suggest features.",
        ["settings.feedback.button"] = "Feedback",

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
        ["profiles.create"] = "New profile",
        ["profiles.empty.hint"] = "Click \"New profile\" in the top right to save your common SDK combinations and apply them in one click.",

        // Logs page
        ["logs.title"] = "Logs",
        ["logs.subtitle"] = "View import, switch and diagnostic records.",
        ["logs.empty"] = "No log entries",
        ["logs.allChannels"] = "All",
        ["logs.prune"] = "Prune Expired",
        ["logs.channelLabel"] = "Channel",
        ["logs.openFolder"] = "Open log folder",
        ["logs.column.time"] = "Time",
        ["logs.column.channel"] = "Channel",
        ["logs.column.message"] = "Message",
        ["logs.empty.hint"] = "Imports, switches and diagnostics will be logged here as you use DevSwitch.",

        // SDK management table columns and path interaction
        ["sdk.column.path"] = "Path",
        ["sdk.path.tooltip"] = "Click to copy the full path",
        ["sdk.path.copied"] = "Path copied to clipboard",

        // SDK overview / category page
        ["sdk.overview.subtitle"] = "Manage local Java, Maven, Node.js, Go and Rust runtimes from one place. Pick a category card below to manage versions.",
        ["sdk.page.subtitle"] = "Manage local versions, source paths and active state for the current SDK type.",
        ["sdk.button.addLocal"] = "Add Local SDK",
        ["sdk.button.download"] = "Download",
        ["sdk.button.detect"] = "Detect Current",
        ["sdk.button.reset"] = "Reset",
        ["sdk.statusFilter.placeholder"] = "Status",
        ["sdk.statusFilter.all"] = "All",
        ["sdk.statusFilter.active"] = "Active",
        ["sdk.statusFilter.usable"] = "Usable",
        ["sdk.statusFilter.unavailable"] = "Unavailable",
        ["sdk.column.name"] = "Name",
        ["sdk.column.version"] = "Version",
        ["sdk.column.source"] = "Source",
        ["sdk.column.status"] = "Status",
        ["sdk.column.action"] = "Action",
        ["sdk.row.runtime"] = "SDK Runtime",
        ["sdk.row.moreActions"] = "More Actions",
        ["sdk.row.action.verify"] = "Verify",
        ["sdk.row.action.openLocation"] = "Open File Location",
        ["sdk.row.action.editName"] = "Edit Name",
        ["sdk.row.action.delete"] = "Delete",
        ["sdk.openLocation.emptyTitle"] = "Cannot open location",
        ["sdk.openLocation.emptyMessage"] = "This SDK has no registered path.",
        ["sdk.openLocation.missingTitle"] = "SDK path not found",
        ["sdk.openLocation.missingMessage"] = "Cannot find this SDK folder:\n{0}\n\nThe SDK may have been moved or deleted. Refresh the list or edit the registration.",
        ["sdk.openLocation.failedTitle"] = "Open failed",
        ["sdk.empty.title"] = "No versions yet",

        // Diagnostics page
        ["doctor.subtitle"] = "Inspects data directory, current link, environment variables, PATH, command versions and helper availability.",
        ["doctor.runAgain"] = "Run Again",
        ["doctor.summary.idle"] = "Click \"Run Again\" to start the diagnostics.",

        // Placeholder page
        ["placeholder.title"] = "Coming soon",
        ["placeholder.desc"] = "This page is still a preview placeholder.",
        ["placeholder.action"] = "Learn more"
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
