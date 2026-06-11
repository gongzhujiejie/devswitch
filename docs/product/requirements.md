# DevSwitch 产品需求文档

创建日期：2026-06-08

## 1. 产品定位

DevSwitch 是一个面向 Windows 开发者的图形化开发工具链版本切换工具。首版聚焦当前 Windows 用户环境下的全局版本切换，帮助用户管理并快速切换本机的 JDK、Maven、Node.js 和 Go 版本。

首版采用 GUI 优先，不要求用户通过命令行完成日常管理和切换操作。GUI 前端使用 C# + WinUI 3 + XAML + MVVM 开发，不使用 Electron、WebView 或 HTML/CSS/JavaScript 作为主界面。

## 2. 目标用户

- 需要在多个项目之间切换 JDK、Maven、Node.js 或 Go 版本的 Windows 开发者。
- 不希望手动编辑环境变量、PATH 或注册表的用户。
- 需要同时管理本地已有 SDK 与在线下载 SDK 的用户。
- 希望工具启动快、切换快、权限风险低的用户。

## 3. 核心目标

- 提供类似截图的左侧 SDK 分类 + 右侧版本列表界面。
- 支持 JDK、Maven、Node.js、Go 四类开发工具链。
- 支持本地导入 SDK 根目录。
- 支持官方源 + 镜像源在线下载。
- 使用 current Junction/Symlink 实现高速切换。
- 只写入当前用户级环境变量，不要求管理员权限。
- 只管理 DevSwitch 自身 PATH 片段，不自动清理用户已有 PATH。
- 提供完整 doctor 诊断和手动修复建议。

## 4. 非目标

首版不做：

- 项目级自动切换，例如 `.sdkx.toml` 或 shim。
- 命令行优先工作流。
- 系统级环境变量切换。
- 自动清理用户 PATH 中的外部冲突项。
- Maven 本地仓库、settings.xml、profiles 管理。
- npm 全局包目录管理。
- Go 的 `GOTOOLCHAIN`、`GOPATH`、`GOBIN` 接管。
- 后台常驻、托盘快速切换、后台自动更新。
- MSIX 优先分发。

## 5. 首版 SDK 范围

| SDK 类型 | 首版能力 | 环境变量策略 |
| --- | --- | --- |
| JDK | 本地导入、在线下载、切换、验证 | 默认设置 `JAVA_HOME`，可选 `JDK_HOME` |
| Maven | 本地导入、在线下载、切换、验证 | 默认设置 `MAVEN_HOME`，可选 `M2_HOME` |
| Node.js | 本地导入、在线下载、切换、验证 | PATH 指向 `current\node`，不设置 `NODE_HOME` |
| Go | 本地导入、在线下载、切换、验证 | 设置 `GOROOT`，不管理 `GOPATH` / `GOBIN` |

## 6. 来源策略

### 6.1 本地导入

- 用户选择 SDK 根目录。
- 不接受 `bin` 目录作为最终导入目录。
- 如果用户误选 `bin`，工具应识别并提示使用父目录。
- 本地导入的 SDK 只登记外部路径，不复制到 DevSwitch 数据根目录。
- 删除外部 SDK 记录时，只移除登记，不删除实体目录。

### 6.2 在线下载

- 下载支持 x64 + arm64。
- 本地导入不限架构。
- 在线下载的 SDK 解压到数据根目录，作为托管 SDK。
- 托管 SDK 目录命名：

```text
sdks\<sdkType>\<distribution>-<version>-<arch>\
```

示例：

```text
sdks\java\temurin-17.0.10-x64\
sdks\maven\apache-maven-3.9.9-any\
sdks\node\nodejs-22.11.0-x64\
sdks\go\go-1.22.5-x64\
```

### 6.3 下载源

- Java：Temurin + Zulu + Corretto。
- Node.js：支持 LTS、Current、历史版本；默认展示 LTS + Current。
- Go：下载并切换全局 Go，不接管 `GOTOOLCHAIN`。
- Maven：只切 Maven 版本，不管理 Maven 用户配置。

## 7. 数据根目录

默认数据根目录：

```text
%LOCALAPPDATA%\DevSwitch
```

允许用户在首次启动或设置页改为其他可写目录。

便携模式规则：

```text
如果 DevSwitch.exe 同目录存在 data\，则使用 exe 同目录 data\ 作为数据根目录。
```

核心变量：

```text
DEVSWITCH_HOME = 数据根目录
```

## 8. 环境变量策略

- 只写入 `HKCU\Environment`。
- 默认不请求管理员权限。
- 使用 `REG_EXPAND_SZ` 写入可展开变量。
- 首次初始化或重置后广播环境变更。
- 明确提示：已打开的终端需要重启后才会读取新环境。

推荐变量：

```text
DEVSWITCH_HOME=%LOCALAPPDATA%\DevSwitch
JAVA_HOME=%DEVSWITCH_HOME%\current\java
MAVEN_HOME=%DEVSWITCH_HOME%\current\maven
GOROOT=%DEVSWITCH_HOME%\current\go
```

托管 PATH 片段：

```text
%DEVSWITCH_HOME%\current\java\bin
%DEVSWITCH_HOME%\current\maven\bin
%DEVSWITCH_HOME%\current\node
%DEVSWITCH_HOME%\current\go\bin
```

## 9. 切换行为

- 使用 `current` 入口作为切换面。
- 默认创建 Directory Junction。
- Junction 失败时再尝试 Symlink。
- 切换时进行轻量验证：
  - current 指向目标 SDK。
  - 关键命令文件存在。
- 切换失败自动回滚到切换前状态。
- 不在每次切换后自动运行版本命令。
- 用户可手动触发命令验证或 doctor。

## 10. UI 需求

主界面采用左侧 SDK 分类 + 右侧版本列表。

左侧分类：

```text
Java
Maven
Node.js
Go
```

右侧表格字段：

```text
名称 / 版本 / 来源 / 路径 / 状态 / 操作
```

状态：

- 使用中
- 可用
- 不可用
- 未验证

操作：

- 切换
- 验证
- 编辑
- 删除

顶部常用操作：

- 新增本地 SDK
- 下载 SDK
- 重置

## 11. 下载器需求

- 支持多线程下载。
- 支持断点续传。
- 默认并发数 4。
- 设置中可调并发数 1-8。
- 下载完成后必须校验 SHA256 或签名。
- 校验失败不解压、不登记。
- 镜像源只作为加速来源，可信结果以官方校验或可信清单为准。
- 解压并登记成功后，默认删除安装包。
- 设置中可开启保留下载缓存。
- 关闭窗口时如果存在下载或解压任务，应弹窗确认；确认关闭则取消任务并保留可清理临时文件记录。

## 12. Doctor 诊断需求

Doctor 页面检查：

- 数据根目录是否可写。
- current 链接是否存在且指向正确。
- `HKCU\Environment` 中 `DEVSWITCH_HOME`、`JAVA_HOME`、`MAVEN_HOME`、`GOROOT` 是否正确。
- 用户 PATH 是否包含 DevSwitch 托管 PATH 片段。
- PATH 前序冲突：Oracle `javapath`、旧 Java、旧 Node、旧 Go、旧 Maven。
- 命令解析结果：`java -version`、`javac -version`、`mvn -v`、`node -v`、`npm -v`、`go version`。
- Go 的 `GOTOOLCHAIN` 当前值。
- npm prefix 当前值。

Doctor 只提供手动修复建议，不自动修改外部 PATH 或用户其他配置。

## 13. 重置需求

重置工具环境可以恢复：

- DevSwitch 自身管理的环境变量。
- DevSwitch 托管 PATH 片段。
- current 入口。
- 配置中的当前使用状态。

重置不会自动删除：

- 外部 SDK 实体目录。
- 用户自己写入的 PATH 项。
- Maven settings.xml。
- npm 全局包。
- Go GOPATH/GOMODCACHE。

托管 SDK 文件只有在用户明确确认后才允许删除。

## 14. 日志与诊断包

日志目录：

```text
%DEVSWITCH_HOME%\logs\
```

日志文件：

```text
app-yyyyMMdd.log
helper-yyyyMMdd.log
download-yyyyMMdd.log
```

策略：

- 默认保留 14 天。
- 单文件最大 20MB。
- 不记录 Token、认证信息或完整环境变量值。

诊断包包含：

- 脱敏日志。
- 配置摘要。
- SDK 记录摘要。
- doctor 结果。
- Windows 版本、CPU 架构、DevSwitch 版本。
- helper 错误码。

诊断包不包含：

- 完整环境变量 dump。
- 完整 PATH 原文。
- 用户目录全量扫描结果。
- Maven settings.xml 内容。
- npm token 或私服认证。

## 15. 更新与分发

分发方式：

- 普通安装包。
- 免安装 zip。

更新方式：

- 手动检查更新。
- 默认使用 GitHub Releases。
- 支持配置备用更新源。
- 不在启动时静默联网。

## 16. 验收标准

首版完成时应满足：

- 无管理员权限下完成首次初始化。
- 可导入本地 JDK、Maven、Node.js、Go 根目录。
- 可下载并登记至少一个 JDK、Node.js、Go、Maven 版本。
- 可一键切换 SDK，并在失败时回滚。
- 新打开终端能解析到切换后的命令版本。
- doctor 能发现 PATH 冲突并给出手动建议。
- 关闭窗口无后台常驻。
- 日志和诊断包可用于排错且不泄露敏感配置。
