# 更新日志（Changelog）

本项目所有重要变更都会记录在此文件。

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [v0.2.13] - 2026-06-13 — 一级导航页闪退修复

### 修复（Fixed）
- **配置档案 / 环境诊断 / 日志 / 设置首次点击卡死闪退**：根据 `startup.log` 中的 `InvalidOperationException: 无法加载界面区域` 证据，移除四个一级导航页的 `x:Load=\"False\"` 首访懒实例化，改为控件树随主窗口创建、业务 IO 仍在导航后异步执行。
- **导航异常边界**：四个一级导航 click handler 统一走 `RunNavigationAction` / `RunNavigationActionAsync`，页面打开失败时回到首页并展示错误提示，不再让异常冒泡到 WinUI 全局未处理异常。
- **日志页稳定性**：日志页改为先显示再刷新；新增 `Refresh()`、加载防重入、通道枚举异常兜底和 `ContentDialog.XamlRoot` 防御，避免重复点击日志页叠加任务或提示路径二次崩溃。
- **设置页初始化时序**：移除 `LanguageComboBox` 的 XAML 默认 `SelectedIndex`，提前设置 `isSettingsInitializing`，并让设置页 `SelectionChanged` 处理器使用 `sender` 作为事件源，避免初始化期间误触发持久化/语言切换。
- **诊断页卡死防御**：`RunDoctorAsync` 的 UI 状态切换纳入 try/finally 并加控件判空；helper JSON 调用增加 5 秒硬超时和进程树清理，避免 helper 卡住导致诊断页永久转圈。

### 测试（Tests）
- 新增一级导航回归测试，覆盖四个崩溃敏感页面不再 `x:Load` 延迟、Show 方法不再依赖 `EnsureDeferredContent/FindName`、click handler 必须走统一导航安全边界。

## [v0.2.12] - 2026-06-13 — 配置档案首访崩溃修复

### 修复（Fixed）
- **点击配置档案卡死并自动退出**：修复 `ProfilesContent x:Load="False"` 首次导航时的异步加载时序。
  - 先把 `ProfilesView` 接入活动内容树，再启动数据加载，确保异常提示拥有有效 `XamlRoot`。
  - `ProfilesView` 首次刷新延迟到 `Loaded` / `XamlRoot` 就绪后执行，避免懒加载阶段提前弹 `ContentDialog`。
  - 档案读写放入后台线程，异步完成后通过 `DispatcherQueue.TryEnqueue` 回 UI 线程更新列表与空状态，避免跨线程 UI 更新或慢盘 IO 卡住首访点击。
  - 错误提示路径增加二次保护，防止加载失败后的提示弹窗再次抛异常导致进程退出。

### 测试（Tests）
- 新增 Profiles 导航回归测试，覆盖首访激活顺序、Initialize 不直接 fire-and-forget 刷新、异步加载后必须回 UI 线程更新。

## [v0.2.11] - 2026-06-13 — 首帧延迟加载 + ReadyToRun 打包修复

### 优化（Changed）
- **首帧延迟加载**：设置页、诊断页、配置档案页与日志页改用 `x:Load=\"False\"`，首次导航时再创建控件树，避免非首屏内容拖慢 `MainWindow.InitializeComponent()`。
- **启动后初始化并行化**：SDK catalog 与 settings 读取在 `Loaded` 后并行启动；环境位置漂移校正改为后台 fire-and-forget，不再串行阻塞首轮交互。
- **设置读取不强制创建设置页**：启动阶段只应用语言与强调色配置；设置页控件保持延迟加载，打开设置页后再同步 ComboBox、数据目录与更新仓库字段。

### 修复（Fixed）
- **Release ReadyToRun 打包失败**：CI 打包脚本在 restore 阶段显式传入 `-r win10-x64` 与 `PublishReadyToRun=true`，publish 阶段使用 `--no-restore` 复用 RID 专用 assets，修复 `NETSDK1094`。
- **精简 shell 环境下 NuGet path1 异常**：打包脚本补齐 `SystemRoot`、`USERPROFILE`、`LOCALAPPDATA`、`TEMP/TMP` 等环境变量兜底，避免 Git Bash/CI 精简环境影响 MSBuild 路径解析。

## [v0.2.10] - 2026-06-13 — Rust SDK 支持 + 启动加速

### 新增（Added）
- **Rust SDK 完整接入**：SDK 管理、总览卡片、左侧导航、导入、切换、删除、验证、Doctor 与 PATH/shim 均支持 Rust。
  - 本地导入识别 standalone toolchain 根目录：`bin\rustc.exe`、`bin\cargo.exe`、`bin\rustdoc.exe`。
  - 支持误选 `bin` 目录时自动回退到 Rust toolchain 根目录。
  - 不把 `.cargo\bin` rustup proxy 目录误识别为可切换 SDK 根。
- **Rust 官方下载源**：下载对话框新增 Rust stable x64/arm64。
  - 使用官方 `rustup-init.exe`，并读取 `.sha256` 校验文件做完整性验证。
  - 安装时使用临时 `CARGO_HOME` / `RUSTUP_HOME` 与 `--no-modify-path`，不污染用户全局 PATH。
  - 下载完成后登记真实 toolchain root：`rustup\toolchains\stable-<triple>`。

### 优化（Changed）
- **启动速度优化**：主窗口首屏路径不再同步递归清理 `data\updates`，改为后台清理，避免历史更新包过多时拖慢启动。
- **启动路径瘦身**：启动阶段不再同步读取/创建 `settings.json` 只为强调色，先用默认主题显示首帧，Loaded 后按设置校正。
- **helper/shim 懒解析**：`DevSwitch.Helper.exe` 与 `DevSwitch.Shim.exe` 路径从 AppServices 构造期扫描改为按需解析，减少启动时 `File.Exists` 与父目录遍历。
- **发布包 ReadyToRun**：CI 打包脚本启用 `PublishReadyToRun=true`，降低 Release 包首次启动 JIT 成本。

### 修复（Fixed）
- **英文界面状态筛选失效**：状态 ComboBox 改用稳定 `Tag` 解析过滤枚举，不再依赖会被本地化的 `Content` 文案。

## [v0.2.9] - 2026-06-12 — 强调色自定义 + 诊断误报修复

### 新增（Added）
- **全局强调色自定义**：设置页新增「强调色」区，提供 6 套 Fluent 2 风格配色
  （海蓝 / 紫罗兰 / 翡翠 / 琥珀 / 玫瑰 / 青空）。点击色块即时换色并持久化到
  `settings.json` 的 `accentColor`，影响按钮、链接、选中态与高亮数字等全局品牌色。
  - 启动时优先读取并应用已保存的强调色，确保首帧即为目标配色；解析失败回退默认海蓝（`#2563EB`，与历史视觉一致）。
  - 换色采用「就地改 Brush.Color」方案：所有引用强调色资源的已渲染控件即时重绘，无需重启。
  - 色块带中英双语 ToolTip 与无障碍名称，随界面语言热切换。
  - 调色板（`AccentPalette`）为纯数据 + 纯逻辑，独立于 WinUI，便于单元测试覆盖。

### 修复（Fixed）
- **环境诊断「DevSwitch PATH 片段」误报为错误**：使用老式逐类型 PATH（`current\java\bin` 等、
  无统一 shims 目录）的用户，诊断恒报「未检测到任何托管片段」Error。
  - 识别口径与 `CheckPathConflictsAsync` 对齐：存在唯一 shims 目录 → Pass；
    仅有老式逐类型片段 → Info（建议迁移到 shims 单目录方案）；两者皆无才报 Error。
  - `NormalizePath` 增强：统一正/反斜杠并去引号空白，便携模式（dataRoot 非 LocalAppData）下也能正确按 dataRoot 前缀识别托管条目。

## [v0.2.8] - 2026-06-11 — 紧急修复 v0.2.7 启动崩溃

### 修复（Fixed）
- **v0.2.7 双击 `DevSwitch.App.exe` 闪退**：v0.2.7 状态过滤 ComboBox 第一项设置了 `IsSelected="True"`，
  XAML 解析期间立即触发 `SelectionChanged`，但此时 `viewModel` 字段尚未在构造函数里赋值，
  抛 `NullReferenceException`，被 `InitializeComponent` 包成 `XamlParseException`，主窗口构造失败、应用闪退。
  - 调整 `MainWindow` 构造顺序：先赋值 `viewModel / dataRoot / appServices` 字段，再调用 `InitializeComponent()`。
  - `OnStatusFilterChanged` 增加 `viewModel is null` 早期返回，作为双层防御。
- 强烈建议所有装了 v0.2.7 的用户升级到 v0.2.8。

## [v0.2.7] - 2026-06-11

### 新增（Added）
- **SDK 列表新增「路径」列**：插在「来源」与「状态」之间，显示完整安装路径。
  - 单击路径文本即可**复制完整路径**到剪贴板，复制成功后顶部弹出 InfoBar 提示，3 秒自动消失。
  - 列宽自适应剩余空间，路径过长被截断时鼠标悬浮显示完整路径 ToolTip。
  - 视觉遵循 Fluent 2：默认次要文本色，hover 时切到品牌色。
- **窗口最小尺寸约束**：通过 `WM_GETMINMAXINFO` 子类化拦截，最小尺寸 1280×800，防止用户拖小到内容截断。

### 修复（Fixed）
- **状态过滤无效**：右上角「状态」下拉切换「全部 / 使用中 / 可用 / 不可用」时列表内容不变。
  - 引入 `DevSwitch.Core.SdkStatusFilter` 枚举 + `SdkStatusFilterMatcher` 纯函数。
  - ViewModel 新增 `SelectedStatusFilter` 属性，按"分类 ∧ 状态"重算可见集；ComboBox `SelectionChanged`
    接到此属性，每次切换实时刷新列表，默认选中「全部」。
- **「检测当前」失败后状态徽章不同步**：命令验证失败（如 `java.exe 未能启动`）后徽章仍显示「可用」。
  - `SdkVersionRow` 改为 `INotifyPropertyChanged`，Status / Operation / CanSwitch 三项可写并触发通知。
  - 验证失败统一钳为「不可用」+ 操作改「查看原因」+ 禁用切换；当前过滤为「可用」时该行立即从列表消失。

### 变更（Changed）
- **默认窗口尺寸调整**：1320×860 → **1480×920**，足够容纳侧边导航 + 完整表格列（含新增路径列）+ 边距。

## [v0.2.6] - 2026-06-11

### 修复（Fixed）
- **彻底清理 `data\updates` 历史更新包**：v0.2.5 的"下次更新前清理"策略仍会让最近一次的更新包
  与历史发布前的旧目录长期残留（如 v0.2.0、v0.2.4、v0.2.5）。本版改为**应用启动时立即清空**，
  此时 updater 必然已退出、所有更新文件不再被任何进程占用，可一次性释放。
  叠加自更新流程内部的"开始前清理"，形成双层保险，磁盘占用稳定为 0。
- 把清理逻辑下沉到 `DevSwitch.Core/DataRootMaintenance`，并补充 5 个单元测试覆盖
  「多版本残留清空」「目录不存在」「空 dataRoot」「不波及同级 config/sdks/logs」「底层 PurgeDirectoryContents」。

## [v0.2.5] - 2026-06-11

### 新增（Added）
- **Oracle JDK 官方版本源**：在「下载 SDK」对话框新增 Oracle JDK 21 / 25 / 26 LTS 选项，
  直接走 Oracle 官方 NFTC 直链（`download.oracle.com/java/{N}/latest/...`），
  无需登录、无需勾选条款。覆盖企业项目要求 `java -version` 输出含 `Oracle Corporation` 的场景。
  与 Adoptium Temurin 并存，不冲突。
- **国内镜像 injdk.cn 入口**：下载对话框选 Java 类型时显示「国内镜像 injdk.cn」按钮，
  点击打开浏览器跳到 https://injdk.cn/，方便国内用户在官方源较慢时手动下载后用「添加本地 SDK」导入。
- 中英双语本地化文案完整接入。

### 修复（Fixed）
- **SDK 切换：自愈空的真实 current 目录**：当 `data\current\<type>` 是空真实目录（旧版本残留 / 历史
  数据）时，切换会自动删除空目录后建 junction，不再死锁。非空真实目录仍维持安全拒绝、绝不删用户数据。
- **下载完成：自动下探 zip 单根目录**：Temurin / Oracle JDK / Maven / Node / Go 的官方包通常
  多带一层根目录（如 `jdk8u472-b08`、`apache-maven-3.9.9`）。下载 pipeline 现在会识别"解压目录
  仅含 1 个子目录、0 个文件"的形态，把真实 SDK 根登记到 catalog，避免切换/检测时找不到 `bin\java.exe`。
- **自更新：清理历史更新包**：每次自更新开始前清空 `data\updates` 目录，避免 zip + extracted 残留
  随版本数累积膨胀。

### 已知限制（Known Issues）
- Oracle JDK 源的 SHA256 校验文件单独发布在同名 `.sha256` 路径，本期下载流程暂未自动拉取并校验
  （与现有 Maven/Node 源行为一致）；下个版本计划统一引入 `ChecksumUrl` 消费链路。
- Oracle JDK `/latest/` 路径不暴露具体小版本号，列表显示为 `Oracle JDK 21 (latest, x64)`；
  解压后的真实小版本号可在 catalog 详情或 zip 内 `release` 文件中查看。

## [v0.2.4] - 2026-06-11

### 新增（Added）
- **GitHub Actions 自动发布**：推送 `v*` tag 即在 Windows runner 上自动构建、打包并发布
  Release（含 `DevSwitch-win10-x64.zip` 与 `.sha256`），无需本地手动打包。
- CI 专用打包脚本 `scripts/ci-build-package.ps1`：不写死本地路径，从 tag 推导版本号。

### 修复（Fixed）
- **CI 构建失败（MSB4062）**：runner 默认选用 .NET 10 SDK 导致 Windows App SDK 的 PRI
  生成任务找不到 `Microsoft.Build.Packaging.Pri.Tasks.dll`。现通过 `global.json` 固定
  .NET 8 SDK，并用 `vswhere` 定位 Visual Studio 的 AppxPackage 工具目录，经
  `-p:AppxMSBuildToolsPath` 传入，确保 PRI 任务可正确加载。

### 变更（Changed）
- 版本号集中维护在 `src/DevSwitch.App/DevSwitch.App.csproj` 的 `<Version>`。

## [v0.1.0] - 初始版本

### 新增（Added）
- SDK 版本管理：Java / Maven / Node.js / Go 的添加、登记与一键切换。
- **shim 单目录 + junction 软链接**切换方案：切换只改 `current` 软链接指向，
  环境变量与系统 PATH 全程不变，秒级生效、不卡顿。
- 从官方源下载 SDK（Adoptium Temurin / Maven / Node.js / Go）。
- 项目级配置档案：为不同项目保存并一键应用一组 SDK 组合。
- 环境诊断（Doctor）：检查 `current` 链接完整性、PATH 冲突、命令解析版本等。
- 日志查看：异步读取并自动脱敏（打码 token/密码、压缩 PATH）。
- 界面语言热切换：简体中文 / English / 跟随系统，即时生效。
- GitHub 一键自更新：下载 → 校验 → 覆盖 → 重启，全程保护用户数据目录。
- 灵活数据目录：便携 / 固定 C 盘 / 自定义三种模式，支持带进度迁移。

[v0.2.11]: https://github.com/gongzhujiejie/devswitch/releases/tag/v0.2.11
[v0.2.10]: https://github.com/gongzhujiejie/devswitch/releases/tag/v0.2.10
[v0.2.9]: https://github.com/gongzhujiejie/devswitch/releases/tag/v0.2.9
[v0.2.8]: https://github.com/gongzhujiejie/devswitch/releases/tag/v0.2.8
[v0.2.7]: https://github.com/gongzhujiejie/devswitch/releases/tag/v0.2.7
[v0.2.6]: https://github.com/gongzhujiejie/devswitch/releases/tag/v0.2.6
[v0.2.5]: https://github.com/gongzhujiejie/devswitch/releases/tag/v0.2.5
[v0.2.4]: https://github.com/gongzhujiejie/devswitch/releases/tag/v0.2.4
