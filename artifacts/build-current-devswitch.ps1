# 文件用途：编译当前 DevSwitch 可用产物：C# Core/Test 与 C++ Helper。
# 创建日期：2026-06-09
# 语言版本要求：Windows PowerShell 5.1+
# 依赖库/工具：I:\SoftWare\dotnet\dotnet.exe、MinGW g++.exe
# NOTE: 合法授权学习使用，仅限本地环境。本脚本只修复当前进程环境，不写注册表。

$ErrorActionPreference = 'Stop'
Set-Location 'I:\lpppp\envoy'

# NOTE: 自动化 Shell 可能缺少标准 Windows 用户环境变量，NuGet restore 依赖这些变量。
$env:DOTNET_ROOT = 'I:\SoftWare\dotnet'
$env:LOCALAPPDATA = 'C:\Users\11714\AppData\Local'
$env:APPDATA = 'C:\Users\11714\AppData\Roaming'
$env:TEMP = 'C:\Users\11714\AppData\Local\Temp'
$env:TMP = 'C:\Users\11714\AppData\Local\Temp'
$env:NUGET_PACKAGES = 'C:\Users\11714\.nuget\packages'
$env:DOTNET_CLI_HOME = 'C:\Users\11714'
$env:Path = 'I:\SoftWare\dotnet;I:\SoftWare\dotnet\tools;C:\Windows\System32;C:\Windows;C:\Windows\System32\WindowsPowerShell\v1.0;C:\Users\11714\scoop\apps\mingw\current\bin;C:\Users\11714\scoop\shims;' + $env:Path

New-Item -ItemType Directory -Path 'artifacts\bin', $env:LOCALAPPDATA, $env:APPDATA, $env:TEMP, $env:NUGET_PACKAGES -Force | Out-Null

# NOTE: 编译 C++ helper。M0/M1 阶段用 MinGW 静态链接，后续迁移到正式 MSVC Win32 hidden helper。
& 'C:\Users\11714\scoop\apps\mingw\current\bin\g++.exe' -std=c++20 -O2 -Wall -Wextra -static -static-libgcc -static-libstdc++ -o 'artifacts\bin\DevSwitch.Helper.exe' 'src\DevSwitch.Helper\main.cpp'
if (-not (Test-Path 'artifacts\bin\DevSwitch.Helper.exe')) {
    throw 'DevSwitch.Helper.exe was not produced.'
}

# NOTE: 编译当前 solution。当前 solution 暂只包含 Core 与 Tests，尚未包含 WinUI App。
& 'I:\SoftWare\dotnet\dotnet.exe' build 'DevSwitch.sln' --configuration Debug --verbosity minimal

# NOTE: 运行测试，确认编译产物可用。
& 'I:\SoftWare\dotnet\dotnet.exe' test 'tests\DevSwitch.Tests\DevSwitch.Tests.csproj' --configuration Debug --verbosity minimal --no-restore

Write-Output 'Build completed.'
Write-Output 'Helper: artifacts\bin\DevSwitch.Helper.exe'
Write-Output 'Core: src\DevSwitch.Core\bin\Debug\net8.0\DevSwitch.Core.dll'
Write-Output 'Tests: tests\DevSwitch.Tests\bin\Debug\net8.0\DevSwitch.Tests.dll'
