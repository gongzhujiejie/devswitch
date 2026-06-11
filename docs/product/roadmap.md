# DevSwitch 开发里程碑

创建日期：2026-06-08

## 总体原则

开发顺序遵循：

```text
核心切换能力 -> 数据与配置 -> GUI 管理 -> 环境初始化 -> 诊断 -> 下载器 -> 分发更新
```

优先证明 current Junction/Symlink 切换链路可靠，再扩展下载、更新和高级诊断。

## M0：工程骨架

### 目标

建立基础工程结构和构建链路。

### 交付物

```text
DevSwitch.App        C# WinUI 3
DevSwitch.Core       C# 核心模型与服务
DevSwitch.Helper     C++ Win32 hidden exe
DevSwitch.Tests      单元测试/集成测试
```

### 验收

- WinUI 3 空窗口可启动。
- helper exe 可隐藏启动并返回 JSON。
- CI 或本地脚本可构建所有项目。
- 日志基础能力可写入 `%DEVSWITCH_HOME%\logs`。

### 风险

- WinUI 3 打包与运行时依赖。
- C# 调用 helper 的进程隐藏和 stdout 捕获。

## M1：数据根目录与配置系统

### 目标

实现 DevSwitch 数据根目录、便携模式和 JSON 配置读写。

### 交付物

- 默认 `%LOCALAPPDATA%\DevSwitch`。
- 检测 exe 同目录 `data\` 自动便携。
- `settings.json`、`sdks.json`。
- schemaVersion。
- 配置备份与迁移框架。
- 原子写入。

### 验收

- 首次启动自动初始化数据根目录。
- 可切换到自定义数据根目录。
- JSON 写入失败不会破坏旧配置。
- 迁移失败会保留旧配置并提示。

## M2：Helper 切换核心

### 目标

实现 Windows 用户级切换的底层能力。

### 交付物

- helper JSON 协议。
- 创建/删除/检查 current Junction。
- Junction 失败后 Symlink fallback。
- 切换失败回滚。
- 写入 `HKCU\Environment`。
- 写入 `REG_EXPAND_SZ`。
- 广播 `WM_SETTINGCHANGE`。

### 验收

- 可把 `current\java` 指向任意合法 JDK 根目录。
- 切换失败后 current 恢复原指向。
- 用户级环境变量写入正确。
- 新打开终端读取到新变量。

### 风险

- Junction 对特殊路径或网络路径兼容性。
- Symlink 需要 Developer Mode 或权限。
- 已打开终端不会自动刷新环境。

## M3：本地导入与 GUI 版本列表

### 目标

实现类似截图的核心 GUI。

### 交付物

- 左侧 SDK 分类视图。
- 右侧版本列表。
- 本地导入弹窗。
- 版本名称自定义。
- SDK 根目录检测。
- 状态：使用中、可用、不可用、未验证。
- 操作：切换、验证、编辑、删除。

### 验收

- 可导入 JDK、Maven、Node.js、Go 根目录。
- 误选 `bin` 目录能提示并建议父目录。
- 可一键切换并更新 UI 状态。
- 删除外部 SDK 只删除记录。

## M4：环境初始化、重置与 Doctor

### 目标

补齐用户环境配置和诊断闭环。

### 交付物

- 首次初始化向导。
- 托管 PATH 片段写入。
- 重置工具环境。
- Doctor 页面。
- 手动命令验证。
- PATH 冲突提示。
- 脱敏诊断包。

### 验收

- Doctor 能检测 DevSwitch PATH 是否存在。
- Doctor 能发现 Oracle javapath 或旧 Java/Node/Go/Maven 遮蔽。
- 重置不会删除外部 SDK。
- 诊断包不包含完整 PATH、完整环境变量、Token 或认证信息。

## M5：在线下载器

### 目标

实现官方源 + 镜像源下载、校验、解压和登记。

### 交付物

- Java：Temurin + Zulu + Corretto。
- Node.js：LTS、Current、历史版本。
- Go 下载源。
- Maven 下载源。
- x64 + arm64 下载。
- 多线程下载。
- 断点续传。
- 默认并发 4，可调 1-8。
- SHA256 或签名校验。
- 解压到 `sdks\<type>\<distribution>-<version>-<arch>`。
- 下载完成默认删除安装包。

### 验收

- 可下载并登记至少一个 Java、Node.js、Go、Maven 版本。
- 校验失败不解压、不登记。
- 中断下载后可继续。
- 关闭窗口时有任务会确认取消。

### 风险

- 不同源的版本 API 不一致。
- 镜像源缺少校验信息。
- 部分服务器不支持 HTTP Range。
- 解压包内部目录结构不统一。

## M6：设置、多语言、更新与分发

### 目标

完成首版发布所需的外围能力。

### 交付物

- 简体中文 + English。
- 设置页。
- 手动检查更新。
- GitHub Releases + 备用更新源。
- 普通安装包。
- 免安装 zip。
- 便携模式验证。

### 验收

- 默认跟随系统语言，可手动切换。
- 点击检查更新才联网。
- 安装包可安装、卸载。
- zip 解压可运行。
- exe 同目录存在 `data\` 时进入便携模式。

## M7：首版验收与风险收口

### 目标

发布前稳定性验证。

### 测试矩阵

| 场景 | 验证点 |
| --- | --- |
| 无管理员权限 | 初始化、导入、切换、doctor 正常 |
| 已存在 JAVA_HOME | DevSwitch 写入用户级变量并提示重启终端 |
| PATH 有旧 Java | doctor 提示遮蔽，不自动删除 |
| JDK 路径失效 | 状态为不可用，禁止切换 |
| 切换失败 | 自动回滚 |
| 下载失败 | 可重试或继续 |
| 校验失败 | 不解压、不登记 |
| 便携模式 | 使用 exe 同目录 data |
| 配置迁移失败 | 备份旧配置并提示 |

## 后续版本候选

首版之后再考虑：

- 项目级 `.devswitch.toml`。
- shim 自动切换。
- 托盘快速切换。
- CLI。
- Gradle、Python、PHP、Flutter、Rust、.NET。
- 下载缓存上限和自动清理策略。
- 企业版自定义源和离线包索引。
