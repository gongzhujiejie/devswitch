# 文件用途：在当前机器上用 I:\SoftWare\dotnet 和 MinGW 运行 DevSwitch 全量测试验证。
# 创建日期：2026-06-09
# 语言版本要求：Windows PowerShell 5.1+
# 依赖库/工具：I:\SoftWare\dotnet\dotnet.exe、C:\Users\11714\scoop\apps\mingw\current\bin\g++.exe
# NOTE: 合法授权学习使用，仅限本地环境。本脚本只修复当前进程环境，不写注册表。

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
Set-Location $repoRoot

# NOTE: 自动化 Shell 可能缺少这些 Windows 用户环境变量；NuGet restore 依赖它们。
$env:DOTNET_ROOT = 'I:\SoftWare\dotnet'
$env:LOCALAPPDATA = 'C:\Users\11714\AppData\Local'
$env:APPDATA = 'C:\Users\11714\AppData\Roaming'
$env:TEMP = 'C:\Users\11714\AppData\Local\Temp'
$env:TMP = 'C:\Users\11714\AppData\Local\Temp'
$env:NUGET_PACKAGES = 'C:\Users\11714\.nuget\packages'
$env:DOTNET_CLI_HOME = 'C:\Users\11714'
$env:Path = 'I:\SoftWare\dotnet;I:\SoftWare\dotnet\tools;C:\Windows\System32;C:\Windows;C:\Windows\System32\WindowsPowerShell\v1.0;C:\Users\11714\scoop\apps\mingw\current\bin;C:\Users\11714\scoop\shims;' + $env:Path

New-Item -ItemType Directory -Path 'artifacts\bin', $env:LOCALAPPDATA, $env:APPDATA, $env:TEMP, $env:NUGET_PACKAGES -Force | Out-Null

$helperOutput = 'artifacts\bin\DevSwitch.Helper.exe'
$helperSource = 'src\DevSwitch.Helper\main.cpp'
$gpp = 'C:\Users\11714\scoop\apps\mingw\current\bin\g++.exe'

# NOTE: 通过真实 C++ 编译器构建 helper。这里不依赖 MSVC，M2 再迁移到正式 Win32/MSVC 项目。
& $gpp -std=c++20 -O2 -Wall -Wextra -static -static-libgcc -static-libstdc++ -o $helperOutput $helperSource
if (-not (Test-Path $helperOutput)) {
    throw "helper executable was not produced at $helperOutput"
}

# NOTE: 使用 I 盘 dotnet SDK 运行全量测试。
& 'I:\SoftWare\dotnet\dotnet.exe' test 'tests\DevSwitch.Tests\DevSwitch.Tests.csproj' --configuration Debug --verbosity minimal --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "dotnet test failed with exit code $LASTEXITCODE"
}
