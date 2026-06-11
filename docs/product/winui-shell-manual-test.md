# DevSwitch WinUI 3 空壳手工测试说明

创建日期：2026-06-09

## 目标

本说明用于在 `DevSwitch.App` 已能产出 WinUI 3 可执行文件后，启动应用并进行第一轮“可点击空壳”手工测试。

当前说明**不假设仓库一定已经可编译**。如果构建阻塞，应先查看 `dotnet build` 输出中的首个错误，再继续手工测试。

## 启动前置条件

- Windows 桌面环境可用。
- 本机存在约定的 .NET SDK 路径：`I:\SoftWare\dotnet`。
- `src\DevSwitch.App` 已完成 WinUI 3 项目骨架，并能构建出 `DevSwitch.App.exe`。
- 若 `DevSwitch.App` 尚未加入 `DevSwitch.sln`，需要先直接构建 App 的 `.csproj`。

## 建议构建命令

优先从仓库根目录运行：

```powershell
$env:DOTNET_ROOT = 'I:\SoftWare\dotnet'
I:\SoftWare\dotnet\dotnet.exe build .\DevSwitch.sln --configuration Debug
```

如果 solution 暂未包含 App，可改为构建 App 项目：

```powershell
$env:DOTNET_ROOT = 'I:\SoftWare\dotnet'
I:\SoftWare\dotnet\dotnet.exe build .\src\DevSwitch.App\DevSwitch.App.csproj --configuration Debug
```

若上述命令失败，不要进入手工测试；先记录并处理构建输出中的第一个有效错误。

## 启动脚本

仓库提供启动脚本草案：

```powershell
.\artifacts\run-devswitch-app.ps1
```

脚本行为：

- 设置当前进程的 `DOTNET_ROOT=I:\SoftWare\dotnet`。
- 在 `src\DevSwitch.App\bin` 下按常见 Debug/Release、x64、Windows TFM 输出路径查找 `DevSwitch.App.exe`。
- 如果找不到产物，会提示先运行构建命令。
- 找到产物后启动最新修改的 `DevSwitch.App.exe`。

## 第一轮手工测试清单

### 1. 窗口打开

- 应用能启动并显示主窗口。
- 窗口标题显示为 `DevSwitch` 或当前设计约定标题。
- 初始窗口尺寸、主题、字体、边距没有明显异常。
- 启动后没有未处理异常弹窗或立即退出。

### 2. 左侧分类导航

逐个点击左侧分类：

- Java
- Maven
- Node.js / Node
- Go

检查点：

- 点击后当前分类高亮正确。
- 右侧内容随分类切换。
- 快速连续点击不会卡死、闪退或出现布局错乱。

### 3. Java / Maven / Node / Go 内容区

对每个分类确认：

- 有假数据时，能看到版本名称、版本号、来源、路径、状态、操作入口。
- 无数据时，显示明确空状态，而不是空白窗口。
- 长路径或长版本名不会严重挤压布局。
- 状态文案能区分“使用中 / 可用 / 不可用 / 未验证”等占位状态。

### 4. 按钮占位行为

点击以下按钮或操作入口：

- 新增本地 SDK
- 下载 SDK
- 重置
- Doctor
- 列表中的切换 / 当前 / 详情 / 原因等占位按钮

检查点：

- 还未接入真实业务时，按钮应给出明确占位反馈，例如提示条、对话框或禁用态。
- 占位按钮不应静默无响应。
- 占位按钮不应修改真实环境变量、PATH、注册表或 current link。

### 5. 空状态

- 切到没有 SDK 数据的分类时，能看到空状态说明。
- 空状态应提供后续入口提示，例如“新增本地 SDK”或“下载 SDK”。
- 空状态文案不应误导用户认为真实扫描已经完成，除非当前版本已接入真实扫描。

### 6. 关闭窗口

- 点击窗口关闭按钮后应用正常退出。
- 关闭时无异常弹窗。
- 再次运行 `artifacts\run-devswitch-app.ps1` 可重新打开应用。

## 记录缺陷建议

每个问题建议记录：

- 构建命令或启动方式。
- App exe 实际路径。
- 点击路径，例如 `Java -> 新增本地 SDK`。
- 期望结果与实际结果。
- 截图或异常文本。

## 与准备度巡检的关系

更完整的准备度判断见：`docs/product/manual-test-readiness.md`。

本文件只描述“WinUI 3 空壳已经能启动后”如何进行第一轮手工点击测试；若构建仍失败，应先回到构建错误定位。
