# 更新日志（Changelog）

本项目所有重要变更都会记录在此文件。

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

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

[v0.2.5]: https://github.com/gongzhujiejie/devswitch/releases/tag/v0.2.5
[v0.2.4]: https://github.com/gongzhujiejie/devswitch/releases/tag/v0.2.4
