# DevSwitch 架构设计文档

创建日期：2026-06-08

## 1. 总体架构

DevSwitch 首版采用 Windows 原生桌面架构：

```text
C# WinUI 3 GUI
  |
  | stdin/stdout JSON
  v
Hidden C++ Win32 Helper Exe
  |
  +-- HKCU\Environment
  +-- Directory Junction / Symlink
  +-- File operations
  +-- WM_SETTINGCHANGE broadcast
```

核心原则：

- GUI 优先，不公开 CLI 工作流。
- 默认普通用户权限运行。
- 系统级操作集中到隐藏 C++ helper exe。
- helper 不弹出 CMD 窗口。
- 数据存储使用 JSON 文件，不使用 SQLite。
- 切换通过稳定 current 入口完成。

## 2. 前端技术栈

DevSwitch 的前端不是 Web 前端，也不使用 HTML/CSS/JavaScript、Electron 或 WebView 作为主界面。首版前端采用 Windows 原生 GUI：

```text
C# + WinUI 3 + XAML + MVVM
```

推荐前端组合：

| 层 | 技术 | 说明 |
| --- | --- | --- |
| UI 框架 | WinUI 3 / Windows App SDK | 原生 Windows 桌面 UI，负责窗口、页面、控件和主题 |
| UI 标记 | XAML | 定义页面布局、样式、资源、数据绑定 |
| UI 语言 | C# | 编写 ViewModel、事件桥接、状态绑定、异步任务调度 |
| 架构模式 | MVVM | 页面只负责展示，业务状态放在 ViewModel |
| MVVM 库 | CommunityToolkit.Mvvm | 用于 `ObservableObject`、`RelayCommand`、属性通知和命令绑定 |
| 导航 | WinUI 3 NavigationView + Frame | 实现左侧 SDK 分类和页面切换 |
| 列表 | WinUI 3 ListView/Grid 布局 | 实现版本列表；首版不依赖重型第三方表格控件 |
| 样式 | WinUI 3 ResourceDictionary | 管理主题、颜色、间距、按钮和状态标签样式 |
| 本地化 | `.resw` 资源文件 | 支持简体中文和 English，默认跟随系统语言 |

前端原则：

- 不使用 Electron，避免启动慢和体积大。
- 不使用 WebView 做主界面，避免引入 Web 前端复杂度。
- 不把业务逻辑写进 XAML code-behind。
- 页面通过 ViewModel 绑定 SDK 列表、下载任务、doctor 结果。
- 版本列表首版使用原生 ListView + Grid 行布局；如果后续需要复杂排序、列拖拽、虚拟化表格，再评估专业 DataGrid 控件。

## 3. 模块划分

```text
DevSwitch.App
  C# WinUI 3/XAML 前端应用，负责 UI、状态展示、用户交互、调用 helper。

DevSwitch.Core
  C# 领域模型与服务，负责 SDK 记录、配置读取、版本源模型、下载任务状态。

DevSwitch.Helper
  C++ Win32 hidden exe，负责 Junction/Symlink、注册表环境变量、PATH 写入、广播、文件删除。

DevSwitch.Downloader
  C# 下载器，负责多线程、断点续传、校验、解压调度。

DevSwitch.Sources
  SDK 版本源适配层，负责 Java/Node.js/Go/Maven 版本列表和下载元数据。
```

## 3. 数据根目录

默认：

```text
%LOCALAPPDATA%\DevSwitch
```

便携模式：

```text
<DevSwitch.exe 所在目录>\data
```

目录结构：

```text
%DEVSWITCH_HOME%\
  config\
    settings.json
    sdks.json
    sources.json
    downloads.json
    validation-cache.json
  current\
    java      -> selected JDK root
    maven     -> selected Maven root
    node      -> selected Node root
    go        -> selected Go root
  sdks\
    java\
    maven\
    node\
    go\
  downloads\
    cache\
    temp\
  logs\
    app-yyyyMMdd.log
    helper-yyyyMMdd.log
    download-yyyyMMdd.log
  backups\
    config\
```

## 4. 配置文件

### 4.1 settings.json

```json
{
  "schemaVersion": 1,
  "dataRoot": "%LOCALAPPDATA%\\DevSwitch",
  "language": "auto",
  "download": {
    "parallelism": 4,
    "keepArchives": false,
    "preferredMirror": null
  },
  "compatibility": {
    "setJdkHome": false,
    "setM2Home": false
  },
  "update": {
    "source": "github-releases",
    "fallbackSource": null
  }
}
```

### 4.2 sdks.json

```json
{
  "schemaVersion": 1,
  "active": {
    "java": "sdk-id-java-17",
    "maven": "sdk-id-maven-399",
    "node": "sdk-id-node-22",
    "go": "sdk-id-go-122"
  },
  "items": [
    {
      "id": "sdk-id-java-17",
      "type": "java",
      "name": "Temurin 17",
      "version": "17.0.10",
      "distribution": "temurin",
      "arch": "x64",
      "source": "managed",
      "path": "%DEVSWITCH_HOME%\\sdks\\java\\temurin-17.0.10-x64",
      "status": "unverified",
      "createdAt": "2026-06-08T00:00:00Z",
      "lastVerifiedAt": null
    }
  ]
}
```

### 4.3 downloads.json

```json
{
  "schemaVersion": 1,
  "tasks": [
    {
      "id": "download-task-id",
      "sdkType": "java",
      "version": "17.0.10",
      "distribution": "temurin",
      "arch": "x64",
      "url": "https://example.invalid/archive.zip",
      "expectedSha256": "...",
      "status": "paused",
      "bytesTotal": 0,
      "bytesCompleted": 0,
      "chunks": []
    }
  ]
}
```

## 5. 配置迁移

每个 JSON 配置文件包含 `schemaVersion`。

启动流程：

```text
1. 读取 settings.json。
2. 检查 schemaVersion。
3. 如果低于当前版本，复制到 backups/config/。
4. 执行逐版本迁移。
5. 写入临时文件。
6. 原子替换正式文件。
7. 迁移失败则保留旧配置并提示用户。
```

写入策略：

```text
file.json.tmp -> flush -> replace file.json
```

## 6. Helper JSON 协议

GUI 通过 stdin 向 helper 发送 JSON，helper 通过 stdout 返回 JSON。

### 6.1 请求格式

```json
{
  "requestId": "uuid",
  "operation": "switchSdk",
  "payload": {
    "sdkType": "java",
    "currentPath": "%DEVSWITCH_HOME%\\current\\java",
    "targetPath": "D:\\Programs\\java\\jdk-17.0.10",
    "linkPreference": "junction-first"
  }
}
```

### 6.2 响应格式

```json
{
  "requestId": "uuid",
  "success": true,
  "errorCode": null,
  "message": "Switched java successfully.",
  "details": {
    "linkType": "junction",
    "changed": ["currentLink"]
  }
}
```

### 6.3 主要操作

```text
initEnvironment
switchSdk
resetEnvironment
createCurrentLink
removeCurrentLink
writeUserEnvironment
appendManagedPathEntries
broadcastEnvironmentChanged
deleteManagedSdk
inspectLink
```

## 7. 切换事务

切换流程：

```text
1. GUI 请求切换。
2. Core 获取目标 SDK 记录。
3. 检查目标路径和关键文件。
4. helper 读取 current 当前指向。
5. helper 创建临时链接或准备替换。
6. helper 替换 current 链接。
7. helper 检查 current 指向目标。
8. GUI/Core 做轻量验证。
9. 成功则更新 active 配置。
10. 失败则 helper 恢复切换前 current 指向。
```

原则：

```text
要么切换成功，要么保持原版本不变。
```

## 8. 环境变量写入

写入位置：

```text
HKCU\Environment
```

写入类型：

```text
REG_EXPAND_SZ
```

默认变量：

```text
DEVSWITCH_HOME=%LOCALAPPDATA%\DevSwitch
JAVA_HOME=%DEVSWITCH_HOME%\current\java
MAVEN_HOME=%DEVSWITCH_HOME%\current\maven
GOROOT=%DEVSWITCH_HOME%\current\go
```

可选兼容变量：

```text
JDK_HOME=%DEVSWITCH_HOME%\current\java
M2_HOME=%DEVSWITCH_HOME%\current\maven
```

托管 PATH 片段：

```text
%DEVSWITCH_HOME%\current\java\bin
%DEVSWITCH_HOME%\current\maven\bin
%DEVSWITCH_HOME%\current\node
%DEVSWITCH_HOME%\current\go\bin
```

写入后广播：

```text
WM_SETTINGCHANGE
lParam = "Environment"
```

## 9. 版本识别

### 9.1 JDK

关键文件：

```text
release
bin\java.exe
bin\javac.exe
```

版本命令：

```text
java -version
javac -version
```

### 9.2 Maven

关键文件：

```text
bin\mvn.cmd
```

版本命令：

```text
mvn -v
```

### 9.3 Node.js

关键文件：

```text
node.exe
npm.cmd
npx.cmd
```

版本命令：

```text
node -v
npm -v
```

### 9.4 Go

关键文件：

```text
bin\go.exe
bin\gofmt.exe
```

版本命令：

```text
go version
```

## 10. 下载器设计

下载器支持：

- 多线程。
- 断点续传。
- 默认并发 4。
- 可调并发 1-8。
- HTTP Range。
- 临时 chunk 状态保存。
- SHA256 或签名校验。
- 解压后登记托管 SDK。

下载状态：

```text
queued
running
paused
verifying
extracting
completed
failed
cancelled
```

下载完成流程：

```text
1. 合并或确认 chunk 文件。
2. 计算 SHA256 或执行签名校验。
3. 校验失败：标记 failed，不解压，不登记。
4. 校验成功：解压到 sdks/<type>/<distribution>-<version>-<arch>/。
5. 识别真实版本。
6. 写入 sdks.json。
7. 默认删除安装包，除非 keepArchives=true。
```

## 11. Doctor 诊断

Doctor 输出分为：

```text
通过
警告
错误
建议
```

检查项：

- 数据根目录可写性。
- current 链接完整性。
- HKCU 环境变量。
- DevSwitch PATH 片段。
- PATH 前序冲突。
- 命令解析版本。
- `GOTOOLCHAIN`。
- npm prefix。
- helper 可用性。
- 配置 schemaVersion。

Doctor 不自动修复外部冲突，只提供手动建议。

## 12. 错误等级

| 等级 | 场景 | UI 表现 |
| --- | --- | --- |
| Info | 切换成功、下载完成 | 提示条或 toast |
| Warning | PATH 前序冲突、未验证 | 黄色提示、doctor 建议 |
| Error | 下载失败、切换失败但已回滚 | 错误提示 + 操作建议 |
| Fatal | 配置迁移失败、helper 不可用 | 弹窗 + 导出诊断包入口 |

## 13. 日志

日志文件：

```text
logs\app-yyyyMMdd.log
logs\helper-yyyyMMdd.log
logs\download-yyyyMMdd.log
```

策略：

- 默认保留 14 天。
- 单文件最大 20MB。
- 敏感字段脱敏。
- 诊断包导出前再次脱敏。

## 14. 分发

产物：

- 普通安装包。
- 免安装 zip。

安装包职责：

- 安装 GUI 和 helper。
- 创建开始菜单入口。
- 不默认写系统级环境变量。

免安装 zip：

- 解压即可运行。
- 若 exe 同目录存在 `data\`，自动进入便携模式。

## 15. 架构风险

| 风险 | 影响 | 应对 |
| --- | --- | --- |
| PATH 被外部项遮蔽 | 用户以为切换失败 | doctor 检测并给手动建议 |
| Junction 创建失败 | 无法切换 | fallback 到 Symlink，并提示 Developer Mode/权限原因 |
| 下载源校验信息缺失 | 无法确认可信 | 不登记或标记源不可用 |
| 配置迁移失败 | 启动异常 | 备份旧配置，提示修复 |
| helper 崩溃 | 操作失败 | GUI 捕获错误，导出诊断包 |
| 旧终端环境不刷新 | 用户看到旧版本 | 初始化/重置后提示重启终端 |
