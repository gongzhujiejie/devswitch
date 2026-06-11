# DevSwitch WinUI 构建阻塞记录

创建日期：2026-06-09

## 结论

当前仓库已经具备 `DevSwitch.App` WinUI 3 空壳工程、假数据 ViewModel、主窗口 XAML 布局，并且已加入 `DevSwitch.sln`。

但是当前机器还不能产出可启动的 `DevSwitch.App.exe`。

## 已完成

- `src/DevSwitch.App/DevSwitch.App.csproj`
- `App.xaml` / `App.xaml.cs`
- `MainWindow.xaml` / `MainWindow.xaml.cs`
- `Models/SdkVersionRow.cs`
- `ViewModels/MainWindowViewModel.cs`
- `DevSwitch.App` 已加入 `DevSwitch.sln`
- `Microsoft.WindowsAppSDK` 已成功还原到 NuGet 缓存：
  - `C:\Users\11714\.nuget\packages\microsoft.windowsappsdk\1.5.240802000`
- XAML 生成中间文件已经出现：
  - `App.g.cs`
  - `MainWindow.g.cs`
  - `App.xbf`
  - `MainWindow.xbf`

## 当前构建错误

使用 `I:\SoftWare\dotnet\dotnet.exe build src\DevSwitch.App\DevSwitch.App.csproj` 时，RID 问题已通过以下属性部分规避：

```xml
<UseRidGraph>true</UseRidGraph>
<RuntimeIdentifier>win10-x64</RuntimeIdentifier>
<RuntimeIdentifiers>win10-x64</RuntimeIdentifiers>
<Platform>x64</Platform>
<Platforms>x64</Platforms>
```

之后新的阻塞为：

```text
Microsoft.Build.Packaging.Pri.Tasks.ExpandPriContent
Could not load file or assembly:
I:\SoftWare\dotnet\sdk\11.0.100-preview.4.26230.115\Microsoft\VisualStudio\v18.0\AppxPackage\Microsoft.Build.Packaging.Pri.Tasks.dll
```

也就是当前 .NET 11 preview SDK 路径下缺少 WinUI/PRI 构建任务。

使用 Visual Studio MSBuild 时，VS MSBuild 又无法解析 I 盘独立 dotnet SDK：

```text
MSB4236: 找不到指定的 SDK Microsoft.NET.Sdk
```

即使显式指定 SDK resolver，也会卡在 WorkloadAutoImportPropsLocator。

## 本机缺口判断

当前机器发现：

- 有 Visual Studio 2022 Community：
  - `I:\SoftWare\Microsoft Visual Studio\2022\Community`
- 有 MSBuild：
  - `I:\SoftWare\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe`
- 有 Windows SDK 目录：
  - `C:\Program Files (x86)\Windows Kits\10`
- 有 I 盘 dotnet：
  - `I:\SoftWare\dotnet`

但未找到：

- `Microsoft.Build.Packaging.Pri.Tasks.dll`
- WinUI / UWP / MSIX packaging 所需构建任务
- 能被 VS MSBuild 正常解析的本机 .NET SDK 安装

## 建议修复路线

### 首选路线：补 Visual Studio 组件

打开 Visual Studio Installer，修改当前 VS 2022 Community，安装与 WinUI 3 相关的工作负载/组件：

- .NET desktop development
- Windows application development
- Windows App SDK C# templates
- Windows 10 SDK / Windows 11 SDK
- MSIX Packaging Tools
- UWP / Appx packaging build tools（若组件列表存在）

安装后重新运行：

```powershell
I:\SoftWare\dotnet\dotnet.exe build .\src\DevSwitch.App\DevSwitch.App.csproj --configuration Debug
```

或用 VS 打开 `DevSwitch.sln` 构建 `DevSwitch.App`。

### 次选路线：安装稳定 .NET 8 SDK

当前只有 `.NET 11 preview SDK`，它对旧 Windows App SDK / win10 RID 行为兼容性较差。

建议额外安装稳定 .NET 8 SDK 到：

```text
I:\SoftWare\dotnet
```

再添加 `global.json` 锁定 SDK 版本。

### 不建议路线：手动复制 PRI task dll

不建议手动复制 `Microsoft.Build.Packaging.Pri.Tasks.dll` 或修改 dotnet SDK 目录。那会污染工具链，且后续更新/卸载风险高。

## 当前是否可手工测试 UI

不能。

原因：尚未生成：

```text
src\DevSwitch.App\bin\...\DevSwitch.App.exe
```

但 UI 文件已就绪。补齐构建工具链后，目标就是生成该 exe，然后执行：

```powershell
.\artifacts\run-devswitch-app.ps1
```
