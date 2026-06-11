# DevSwitch 手工测试准备度巡检

创建日期：2026-06-09

## 结论

当前仓库**还不能编译出可手工点击的 DevSwitch 应用**。

原因是 `src/DevSwitch.App` 目前只有占位 `README.md`，没有 WinUI 3 项目文件、XAML 入口、窗口、应用清单或被加入 `DevSwitch.sln`。现有 solution 只包含 `DevSwitch.Core` 与 `DevSwitch.Tests`，因此即使 Core 可编译，也不会产出可启动 GUI 程序。

## 已确认现状

- 产品路线图 M0 要求：WinUI 3 空窗口可启动、helper exe 可隐藏启动并返回 JSON、本地或 CI 可构建所有项目。
- UI 原型要求：C# + WinUI 3 + XAML + MVVM，左侧 SDK 分类，右侧版本列表。
- 架构设计要求：`DevSwitch.App` 作为 C# WinUI 3/XAML 前端应用，负责 UI、状态展示、用户交互、调用 helper。
- 当前 `src/DevSwitch.App`：仅有占位说明，明确写着后续再创建正式 WinUI 3 项目。
- 当前 `DevSwitch.sln`：未引用 `DevSwitch.App`，也未引用 `DevSwitch.Helper` 工程。
- 当前可见 C# 项目：只有 `src/DevSwitch.Core/DevSwitch.Core.csproj`。

## 距离“编译出应用做手工测试”的最小缺口

### 必需缺口

1. `src/DevSwitch.App/DevSwitch.App.csproj`
   - SDK 类型需要支持 WinUI 3 / Windows App SDK。
   - 目标框架需使用 Windows TFM，例如 `net8.0-windows10.0.19041.0` 或项目约定版本。
   - 需要启用 WinUI。
   - 需要引用 `DevSwitch.Core`。

2. WinUI 应用入口
   - `App.xaml`
   - `App.xaml.cs`
   - `MainWindow.xaml`
   - `MainWindow.xaml.cs`

3. 应用基础配置
   - `Package.appxmanifest` 或非打包 WinUI 所需配置。
   - Windows App SDK 依赖声明。
   - 平台配置建议至少支持 `x64` Debug。

4. Solution 接入
   - 将 `DevSwitch.App` 加入 `DevSwitch.sln`。
   - 追加对应 Debug/Release、x64 平台配置。

5. 最小 UI 可点击面
   - 主窗口标题：`DevSwitch`。
   - 左侧分类：Java、Maven、Node.js、Go。
   - 右侧版本列表：名称、版本、来源、路径、状态、操作。
   - 顶部按钮：新增本地 SDK、下载 SDK、重置、Doctor。

### 手工测试可分两档推进

#### A. 最短可点击壳：假数据 UI

目标：最快让用户打开窗口、点击分类、观察布局和基础交互。

建议内容：

- 使用 WinUI 3 空窗口承载原型布局。
- ViewModel 内置 3-5 条假 SDK 数据：
  - Java：Temurin 17，状态使用中。
  - Java：JDK 8，状态可用。
  - Node.js：Node 22，状态未验证。
  - Go：Go 1.22，状态不可用。
- 按钮先弹出 `ContentDialog` 或提示条，说明功能尚未接入。
- 切换按钮先只更新内存状态，不调用 helper，不写环境变量。

优点：

- 最快验证 UI 布局、文案、控件尺寸、空状态、基础操作路径。
- 不阻塞 Core/Helper 继续演进。
- 手工测试能马上发现 UI 原型缺陷。

不足：

- 不能验证真实导入、切换、环境变量、current link、配置持久化。

#### B. 最小真实链路：接入 Core 读取/检测能力

目标：在 UI 可点击基础上，开始验证真实本地 SDK 导入和状态展示。

建议内容：

- `DevSwitch.App` 引用 `DevSwitch.Core`。
- 导入弹窗选择目录后调用 Core 的 SDK 根目录检测逻辑。
- 使用 Core 的数据根目录解析和 JSON 配置能力加载/保存 SDK 记录。
- 切换操作暂时仍可置灰，等 helper 工程化后再接入。

优点：

- 可以验证 M1/M3 的一部分真实行为。
- UI 缺陷与 Core 数据模型能尽早对齐。

不足：

- helper 未项目化前，M2 切换验收仍无法完整手测。

## 建议最短路径

1. 先补 M0 的 `DevSwitch.App` 最小 WinUI 3 工程，不做完整业务实现。
2. 第一个可测版本采用“假数据 UI”策略：窗口能启动、分类能切换、列表能显示、按钮有占位反馈。
3. 把 `DevSwitch.App` 加入 solution，明确本地构建命令和 VS 启动项目。
4. 第二步再接入 `DevSwitch.Core`：导入弹窗、SDK 根目录检测、配置读写。
5. helper 暂时不要硬接，等 `DevSwitch.Helper` 有正式项目文件和稳定 JSON 协议产物后，再做切换链路手工测试。

## 第一轮手工测试清单

当 WinUI 空壳完成后，优先测试：

- 应用能从 Visual Studio 或 `dotnet build` 产物启动。
- 首屏窗口标题、尺寸、主题显示正常。
- 左侧 Java/Maven/Node.js/Go 分类可点击且高亮正确。
- 无数据分类显示空状态和两个入口按钮。
- 有数据分类显示版本列表字段：名称、版本、来源、路径、状态、操作。
- 使用中状态的“切换”按钮禁用或显示“当前”。
- 不可用状态的“切换”按钮禁用，并能看到原因入口。
- 新增本地 SDK、下载 SDK、重置、Doctor 按钮都有明确占位反馈。
- 关闭窗口无异常。

## 暂不建议现在做的事

- 不建议直接实现下载器、更新、安装包。
- 不建议在 UI 首轮中写环境变量或改 PATH。
- 不建议在 helper 未项目化前做真实切换按钮。
- 不建议一次性搭完整 MVVM、导航、设置、多语言、Doctor 全部页面。

## 手工测试准备度状态

| 项目 | 状态 | 说明 |
| --- | --- | --- |
| Core 项目 | 部分具备 | 已有 `DevSwitch.Core.csproj`，可作为后续 UI 接入基础。 |
| App 项目文件 | 缺失 | `src/DevSwitch.App` 仅 README 占位。 |
| WinUI 空窗口 | 缺失 | 不满足 M0 验收。 |
| Solution 接入 App | 缺失 | `DevSwitch.sln` 只包含 Core 与 Tests。 |
| 可点击 UI | 缺失 | 无 XAML、窗口、ViewModel。 |
| 假数据列表 | 缺失 | 需要用于首轮 UI 手测。 |
| 真实 Core 接入 | 未接入 | App 不存在，尚无法引用 Core。 |
| Helper 接入 | 未就绪 | helper 目前未见正式工程接入 solution。 |

