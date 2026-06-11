# 更新日志（Changelog）

本项目所有重要变更都会记录在此文件。

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

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

[v0.2.4]: https://github.com/gongzhujiejie/devswitch/releases/tag/v0.2.4
